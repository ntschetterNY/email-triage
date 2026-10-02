import SwiftUI

/// A quick reply. Outlook on the PC builds it - recipients, signature and
/// quoted history - and sends it, so it reads exactly like one from the desktop.
struct ReplySheet: View {
    let model: InboxModel
    let conversation: Conversation
    @State var senderOnly: Bool
    /// Called once sent; true when the conversation was archived too.
    let onSent: (Bool) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var archiveAfter = true
    @State private var sending = false
    @State private var problem: String?
    @FocusState private var focused: Bool

    init(model: InboxModel, conversation: Conversation, senderOnly: Bool, onSent: @escaping (Bool) -> Void) {
        self.model = model
        self.conversation = conversation
        _senderOnly = State(initialValue: senderOnly)
        self.onSent = onSent
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("Reply to", selection: $senderOnly) {
                        Text("All").tag(false)
                        Text("Sender only").tag(true)
                    }
                    .pickerStyle(.segmented)

                    Text(senderOnly ? conversation.from : "\(conversation.from) and everyone on the thread")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }

                Section {
                    TextEditor(text: $text)
                        .frame(minHeight: 180)
                        .focused($focused)
                } footer: {
                    Text("Your Outlook signature and the earlier messages are added on the PC.")
                }

                Section {
                    Toggle("Archive the conversation after sending", isOn: $archiveAfter)
                }

                if let problem {
                    Section { Text(problem).foregroundStyle(.red) }
                }
            }
            .navigationTitle(conversation.displaySubject)
            .navigationBarTitleDisplayMode(.inline)
            .interactiveDismissDisabled(!text.isEmpty || sending)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                        .disabled(sending)
                }
                ToolbarItem(placement: .confirmationAction) {
                    if sending {
                        ProgressView()
                    } else {
                        Button("Send") { Task { await send() } }
                            .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    }
                }
            }
            .onAppear { focused = true }
        }
    }

    private func send() async {
        sending = true
        defer { sending = false }
        do {
            try await model.reply(to: conversation, senderOnly: senderOnly, text: text, archiveAfter: archiveAfter)
            onSent(archiveAfter)
        } catch {
            problem = error.localizedDescription
        }
    }
}
