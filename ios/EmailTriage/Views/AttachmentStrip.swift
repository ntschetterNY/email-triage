import QuickLook
import SwiftUI

/// The newest message's files, along the top of the conversation as the
/// desktop's reading pane shows them. Tap one to fetch it from the PC and
/// open it in Quick Look, which can also share or save it.
struct AttachmentStrip: View {
    let client: CompanionClient
    let attachments: [Attachment]

    @State private var opening: Attachment.ID?
    @State private var preview: URL?
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    ForEach(attachments) { attachment in
                        chip(attachment)
                    }
                }
                .padding(.horizontal, 12)
            }
            if let problem {
                Text(problem)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .padding(.horizontal, 12)
            }
        }
        .padding(.vertical, 8)
        .background(.bar)
        .quickLookPreview($preview)
    }

    private func chip(_ attachment: Attachment) -> some View {
        Button {
            open(attachment)
        } label: {
            HStack(spacing: 6) {
                if opening == attachment.id {
                    ProgressView()
                        .controlSize(.small)
                } else {
                    Image(systemName: attachment.symbol)
                }
                Text(attachment.name)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 200)
                if !attachment.sizeText.isEmpty {
                    Text(attachment.sizeText)
                        .foregroundStyle(.secondary)
                }
            }
            .font(.footnote)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(.quaternary, in: Capsule())
        }
        .buttonStyle(.plain)
        .disabled(opening != nil)
        .opacity(attachment.blocked ? 0.5 : 1)
        .accessibilityHint(attachment.blocked ? "A program or script. Open it in Outlook on the PC." : "Opens the file")
    }

    private func open(_ attachment: Attachment) {
        if attachment.blocked {
            problem = "\(attachment.name) is a program or script. Open it in Outlook on the PC."
            return
        }
        opening = attachment.id
        problem = nil
        Task {
            defer { opening = nil }
            do {
                preview = try await client.attachment(attachment)
            } catch {
                problem = error.localizedDescription
            }
        }
    }
}
