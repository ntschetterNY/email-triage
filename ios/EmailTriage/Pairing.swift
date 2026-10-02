import Foundation
import Security

/// What the PC's pairing code carries: where the PC is, the token to send,
/// and the fingerprint of the certificate to trust. Nothing else is trusted.
struct PairingInfo: Codable, Equatable {
    let hosts: [String]
    let port: Int
    let token: String
    /// SHA-256 of the PC's certificate, lower-case hex.
    let fingerprint: String
    let pcName: String

    /// Reads `emailtriage://pair?h=192.168.1.20,10.0.0.5&p=47821&t=...&f=...&n=PC`.
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
        guard !hosts.isEmpty,
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
