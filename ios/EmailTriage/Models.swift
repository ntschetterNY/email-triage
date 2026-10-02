import Foundation

// The companion API's shapes, as the PC sends them (camelCase JSON).
// See src/EmailTriage.Companion/Dtos.cs.

/// A mail item as Outlook knows it: EntryId and StoreId.
struct Ref: Codable, Hashable {
    let e: String
    let s: String
}

struct Conversation: Codable, Identifiable, Hashable {
    let key: String
    let subject: String
    let from: String
    let fromAddress: String
    let lastActivity: Date
    var unread: Bool
    let attachments: Bool
    var flagged: Bool
    let count: Int
    /// "mail", or a meeting kind such as "meetingRequest".
    let kind: String
    let latestIsMine: Bool
    /// The message a reply answers.
    let replyTo: Ref
    /// What archive, move, snooze, read and flag act on.
    let inbox: [Ref]
    /// Every message to show when reading, newest first.
    let messages: [Ref]

    var id: String { key }

    var isMeeting: Bool { kind != "mail" }

    var displaySubject: String { subject.isEmpty ? "(no subject)" : subject }
}

struct Inbox: Codable {
    let conversations: [Conversation]
    let asOf: Date
}

struct ThreadPage: Codable {
    let html: String
    let shown: Int
    let hidden: Int
}

struct Folder: Codable, Identifiable, Hashable {
    let e: String
    let s: String
    let path: String
    let name: String
    let breadcrumb: String

    var id: String { e }
}

struct SnoozeOption: Codable, Identifiable, Hashable {
    let label: String
    let hint: String
    let when: Date

    var id: String { label + when.description }
}

struct Hello: Codable {
    let pcName: String
    let version: String
    let actionCategory: String
}

struct APIErrorBody: Codable {
    let error: String
}

struct Done: Codable {
    let done: Int
}

/// For calls the PC answers with 204 No Content.
struct Empty: Codable {}

extension JSONDecoder {
    /// .NET writes DateTimeOffset as ISO 8601 with an offset, and with up to
    /// seven fractional digits when there are any.
    static let companion: JSONDecoder = {
        let decoder = JSONDecoder()
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let whole = ISO8601DateFormatter()
        whole.formatOptions = [.withInternetDateTime]

        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            if let date = fractional.date(from: text) ?? whole.date(from: text) { return date }

            // More than three fractional digits: trim them and try again.
            if let dot = text.firstIndex(of: "."),
               let zone = text[dot...].firstIndex(where: { $0 == "+" || $0 == "-" || $0 == "Z" }) {
                let digits = text[text.index(after: dot)..<zone].prefix(3)
                let trimmed = String(text[..<dot]) + "." + digits + String(text[zone...])
                if let date = fractional.date(from: trimmed) { return date }
            }
            throw DecodingError.dataCorrupted(.init(
                codingPath: decoder.codingPath, debugDescription: "Unreadable date \(text)"))
        }
        return decoder
    }()
}

extension JSONEncoder {
    static let companion: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()
}
