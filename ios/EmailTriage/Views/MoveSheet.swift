import SwiftUI

/// The desktop's move palette (v): your most-used folders first, fuzzy search as you type.
struct MoveSheet: View {
    let client: CompanionClient
    let onPick: (Folder) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var folders: [Folder] = []
    @State private var loading = true
    @State private var problem: String?

    var body: some View {
        NavigationStack {
            List(folders) { folder in
                Button {
                    onPick(folder)
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(folder.name)
                            .foregroundStyle(.primary)
                        if folder.breadcrumb != folder.name {
                            Text(folder.breadcrumb.replacingOccurrences(of: " -> ", with: " › "))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                    }
                }
            }
            .listStyle(.plain)
            .overlay {
                if let problem {
                    ContentUnavailableView("Can't load folders", systemImage: "folder.badge.questionmark",
                                           description: Text(problem))
                } else if loading && folders.isEmpty {
                    ProgressView()
                } else if folders.isEmpty {
                    ContentUnavailableView.search(text: query)
                }
            }
            .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Folder")
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            .navigationTitle("Move to")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .task(id: query) {
                if !query.isEmpty {
                    try? await Task.sleep(for: .milliseconds(200))
                    guard !Task.isCancelled else { return }
                }
                loading = true
                defer { loading = false }
                do {
                    folders = try await client.folders(matching: query)
                    problem = nil
                } catch {
                    if !Task.isCancelled { problem = error.localizedDescription }
                }
            }
        }
    }
}
