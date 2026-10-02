import SwiftUI

/// When should this come back? The PC's presets, a typed time read the way
/// the desktop reads it ("tomorrow 9am", "fri", "3d"), or a date picker.
struct SnoozeSheet: View {
    let client: CompanionClient
    let onPick: (Date) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var presets: [SnoozeOption] = []
    @State private var typed = ""
    @State private var parsed: SnoozeOption?
    @State private var custom = Calendar.current.date(byAdding: .day, value: 1, to: Date()) ?? Date()
    @State private var problem: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    TextField("Type a time, e.g. tomorrow 9am, fri, 3d", text: $typed)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.done)
                        .onSubmit { if let parsed { onPick(parsed.when) } }
                    if let parsed {
                        OptionRow(option: parsed) { onPick(parsed.when) }
                    } else if !typed.trimmingCharacters(in: .whitespaces).isEmpty {
                        Text("Not a time I understand yet")
                            .foregroundStyle(.secondary)
                    }
                }

                Section {
                    ForEach(presets) { option in
                        OptionRow(option: option) { onPick(option.when) }
                    }
                    if presets.isEmpty && problem == nil {
                        ProgressView()
                    }
                }

                Section("Pick a date") {
                    DatePicker("Back at", selection: $custom, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                    Button("Snooze until then") { onPick(custom) }
                }

                if let problem {
                    Section { Text(problem).foregroundStyle(.red) }
                }
            }
            .navigationTitle("Come back to this")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .task {
                do { presets = try await client.snoozeOptions() }
                catch { problem = error.localizedDescription }
            }
            .task(id: typed) {
                let text = typed.trimmingCharacters(in: .whitespaces)
                guard !text.isEmpty else { parsed = nil; return }
                try? await Task.sleep(for: .milliseconds(250))
                guard !Task.isCancelled else { return }
                parsed = try? await client.parseSnooze(text)
            }
        }
        .presentationDetents([.medium, .large])
    }
}

private struct OptionRow: View {
    let option: SnoozeOption
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack {
                Text(option.label)
                    .foregroundStyle(.primary)
                Spacer()
                Text(option.hint)
                    .foregroundStyle(.secondary)
                    .font(.callout)
            }
        }
    }
}
