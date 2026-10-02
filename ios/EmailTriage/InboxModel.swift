import Foundation
import Observation

/// Which PC this phone is paired with, if any.
@MainActor
@Observable
final class AppState {
    private(set) var pairing: PairingInfo?
    private(set) var inbox: InboxModel?

    init() {
        if let saved = PairingStore.load() { use(saved) }
    }

    /// Pairs from a scanned or pasted link; false when it isn't a pairing link.
    @discardableResult
    func pair(with link: String) -> Bool {
        guard let info = PairingInfo(link: link) else { return false }
        PairingStore.save(info)
        use(info)
        return true
    }

    func unpair() {
        PairingStore.clear()
        pairing = nil
        inbox = nil
    }

    private func use(_ info: PairingInfo) {
        pairing = info
        inbox = InboxModel(client: CompanionClient(pairing: info))
    }
}

/// The inbox as the phone shows it. Triage moves take the conversation out
/// of the list at once and put it back if the PC refuses.
@MainActor
@Observable
final class InboxModel {
    let client: CompanionClient

    private(set) var conversations: [Conversation] = []
    private(set) var isLoading = false
    private(set) var hasLoaded = false
    private(set) var lastUpdated: Date?

    /// A problem to show over the list.
    var error: String?
    /// A short note of what just happened.
    var notice: String?
    /// True once the PC has said this phone is no longer paired.
    private(set) var isUnpaired = false

    var showUnreadOnly = false

    init(client: CompanionClient) {
        self.client = client
    }

    var visible: [Conversation] {
        showUnreadOnly ? conversations.filter(\.unread) : conversations
    }

    func refresh() async {
        guard !isLoading else { return }
        isLoading = true
        defer { isLoading = false }

        do {
            let inbox = try await client.inbox()
            conversations = inbox.conversations
            lastUpdated = Date()
            hasLoaded = true
            error = nil
        } catch {
            report(error)
        }
    }

    func archive(_ c: Conversation) async {
        await takeOut(c, done: "Archived") { try await self.client.archive(c.inbox) }
    }

    func snooze(_ c: Conversation, until when: Date) async {
        let label = when.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated).hour().minute())
        await takeOut(c, done: "Snoozed until \(label)") { try await self.client.snooze(c.inbox, until: when) }
    }

    func move(_ c: Conversation, to folder: Folder) async {
        await takeOut(c, done: "Moved to \(folder.name)") { try await self.client.move(c.inbox, to: folder) }
    }

    func setFlag(_ c: Conversation, on: Bool) async {
        update(c.key) { $0.flagged = on }
        do {
            try await client.setFlag(c.inbox, on: on)
            notice = on ? "Flagged for action" : "Flag cleared"
        } catch {
            update(c.key) { $0.flagged = !on }
            report(error)
        }
    }

    func setRead(_ c: Conversation, read: Bool) async {
        guard c.unread == read else { return }
        update(c.key) { $0.unread = !read }
        do {
            try await client.setRead(c.inbox, read: read)
        } catch {
            update(c.key) { $0.unread = read }
            report(error)
        }
    }

    /// Sends a reply; with `archiveAfter`, the conversation leaves the list once it has gone.
    func reply(to c: Conversation, senderOnly: Bool, text: String, archiveAfter: Bool) async throws {
        try await client.reply(to: c.replyTo, senderOnly: senderOnly, text: text,
                               archive: archiveAfter ? c.inbox : nil)
        if archiveAfter { conversations.removeAll { $0.key == c.key } }
        notice = archiveAfter ? "Sent and archived" : "Sent"
    }

    // MARK: -

    private func takeOut(_ c: Conversation, done: String, action: @escaping () async throws -> Done) async {
        guard let index = conversations.firstIndex(where: { $0.key == c.key }) else { return }
        let removed = conversations.remove(at: index)

        do {
            _ = try await action()
            notice = done
        } catch {
            conversations.insert(removed, at: min(index, conversations.count))
            report(error)
        }
    }

    private func update(_ key: String, _ change: (inout Conversation) -> Void) {
        guard let index = conversations.firstIndex(where: { $0.key == key }) else { return }
        change(&conversations[index])
    }

    private func report(_ error: Error) {
        if case CompanionError.unpaired = error { isUnpaired = true }
        self.error = error.localizedDescription
    }
}
