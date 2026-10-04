import CryptoKit
import Foundation

enum RelayError: Error {
    /// Couldn't reach the relay, or the connection dropped mid-call.
    case unreachable
    /// The relay took the request but the PC never answered: it's asleep,
    /// offline, or Email Triage isn't running.
    case noAnswer
}

/// The device's end of the relay, for when the PC can't be reached directly
/// (a Public network on the PC, guest Wi-Fi, or away from the office). Both
/// ends connect out to a Supabase Realtime channel. Every request is sealed
/// with AES-GCM under the key from the pairing code, so the relay carries
/// only ciphertext. The PC opens it, asks its own HTTPS server, and seals
/// the answer back. Mirrors `RelaySeal` and `RealtimeChannel` on the PC.
actor RelayClient {
    private static let chunkSize = 160 * 1024
    private static let answerTimeout: Duration = .seconds(45)
    private static let heartbeatEvery: Duration = .seconds(25)
    /// No frame for this long and the socket is presumed dead, as it is after the app was in the background.
    private static let staleAfter: TimeInterval = 35
    private static let requestLabel = Data("email-triage relay request".utf8)
    private static let responseLabel = Data("email-triage relay response".utf8)

    private let socketURL: URL
    private let topic: String
    private let key: SymmetricKey
    private let session = URLSession(configuration: .ephemeral)

    private var socket: URLSessionWebSocketTask?
    private var joining: Task<URLSessionWebSocketTask, Error>?
    private var joinRef = ""
    private var ref = 0
    private var lastHeard = Date.distantPast
    private var heartbeat: Task<Void, Never>?
    private var waiting: [String: CheckedContinuation<(status: Int, body: Data), Error>] = [:]
    private var parts: [String: [String?]] = [:]

    init?(pairing: PairingInfo) {
        guard let url = pairing.relayURL, let apiKey = pairing.relayAPIKey, let key = pairing.relayKey,
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
        else { return nil }
        components.scheme = "wss"
        components.path = "/realtime/v1/websocket"
        components.queryItems = [URLQueryItem(name: "apikey", value: apiKey), URLQueryItem(name: "vsn", value: "1.0.0")]
        guard let socketURL = components.url else { return nil }

        self.socketURL = socketURL
        self.key = SymmetricKey(data: key)
        let digest = SHA256.hash(data: key + Data("email-triage relay channel".utf8))
        self.topic = "realtime:email-triage-" + String(digest.map { String(format: "%02x", $0) }.joined().prefix(40))
    }

    /// Sends one API call through the relay; the status and body the PC's server answered.
    func send(method: String, path: String, body: Data?) async throws -> (status: Int, body: Data) {
        let id = UUID().uuidString
        let request = Request(
            id: id, at: Int64(Date().timeIntervalSince1970 * 1000), method: method, path: path,
            body: body.map { String(decoding: $0, as: UTF8.self) })
        let sealed = try seal(try JSONEncoder().encode(request), label: Self.requestLabel)
        let ws = try await connection()

        return try await withCheckedThrowingContinuation { continuation in
            Task { await self.start(id: id, sealed: sealed, on: ws, answer: continuation) }
        }
    }

    // MARK: - Calls

    private func start(
        id: String, sealed: String, on ws: URLSessionWebSocketTask,
        answer: CheckedContinuation<(status: Int, body: Data), Error>
    ) async {
        waiting[id] = answer

        Task {
            try? await Task.sleep(for: Self.answerTimeout)
            self.finish(id, with: .failure(RelayError.noAnswer))
        }

        do {
            for chunk in chunks(id: id, sealed: sealed) {
                try await sendFrame([
                    "topic": topic, "event": "broadcast", "ref": nextRef(), "join_ref": joinRef,
                    "payload": ["type": "broadcast", "event": "req", "payload": chunk],
                ], on: ws)
            }
        } catch {
            finish(id, with: .failure(RelayError.unreachable))
        }
    }

    private func finish(_ id: String, with result: Result<(status: Int, body: Data), Error>) {
        parts[id] = nil
        waiting.removeValue(forKey: id)?.resume(with: result)
    }

    private func chunks(id: String, sealed: String) -> [[String: Any]] {
        let n = max(1, (sealed.count + Self.chunkSize - 1) / Self.chunkSize)
        var start = sealed.startIndex
        return (0..<n).map { i in
            let end = sealed.index(start, offsetBy: Self.chunkSize, limitedBy: sealed.endIndex) ?? sealed.endIndex
            defer { start = end }
            return ["id": id, "i": i, "n": n, "d": String(sealed[start..<end])]
        }
    }

    /// A broadcast from the PC: one chunk of an answer.
    private func received(_ chunk: [String: Any]) {
        guard let id = chunk["id"] as? String, waiting[id] != nil,
              let i = chunk["i"] as? Int, let n = chunk["n"] as? Int, let d = chunk["d"] as? String,
              n >= 1, n <= 200, (0..<n).contains(i)
        else { return }

        var have = parts[id] ?? Array(repeating: nil, count: n)
        guard have.count == n else { return }
        have[i] = d
        if have.contains(where: { $0 == nil }) {
            parts[id] = have
            return
        }

        do {
            let plain = try open(have.compactMap { $0 }.joined(), label: Self.responseLabel)
            let response = try JSONDecoder().decode(Response.self, from: plain)
            finish(id, with: .success((response.status, Data(response.body.utf8))))
        } catch {
            // Not sealed by the PC this device is paired with: keep waiting.
            parts[id] = nil
        }
    }

    // MARK: - Sealing

    private func seal(_ plain: Data, label: Data) throws -> String {
        let compressed = try (plain as NSData).compressed(using: .zlib) as Data
        guard let combined = try AES.GCM.seal(compressed, using: key, authenticating: label).combined else {
            throw RelayError.unreachable
        }
        return combined.base64EncodedString()
    }

    private func open(_ sealed: String, label: Data) throws -> Data {
        guard let combined = Data(base64Encoded: sealed) else { throw CryptoKitError.authenticationFailure }
        let compressed = try AES.GCM.open(AES.GCM.SealedBox(combined: combined), using: key, authenticating: label)
        return try (compressed as NSData).decompressed(using: .zlib) as Data
    }

    // MARK: - Connection

    /// The joined channel, connecting first when there isn't one or it has gone quiet.
    private func connection() async throws -> URLSessionWebSocketTask {
        if let socket, Date().timeIntervalSince(lastHeard) < Self.staleAfter { return socket }
        if let joining { return try await joining.value }

        drop(socket)
        let task = Task { try await self.join() }
        joining = task
        defer { joining = nil }
        do {
            return try await task.value
        } catch {
            throw RelayError.unreachable
        }
    }

    private func join() async throws -> URLSessionWebSocketTask {
        let ws = session.webSocketTask(with: socketURL)
        ws.maximumMessageSize = 4 * 1024 * 1024
        ws.resume()

        let timeout = Task {
            try await Task.sleep(for: .seconds(15))
            ws.cancel(with: .goingAway, reason: nil)
        }
        defer { timeout.cancel() }

        joinRef = nextRef()
        try await sendFrame([
            "topic": topic, "event": "phx_join", "ref": joinRef, "join_ref": joinRef,
            "payload": ["config": [
                "broadcast": ["ack": false, "self": false],
                "presence": ["key": "", "enabled": false],
                "postgres_changes": [Any](),
                "private": false,
            ]],
        ], on: ws)

        while true {
            let frame = try await Self.frame(try await ws.receive())
            guard frame["event"] as? String == "phx_reply", frame["ref"] as? String == joinRef else { continue }
            let payload = frame["payload"] as? [String: Any]
            guard payload?["status"] as? String == "ok" else { throw RelayError.unreachable }
            break
        }

        socket = ws
        lastHeard = Date()
        listen(on: ws)
        heartbeat = Task { await self.beat(on: ws) }
        return ws
    }

    private func listen(on ws: URLSessionWebSocketTask) {
        Task {
            while true {
                do {
                    let frame = try await Self.frame(try await ws.receive())
                    self.handle(frame, from: ws)
                } catch {
                    self.drop(ws)
                    return
                }
            }
        }
    }

    private func handle(_ frame: [String: Any], from ws: URLSessionWebSocketTask) {
        guard ws === socket else { return }
        lastHeard = Date()

        switch frame["event"] as? String {
        case "broadcast" where frame["topic"] as? String == topic:
            if let outer = frame["payload"] as? [String: Any], outer["event"] as? String == "res",
               let chunk = outer["payload"] as? [String: Any] {
                received(chunk)
            }
        case "phx_error", "phx_close":
            if frame["topic"] as? String == topic { drop(ws) }
        default:
            break
        }
    }

    private func beat(on ws: URLSessionWebSocketTask) async {
        while !Task.isCancelled {
            try? await Task.sleep(for: Self.heartbeatEvery)
            guard ws === socket else { return }
            try? await sendFrame(["topic": "phoenix", "event": "heartbeat", "payload": [String: Any](), "ref": nextRef()], on: ws)
        }
    }

    /// Forgets a dead connection; calls waiting on it fail, and the next call reconnects.
    private func drop(_ ws: URLSessionWebSocketTask?) {
        guard let ws, ws === socket else { return }
        socket = nil
        heartbeat?.cancel()
        heartbeat = nil
        ws.cancel(with: .goingAway, reason: nil)
        for id in Array(waiting.keys) { finish(id, with: .failure(RelayError.unreachable)) }
    }

    private func sendFrame(_ frame: [String: Any], on ws: URLSessionWebSocketTask) async throws {
        let data = try JSONSerialization.data(withJSONObject: frame)
        try await ws.send(.string(String(decoding: data, as: UTF8.self)))
    }

    private static func frame(_ message: URLSessionWebSocketTask.Message) throws -> [String: Any] {
        let data: Data
        switch message {
        case .string(let text): data = Data(text.utf8)
        case .data(let bytes): data = bytes
        @unknown default: return [:]
        }
        return (try JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    private func nextRef() -> String {
        ref += 1
        return String(ref)
    }

    private struct Request: Encodable {
        let id: String
        let at: Int64
        let method: String
        let path: String
        let body: String?
    }

    private struct Response: Decodable {
        let id: String
        let status: Int
        let body: String
    }
}
