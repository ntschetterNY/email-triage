import SwiftUI

@main
struct EmailTriageApp: App {
    @State private var state = AppState()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(state)
                // Tapping a pairing link (emailtriage://pair?...) anywhere on the phone opens it here.
                .onOpenURL { url in state.pair(with: url.absoluteString) }
        }
    }
}

struct RootView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        if let inbox = state.inbox {
            InboxView(model: inbox)
                .id(state.pairing?.fingerprint)
        } else {
            PairingView()
        }
    }
}
