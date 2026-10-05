import SwiftUI

/// The desktop's move palette (v): your most-used folders first, fuzzy search
/// as you type. The search box has the keyboard as soon as it opens, and
/// Return moves to the top match, so v, a few letters, Return files it.
struct MoveSheet: View {
    let client: CompanionClient
    let onPick: (Folder) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var folders: [Folder] = []
    @State private var loading = true
    @State private var problem: String?
    @FocusState private var searching: Bool
    /// The query `folders` answers, so Return never picks from an older search.
    @State private var answered: String?
    /// Return came before the matches did: pick the top one when they arrive.
    @State private var pickWhenLoaded = false

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
            .safeAreaInset(edge: .top, spacing: 0) { searchField }
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
            .navigationTitle("Move to")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
            .task {
                // A sheet still sliding up can refuse focus; ask again once it's there.
                searching = true
                try? await Task.sleep(for: .milliseconds(350))
                searching = true
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
                    answered = query
                    problem = nil
                    if pickWhenLoaded, let top = folders.first { onPick(top) }
                } catch {
                    if !Task.isCancelled { problem = error.localizedDescription }
                }
            }
        }
    }

    private func pickTop() {
        if answered == query, let top = folders.first {
            onPick(top)
        } else {
            pickWhenLoaded = true
        }
    }

    /// A plain field rather than .searchable: iOS 17 has no way to put the
    /// keyboard in a search bar when the sheet opens.
    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(.secondary)
            TextField("Folder", text: $query)
                .focused($searching)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .submitLabel(.go)
                .onSubmit(pickTop)
            if !query.isEmpty {
                Button {
                    query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear")
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(.bar)
    }
}
