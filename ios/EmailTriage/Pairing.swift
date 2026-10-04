import Foundation
import Security

/// What the PC's pairing code carries: where the PC is, the token to send,
/// and the fingerprint of the certificate to trust. Nothing else is trusted.
/// When the PC has a relay set up, also where it is and the key that seals
/// everything sent through it.
struct PairingInfo: Codable, Equatable {
    let hosts: [String]
    let port: Int
    let token: String
    /// SHA-256 of the PC's certificate, lower-case hex.
    let fingerprint: String
    let pcName: String
    /// The relay's Supabase project, e.g. https://abcd.supabase.co. Absent
    /// in pairings made before the relay, which decode with these nil.
    let relayURL: URL?
    /// That project's publishable key.
    let relayAPIKey: String?
    /// The 256-bit AES key the PC and this device seal relay traffic with.
    let relayKey: Data?

    var hasRelay: Bool { relayURL != nil && relayAPIKey != nil && relayKey != nil }

    /// Reads `emailtriage://pair?h=192.168.1.20,10.0.0.5&p=47821&t=...&f=...&n=PC`,
    /// with `&r=https://...&a=...&k=...` when there is a relay.
    init?(link: String) {
        guard let components = URLComponents(string: link.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme?.lowercased() == "emailtriage",
              components.host?.lowercased() == "pair"
        else { return nil }

        func value(_ name: String) -> String? {
            components.queryItems?.first(where: { $0.name == name })?.value
        }

        let hosts = (value("h") ?? "")
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        let relayURL = value("r").flatMap(URL.init(string:)).flatMap { $0.scheme == "https" ? $0 : nil }
        let relayAPIKey = value("a").flatMap { $0.isEmpty ? nil : $0 }
        let relayKey = value("k").flatMap(Data.init(base64URL:)).flatMap { $0.count == 32 ? $0 : nil }
        let hasRelay = relayURL != nil && relayAPIKey != nil && relayKey != nil

        guard !hosts.isEmpty || hasRelay,
              let port = Int(value("p") ?? ""), (1...65535).contains(port),
              let token = value("t"), !token.isEmpty,
              let fingerprint = value("f")?.lowercased(),
              fingerprint.count == 64, fingerprint.allSatisfy(\.isHexDigit)
        else { return nil }

        self.hosts = hosts
        self.port = port
        self.token = token
        self.fingerprint = fingerprint
        self.pcName = value("n") ?? "your PC"
        self.relayURL = hasRelay ? relayURL : nil
        self.relayAPIKey = hasRelay ? relayAPIKey : nil
        self.relayKey = hasRelay ? relayKey : nil
    }
}

extension Data {
    /// Base64url without padding, as the PC writes keys into links.
    init?(base64URL text: String) {
        var base64 = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        self.init(base64Encoded: base64)
    }
}

/// Keeps the pairing in the Keychain, on this device only.
enum PairingStore {
    private static let service = "EmailTriage.pairing"
    private static let account = "pc"

    static func load() -> PairingInfo? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data
        else { return nil }
        return try? JSONDecoder().decode(PairingInfo.self, from: data)
    }

    static func save(_ pairing: PairingInfo) {
        clear()
        guard let data = try? JSONEncoder().encode(pairing) else { return }
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: data,
        ]
        SecItemAdd(item as CFDictionary, nil)
    }

    static func clear() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
