import SwiftUI

/// One conversation, rendered on the PC exactly as the desktop reading pane
/// shows it, with the triage moves along the bottom.
struct ThreadView: View {
    let model: InboxModel
    let conversation: Conversation

    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme

    @State private var page: ThreadPage?
    @State private var loadError: String?
    @State private var flagged: Bool
    @State private var snoozing = false
    @State private var moving = false
    @State private var replying: ReplyMode?

    init(model: InboxModel, conversation: Conversation) {
        self.model = model
        self.conversation = conversation
        _flagged = State(initialValue: conversation.flagged)
    }

    var body: some View {
        content
            .navigationTitle(conversation.displaySubject)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { bottomBar }
            .task(id: colorScheme) { await load() }
            .sheet(isPresented: $snoozing) {
                SnoozeSheet(client: model.client) { when in
                    snoozing = false
                    leave { await model.snooze(conversation, until: when) }
                }
            }
            .sheet(isPresented: $moving) {
                MoveSheet(client: model.client) { folder in
                    moving = false
                    leave { await model.move(conversation, to: folder) }
                }
            }
            .sheet(item: $replying) { mode in
                ReplySheet(model: model, conversation: conversation, senderOnly: mode == .sender) { archived in
                    replying = nil
                    if archived { dismiss() }
                }
            }
    }

    @ViewBuilder
    private var content: some View {
        if let page {
            MailWebView(html: page.html)
                .ignoresSafeArea(edges: .bottom)
        } else if let loadError {
            ContentUnavailableView {
                Label("Can't open this conversation", systemImage: "envelope.badge.shield.half.filled")
            } description: {
                Text(loadError)
            } actions: {
                Button("Try again") { Task { await load() } }
            }
        } else {
            ProgressView()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    @ToolbarContentBuilder
    private var bottomBar: some ToolbarContent {
        ToolbarItemGroup(placement: .bottomBar) {
            Button {
                leave { await model.archive(conversation) }
            } label: {
                Label("Archive", systemImage: "archivebox")
            }

            Spacer()

            Button {
                snoozing = true
            } label: {
                Label("Snooze", systemImage: "clock")
            }

            Spacer()

            Button {
                moving = true
            } label: {
                Label("Move", systemImage: "folder")
            }

            Spacer()

            Button {
                flagged.toggle()
                let on = flagged
                Task { await model.setFlag(conversation, on: on) }
            } label: {
                Label(flagged ? "Unflag" : "Flag", systemImage: flagged ? "flag.fill" : "flag")
            }
            .tint(flagged ? .red : nil)

            Spacer()

            Menu {
                Button("Reply all", systemImage: "arrowshape.turn.up.left.2") { replying = .all }
                Button("Reply to sender", systemImage: "arrowshape.turn.up.left") { replying = .sender }
            } label: {
                Label("Reply", systemImage: "arrowshape.turn.up.left")
            } primaryAction: {
                replying = .all
            }
        }
    }

    private func load() async {
        do {
            page = try await model.client.thread(conversation.messages, dark: colorScheme == .dark)
            loadError = nil
            if conversation.unread { await model.setRead(conversation, read: true) }
        } catch {
            if page == nil { loadError = error.localizedDescription }
        }
    }

    /// Back to the list straight away; the move finishes, or is undone with a message, there.
    private func leave(_ action: @escaping () async -> Void) {
        dismiss()
        Task { await action() }
    }
}

enum ReplyMode: String, Identifiable {
    case all, sender
    var id: String { rawValue }
}
