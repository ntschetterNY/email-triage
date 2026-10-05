import SwiftUI

/// One conversation, rendered on the PC exactly as the desktop reading pane
/// shows it, with the triage moves along the bottom. With a hardware
/// keyboard (iPad) the desktop's keys work: e archive, h snooze, v move,
/// a flag, r reply all, Shift+R reply to sender, j / k next and previous,
/// Cmd+Shift+L feedback.
struct ThreadView: View {
    let model: InboxModel
    let conversation: Conversation
    /// The conversation is leaving the list: the inbox opens the next one, or goes back.
    var onLeave: () -> Void = {}
    /// j / k: move through the list by this many.
    var onStep: (Int) -> Void = { _ in }

    @Environment(\.colorScheme) private var colorScheme

    @State private var page: ThreadPage?
    @State private var loadError: String?
    @State private var flagged: Bool
    @State private var snoozing = false
    @State private var moving = false
    @State private var replying: ReplyMode?
    @State private var givingFeedback = false

    init(model: InboxModel, conversation: Conversation,
         onLeave: @escaping () -> Void = {}, onStep: @escaping (Int) -> Void = { _ in }) {
        self.model = model
        self.conversation = conversation
        self.onLeave = onLeave
        self.onStep = onStep
        _flagged = State(initialValue: conversation.flagged)
    }

    var body: some View {
        content
            .navigationTitle(conversation.displaySubject)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { bottomBar }
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        givingFeedback = true
                    } label: {
                        Label("Feedback", systemImage: "exclamationmark.bubble")
                    }
                }
            }
            .background { keyboardShortcuts }
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
            .sheet(isPresented: $givingFeedback) {
                FeedbackSheet(client: model.client, screen: .conversation)
            }
            .sheet(item: $replying) { mode in
                ReplySheet(model: model, conversation: conversation, senderOnly: mode == .sender) { archived in
                    replying = nil
                    if archived { onLeave() }
                }
            }
    }

    @ViewBuilder
    private var content: some View {
        if let page {
            MailWebView(html: page.html)
                .ignoresSafeArea(edges: .bottom)
                .safeAreaInset(edge: .top, spacing: 0) {
                    if let files = page.attachments, !files.isEmpty {
                        AttachmentStrip(client: model.client, attachments: files)
                            .id(conversation.key)
                    }
                }
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

            Button(action: toggleFlag) {
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

    private func toggleFlag() {
        flagged.toggle()
        let on = flagged
        Task { await model.setFlag(conversation, on: on) }
    }

    /// On to the next conversation straight away; the move finishes, or is
    /// undone with a message, in the list.
    private func leave(_ action: @escaping () async -> Void) {
        onLeave()
        Task { await action() }
    }

    /// Invisible buttons that carry the desktop's single-key shortcuts. They
    /// only fire while this view has the keyboard, so typing in a sheet is safe.
    private var keyboardShortcuts: some View {
        ZStack {
            Button("Archive") { leave { await model.archive(conversation) } }
                .keyboardShortcut("e", modifiers: [])
            Button("Snooze") { snoozing = true }
                .keyboardShortcut("h", modifiers: [])
            Button("Move") { moving = true }
                .keyboardShortcut("v", modifiers: [])
            Button("Flag", action: toggleFlag)
                .keyboardShortcut("a", modifiers: [])
            Button("Reply all") { replying = .all }
                .keyboardShortcut("r", modifiers: [])
            Button("Reply to sender") { replying = .sender }
                .keyboardShortcut("r", modifiers: .shift)
            Button("Next") { onStep(1) }
                .keyboardShortcut("j", modifiers: [])
            Button("Previous") { onStep(-1) }
                .keyboardShortcut("k", modifiers: [])
            Button("Feedback") { givingFeedback = true }
                .keyboardShortcut("l", modifiers: [.command, .shift])
        }
        .opacity(0)
        .accessibilityHidden(true)
    }
}

enum ReplyMode: String, Identifiable {
    case all, sender
    var id: String { rawValue }
}
