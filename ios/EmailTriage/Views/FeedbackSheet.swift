import SwiftUI
import UIKit

/// Lavish, from the device: say what should change about a screen and the
/// PC files it as a GitHub issue with its own sign-in, then follows it
/// through branch, pull request, merge and release. Only the comment and the
/// screen's name are sent, never what the mail says.
struct FeedbackSheet: View {
    let client: CompanionClient

    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL

    @State private var about: FeedbackScreen
    @State private var comment = ""
    @State private var sending = false
    @State private var result: FeedbackResult?
    @State private var problem: String?
    @State private var notes: [FeedbackNote] = []
    @State private var loadingNotes = true
    @FocusState private var writing: Bool

    /// `screen`: the one it was opened from, picked to start with.
    init(client: CompanionClient, screen: FeedbackScreen = .inbox) {
        self.client = client
        _about = State(initialValue: screen)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("About", selection: $about) {
                        ForEach(FeedbackScreen.allCases) { s in
                            Label(s.title, systemImage: s.symbol).tag(s)
                        }
                    }
                    TextField("What should change?", text: $comment, axis: .vertical)
                        .lineLimit(4...10)
                        .focused($writing)
                } footer: {
                    Text("Filed as a public GitHub issue by Email Triage on your PC. "
                        + "Only what you write here and the screen's name are sent, never your mail.")
                }

                if let result {
                    Section {
                        Label(result.message, systemImage: result.filed ? "checkmark.circle.fill" : "arrow.up.forward.app")
                            .foregroundStyle(result.filed ? Color.green : Color.primary)
                        if let url = URL(string: result.url), !result.url.isEmpty {
                            Button(result.filed ? "Open on GitHub" : "Open GitHub's form") { openURL(url) }
                        }
                    }
                }
                if let problem {
                    Section { Text(problem).foregroundStyle(.red) }
                }

                Section("Sent") {
                    if notes.isEmpty {
                        if loadingNotes {
                            ProgressView()
                        } else {
                            Text("Nothing yet.").foregroundStyle(.secondary)
                        }
                    }
                    ForEach(notes) { note in
                        NoteRow(note: note) {
                            if let link = note.url, let url = URL(string: link) { openURL(url) }
                        }
                    }
                }
            }
            .navigationTitle("Feedback")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    if sending {
                        ProgressView()
                    } else {
                        Button("Send", action: send)
                            .disabled(comment.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            .keyboardShortcut(.return, modifiers: .command)
                    }
                }
            }
            .task { await loadNotes() }
            .task {
                writing = true
                try? await Task.sleep(for: .milliseconds(350))
                writing = true
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func send() {
        let text = comment.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !sending else { return }
        sending = true
        problem = nil
        Task {
            defer { sending = false }
            do {
                let answer = try await client.sendFeedback(
                    comment: text, screen: about.title, detail: nil,
                    device: Self.device, appVersion: Self.appVersion)
                result = answer
                comment = ""
                // Not filed: the PC has no GitHub sign-in, so submit it here.
                if !answer.filed, let url = URL(string: answer.url) { openURL(url) }
                await loadNotes()
            } catch {
                problem = error.localizedDescription
            }
        }
    }

    private func loadNotes() async {
        defer { loadingNotes = false }
        if let list = try? await client.feedbackNotes() { notes = list }
    }

    /// "iPad · iPadOS 18.1"
    static var device: String {
        let d = UIDevice.current
        return "\(d.model) · \(d.systemName) \(d.systemVersion)"
    }

    static var appVersion: String {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String
        return build.map { "\(version) (\($0))" } ?? version
    }
}

/// Which part of the app a note is about.
enum FeedbackScreen: String, CaseIterable, Identifiable {
    case inbox, conversation, attachments, move, snooze, reply, keyboard, pairing, other

    var id: String { rawValue }

    var title: String {
        switch self {
        case .inbox: return "Inbox list"
        case .conversation: return "Conversation"
        case .attachments: return "Attachments"
        case .move: return "Move to folder"
        case .snooze: return "Snooze"
        case .reply: return "Reply"
        case .keyboard: return "Keyboard keys"
        case .pairing: return "Pairing and connecting"
        case .other: return "Something else"
        }
    }

    var symbol: String {
        switch self {
        case .inbox: return "tray"
        case .conversation: return "envelope.open"
        case .attachments: return "paperclip"
        case .move: return "folder"
        case .snooze: return "clock"
        case .reply: return "arrowshape.turn.up.left"
        case .keyboard: return "keyboard"
        case .pairing: return "qrcode"
        case .other: return "ellipsis.bubble"
        }
    }
}

private struct NoteRow: View {
    let note: FeedbackNote
    let open: () -> Void

    var body: some View {
        Button(action: open) {
            VStack(alignment: .leading, spacing: 6) {
                Text(note.comment)
                    .foregroundStyle(.primary)
                    .lineLimit(3)
                HStack(spacing: 6) {
                    if let n = note.number {
                        Text("#\(n)").font(.caption.monospacedDigit().weight(.semibold))
                    }
                    Text(note.location)
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    Text(note.stageText)
                        .foregroundStyle(note.stage == "declined" ? Color.secondary : Color.accentColor)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                // Filed › Branch › PR › Merged › Released, as the desktop panel shows it.
                HStack(spacing: 3) {
                    ForEach(0..<5) { step in
                        Capsule()
                            .fill(step < note.stepsReached ? Color.accentColor : Color.secondary.opacity(0.25))
                            .frame(height: 4)
                    }
                }
            }
            .padding(.vertical, 2)
        }
        .buttonStyle(.plain)
        .disabled(note.url == nil)
    }
}
