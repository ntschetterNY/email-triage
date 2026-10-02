import SwiftUI

/// The triage list: swipe right to archive, left to snooze or flag, tap to read.
struct InboxView: View {
    @Bindable var model: InboxModel
    @Environment(AppState.self) private var state
    @Environment(\.scenePhase) private var scenePhase

    @State private var path: [Conversation] = []
    @State private var snoozing: Conversation?
    @State private var moving: Conversation?
    @State private var confirmUnpair = false

    var body: some View {
        NavigationStack(path: $path) {
            list
                .navigationTitle("Inbox")
                .toolbar { toolbar }
                .navigationDestination(for: Conversation.self) { c in
                    ThreadView(model: model, conversation: c)
                }
                .refreshable { await model.refresh() }
                .overlay { emptyState }
                .safeAreaInset(edge: .bottom) { banner }
        }
        .task { await model.refresh() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await model.refresh() } }
        }
        .sheet(item: $snoozing) { c in
            SnoozeSheet(client: model.client) { when in
                snoozing = nil
                Task { await model.snooze(c, until: when) }
            }
        }
        .sheet(item: $moving) { c in
            MoveSheet(client: model.client) { folder in
                moving = nil
                Task { await model.move(c, to: folder) }
            }
        }
        .confirmationDialog("Unpair from \(state.pairing?.pcName ?? "the PC")?", isPresented: $confirmUnpair, titleVisibility: .visible) {
            Button("Unpair", role: .destructive) { state.unpair() }
        } message: {
            Text("You'll need to scan the code on the PC again to use the app.")
        }
    }

    private var list: some View {
        List(model.visible) { c in
            NavigationLink(value: c) {
                ConversationRow(conversation: c)
            }
            .swipeActions(edge: .leading, allowsFullSwipe: true) {
                Button {
                    Task { await model.archive(c) }
                } label: {
                    Label("Archive", systemImage: "archivebox")
                }
                .tint(.green)
            }
            .swipeActions(edge: .trailing, allowsFullSwipe: true) {
                Button {
                    snoozing = c
                } label: {
                    Label("Snooze", systemImage: "clock")
                }
                .tint(.orange)

                Button {
                    Task { await model.setFlag(c, on: !c.flagged) }
                } label: {
                    Label(c.flagged ? "Unflag" : "Flag", systemImage: c.flagged ? "flag.slash" : "flag")
                }
                .tint(.red)

                Button {
                    moving = c
                } label: {
                    Label("Move", systemImage: "folder")
                }
                .tint(.indigo)
            }
            .contextMenu {
                Button(c.unread ? "Mark as read" : "Mark as unread",
                       systemImage: c.unread ? "envelope.open" : "envelope.badge") {
                    Task { await model.setRead(c, read: c.unread) }
                }
            }
        }
        .listStyle(.plain)
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            Menu {
                Toggle(isOn: $model.showUnreadOnly) {
                    Label("Unread only", systemImage: "envelope.badge")
                }
                Divider()
                if let pc = state.pairing?.pcName {
                    Text("Paired with \(pc)")
                }
                Button("Unpair...", systemImage: "xmark.circle", role: .destructive) {
                    confirmUnpair = true
                }
            } label: {
                Image(systemName: model.showUnreadOnly
                      ? "line.3.horizontal.decrease.circle.fill"
                      : "line.3.horizontal.decrease.circle")
            }
        }
    }

    @ViewBuilder
    private var emptyState: some View {
        if model.visible.isEmpty {
            if !model.hasLoaded && model.isLoading {
                ProgressView("Reaching \(state.pairing?.pcName ?? "your PC")...")
            } else if model.hasLoaded {
                ContentUnavailableView(
                    model.showUnreadOnly ? "Nothing unread" : "Inbox zero",
                    systemImage: "checkmark.circle",
                    description: Text("Pull down to check again."))
            } else if model.error != nil {
                ContentUnavailableView {
                    Label("Can't load the inbox", systemImage: "wifi.exclamationmark")
                } description: {
                    Text(model.error ?? "")
                } actions: {
                    Button("Try again") { Task { await model.refresh() } }
                    if model.isUnpaired {
                        Button("Pair again") { state.unpair() }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var banner: some View {
        if let text = model.error, model.hasLoaded {
            Banner(text: text, isError: true) { model.error = nil }
        } else if let text = model.notice {
            Banner(text: text, isError: false) { model.notice = nil }
                .task(id: text) {
                    try? await Task.sleep(for: .seconds(2.5))
                    if model.notice == text { model.notice = nil }
                }
        }
    }
}

struct ConversationRow: View {
    let conversation: Conversation

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Circle()
                .fill(conversation.unread ? Color.accentColor : .clear)
                .frame(width: 9, height: 9)
                .padding(.top, 6)

            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(conversation.from)
                        .font(.body.weight(conversation.unread ? .semibold : .regular))
                        .lineLimit(1)
                    if conversation.count > 1 {
                        Text("\(conversation.count)")
                            .font(.caption.weight(.medium))
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 6)
                    Text(conversation.lastActivity, format: Self.dateStyle(for: conversation.lastActivity))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                HStack(spacing: 5) {
                    if conversation.isMeeting {
                        Image(systemName: "calendar")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if conversation.latestIsMine {
                        Image(systemName: "arrowshape.turn.up.left.fill")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                    Text(conversation.displaySubject)
                        .font(.subheadline)
                        .foregroundStyle(conversation.unread ? .primary : .secondary)
                        .lineLimit(2)
                    Spacer(minLength: 0)
                    if conversation.attachments {
                        Image(systemName: "paperclip")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if conversation.flagged {
                        Image(systemName: "flag.fill")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
            }
        }
        .padding(.vertical, 3)
    }

    /// Today shows the time; this week the weekday; older the date.
    static func dateStyle(for date: Date) -> Date.FormatStyle {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) { return .dateTime.hour().minute() }
        if let days = calendar.dateComponents([.day], from: date, to: Date()).day, days < 6 {
            return .dateTime.weekday(.abbreviated)
        }
        return .dateTime.day().month(.abbreviated)
    }
}

struct Banner: View {
    let text: String
    let isError: Bool
    let onClose: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .foregroundStyle(isError ? .orange : .green)
            Text(text)
                .font(.callout)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.caption.bold())
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .padding(.horizontal, 12)
        .padding(.bottom, 6)
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }
}
