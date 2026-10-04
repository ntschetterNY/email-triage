import CryptoKit
import Foundation
import os

enum CompanionError: LocalizedError {
    /// None of the PC's addresses answered.
    case unreachable(pcName: String)
    /// The PC no longer accepts this phone's token.
    case unpaired(String)
    /// The PC answered with an error to show.
    case server(status: Int, message: String)

    var errorDescription: String? {
        switch self {
        case .unreachable(let pc):
            return "Can't reach \(pc). Check that the PC is awake, Email Triage is running, "
                + "and \"Let my iPhone or iPad triage this inbox\" is on in its Settings. "
                + "Away from the PC's Wi-Fi, a relay has to be set up there too."
        case .unpaired(let message):
            return message
        case .server(_, let message):
            return message
        }
    }
}

/// Talks to the Email Triage companion on the PC over HTTPS, trusting only
/// the certificate whose fingerprint came in the pairing code, and sending
/// the pairing token with every request. When the PC can't be reached
/// directly and it has a relay set up, the same calls go sealed through that.
final class CompanionClient: NSObject, URLSessionDelegate {
    let pairing: PairingInfo

    /// Stands for the relay among the PC's addresses.
    private static let relayRoute = "relay"
    /// How long to wait on an address that hasn't answered before, when the relay is there to fall back to.
    private static let probeTimeout: TimeInterval = 5

    private let relay: RelayClient?

    /// The address that answered last, tried first next time; and the
    /// addresses whose certificate was not the pinned one. Calls run
    /// concurrently and the pinning callback on the session's queue, so both
    /// sit behind a lock.
    private struct Memory {
        var preferredHost: String?
        var wrongCertificate: Set<String> = []
    }
    private let memory = OSAllocatedUnfairLock(initialState: Memory())

    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.waitsForConnectivity = false
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()

    init(pairing: PairingInfo) {
        self.pairing = pairing
        self.relay = RelayClient(pairing: pairing)
    }

    // MARK: - API

    func hello() async throws -> Hello { try await call("GET", "/api/hello") }

    func inbox() async throws -> Inbox { try await call("GET", "/api/inbox") }

    func thread(_ refs: [Ref], dark: Bool) async throws -> ThreadPage {
        try await call("POST", "/api/thread", body: ThreadBody(refs: refs, dark: dark))
    }

    @discardableResult
    func archive(_ refs: [Ref]) async throws -> Done {
        try await call("POST", "/api/archive", body: RefsBody(refs: refs))
    }

    @discardableResult
    func move(_ refs: [Ref], to folder: Folder) async throws -> Done {
        try await call("POST", "/api/move", body: MoveBody(refs: refs, folder: folder))
    }

    func folders(matching query: String) async throws -> [Folder] {
        try await call("GET", "/api/folders", query: [URLQueryItem(name: "q", value: query)])
    }

    func snoozeOptions() async throws -> [SnoozeOption] { try await call("GET", "/api/snooze/options") }

    /// The time "tomorrow 9am" means on the PC, or nil when it isn't one.
    func parseSnooze(_ text: String) async throws -> SnoozeOption? {
        do {
            let option: SnoozeOption = try await call(
                "GET", "/api/snooze/parse", query: [URLQueryItem(name: "text", value: text)])
            return option
        } catch CompanionError.server(let status, _) where status == 422 {
            return nil
        }
    }

    @discardableResult
    func snooze(_ refs: [Ref], until when: Date) async throws -> Done {
        try await call("POST", "/api/snooze", body: SnoozeBody(refs: refs, when: when))
    }

    func setRead(_ refs: [Ref], read: Bool) async throws {
        let _: Empty = try await call("POST", "/api/read", body: ReadBody(refs: refs, read: read))
    }

    func setFlag(_ refs: [Ref], on: Bool) async throws {
        let _: Empty = try await call("POST", "/api/flag", body: FlagBody(refs: refs, on: on))
    }

    func reply(to ref: Ref, senderOnly: Bool, text: String, archive: [Ref]?) async throws {
        let body = ReplyBody(to: ref, scope: senderOnly ? "sender" : "all", text: text, archiveRefs: archive)
        let _: Empty = try await call("POST", "/api/reply", body: body)
    }

    // MARK: - Transport

    private func call<T: Decodable>(
        _ method: String, _ path: String, query: [URLQueryItem] = [], body: (any Encodable)? = nil
    ) async throws -> T {
        var hosts = pairing.hosts
        if relay != nil { hosts.append(Self.relayRoute) }
        let preferred = memory.withLock { $0.preferredHost }
        if let preferred, let index = hosts.firstIndex(of: preferred) {
            hosts.swapAt(0, index)
        }
        var wrongCertificate = false

        let payload = try body.map { try JSONEncoder.companion.encode($0) }

        for host in hosts {
            if host == Self.relayRoute, let relay {
                var components = URLComponents()
                components.path = path
                if !query.isEmpty { components.queryItems = query }
                let target = components.percentEncodedPath
                    + (components.percentEncodedQuery.map { "?" + $0 } ?? "")

                let answer: (status: Int, body: Data)
                do {
                    answer = try await relay.send(method: method, path: target, body: payload)
                } catch is RelayError {
                    try Task.checkCancellation()
                    continue
                }
                memory.withLock { $0.preferredHost = host }
                return try Self.decode(status: answer.status, data: answer.body)
            }

            var components = URLComponents()
            components.scheme = "https"
            components.host = host
            components.port = pairing.port
            components.path = path
            if !query.isEmpty { components.queryItems = query }
            guard let url = components.url else { continue }

            var request = URLRequest(url: url)
            request.httpMethod = method
            request.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            if let payload {
                request.httpBody = payload
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            }
            // Don't sit out the full timeout on an address that may be
            // unreachable from here when the relay could answer instead.
            if relay != nil, host != preferred { request.timeoutInterval = Self.probeTimeout }

            let data: Data
            let response: URLResponse
            do {
                (data, response) = try await session.data(for: request)
            } catch let error as URLError where Self.isUnreachable(error) {
                // The caller gave up: stop, rather than try every other address
                // and report the PC unreachable.
                try Task.checkCancellation()
                if memory.withLock({ $0.wrongCertificate.remove(host) != nil }) { wrongCertificate = true }
                continue
            }

            memory.withLock { $0.preferredHost = host }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            return try Self.decode(status: status, data: data)
        }

        // Something answered, but not with the PC's pinned certificate: the
        // PC made a new pairing code ("Unpair all devices"), so pair again.
        if wrongCertificate {
            throw CompanionError.unpaired(
                "\(pairing.pcName) has a new pairing code. Pair this device again from Settings on the PC.")
        }
        throw CompanionError.unreachable(pcName: pairing.pcName)
    }

    /// The PC's answer, the same whichever way it came.
    private static func decode<T: Decodable>(status: Int, data: Data) throws -> T {
        if (200..<300).contains(status) {
            if status == 204 || data.isEmpty, let empty = Empty() as? T { return empty }
            return try JSONDecoder.companion.decode(T.self, from: data)
        }

        let message = (try? JSONDecoder.companion.decode(APIErrorBody.self, from: data))?.error
            ?? "The PC answered \(status)."
        if status == 401 { throw CompanionError.unpaired(message) }
        throw CompanionError.server(status: status, message: message)
    }

    private static func isUnreachable(_ error: URLError) -> Bool {
        switch error.code {
        case .cannotConnectToHost, .cannotFindHost, .timedOut, .networkConnectionLost,
             .notConnectedToInternet, .dnsLookupFailed, .secureConnectionFailed,
             .serverCertificateUntrusted, .cancelled:
            return true
        default:
            return false
        }
    }

    // MARK: - Certificate pinning

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first
        else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        let der = SecCertificateCopyData(leaf) as Data
        let fingerprint = SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()

        if fingerprint == pairing.fingerprint {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            // Not the PC this phone was paired with - whatever else it is.
            let host = challenge.protectionSpace.host
            memory.withLock { _ = $0.wrongCertificate.insert(host) }
            completionHandler(.cancelAuthenticationChallenge, nil)
        }
    }
}

// MARK: - Request bodies

private struct RefsBody: Encodable { let refs: [Ref] }
private struct ThreadBody: Encodable { let refs: [Ref]; let dark: Bool }
private struct MoveBody: Encodable { let refs: [Ref]; let folder: Folder }
private struct SnoozeBody: Encodable { let refs: [Ref]; let when: Date }
private struct ReadBody: Encodable { let refs: [Ref]; let read: Bool }
private struct FlagBody: Encodable { let refs: [Ref]; let on: Bool }
private struct ReplyBody: Encodable {
    let to: Ref
    let scope: String
    let text: String
    let archiveRefs: [Ref]?
}
