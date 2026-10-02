import SwiftUI
import UIKit

/// First run: pair with the PC by scanning the code in Email Triage's
/// Settings, or by pasting the same link.
struct PairingView: View {
    @Environment(AppState.self) private var state
    @State private var scanning = false
    @State private var problem: String?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    Image(systemName: "tray.and.arrow.down.fill")
                        .font(.system(size: 44))
                        .foregroundStyle(.tint)
                        .padding(.top, 24)

                    Text("Triage your Outlook inbox from your iPhone or iPad")
                        .font(.title2.bold())

                    VStack(alignment: .leading, spacing: 10) {
                        Step(number: 1, text: "On your PC, open Email Triage and press Ctrl+, for Settings.")
                        Step(number: 2, text: "Under iPhone and iPad, tick \"Let my iPhone or iPad triage this inbox over Wi-Fi\", then click Pair a device.")
                        Step(number: 3, text: "Scan the code it shows. This device has to be on the same Wi-Fi as the PC.")
                    }

                    Button {
                        scanning = true
                    } label: {
                        Label("Scan pairing code", systemImage: "qrcode.viewfinder")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .padding(.top, 8)

                    Button {
                        pair(UIPasteboard.general.string ?? "")
                    } label: {
                        Label("Paste pairing link", systemImage: "doc.on.clipboard")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.large)

                    if let problem {
                        Text(problem)
                            .font(.callout)
                            .foregroundStyle(.red)
                    }

                    Text("Your mail stays in Outlook on the PC. This device shows it only while it can reach the PC, over an encrypted connection to that PC alone.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .padding(.top, 8)
                }
                .padding(.horizontal, 24)
                // A readable column on iPad rather than the full width.
                .frame(maxWidth: 520)
                .frame(maxWidth: .infinity)
            }
            .navigationTitle("Email Triage")
            .navigationBarTitleDisplayMode(.inline)
            .sheet(isPresented: $scanning) {
                QRScannerSheet { code in
                    scanning = false
                    pair(code)
                }
            }
        }
    }

    private func pair(_ link: String) {
        if state.pair(with: link) {
            problem = nil
        } else {
            problem = "That isn't an Email Triage pairing code. Use the one under Settings › iPhone and iPad › Pair a device on the PC."
        }
    }
}

private struct Step: View {
    let number: Int
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text("\(number)")
                .font(.footnote.bold())
                .frame(width: 22, height: 22)
                .background(Circle().fill(Color.accentColor.opacity(0.2)))
            Text(text)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
