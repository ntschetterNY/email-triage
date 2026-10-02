import SwiftUI
import UIKit
import WebKit

/// Shows a mail page from the PC. The page already forbids scripts in its
/// Content-Security-Policy; JavaScript is off here as well, nothing is
/// stored, and links open in Safari rather than inside the app.
struct MailWebView: UIViewRepresentable {
    let html: String

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.defaultWebpagePreferences.allowsContentJavaScript = false
        config.dataDetectorTypes = [.phoneNumber, .link, .address, .calendarEvent]

        let view = WKWebView(frame: .zero, configuration: config)
        view.navigationDelegate = context.coordinator
        view.isOpaque = false
        view.backgroundColor = .clear
        view.scrollView.backgroundColor = .clear
        return view
    }

    func updateUIView(_ view: WKWebView, context: Context) {
        guard context.coordinator.loaded != html else { return }
        context.coordinator.loaded = html
        view.loadHTMLString(Self.fitToPhone(html), baseURL: nil)
    }

    /// The page is written for the desktop pane; a viewport line makes it
    /// lay out at phone width instead of shrinking a 980-pixel page.
    static func fitToPhone(_ html: String) -> String {
        let viewport = #"<meta name="viewport" content="width=device-width, initial-scale=1">"#
            + "<style>html, body { padding: 12px 14px !important; }</style>"
        if let head = html.range(of: "<head>", options: .caseInsensitive) {
            var page = html
            page.insert(contentsOf: viewport, at: head.upperBound)
            return page
        }
        return viewport + html
    }

    final class Coordinator: NSObject, WKNavigationDelegate {
        var loaded: String?

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            // The page itself loads; anything else the user tapped goes to the system.
            if navigationAction.navigationType == .other, navigationAction.request.url?.scheme == "about" {
                decisionHandler(.allow)
                return
            }
            if let url = navigationAction.request.url, navigationAction.navigationType == .linkActivated {
                if ["http", "https", "mailto", "tel"].contains(url.scheme?.lowercased() ?? "") {
                    UIApplication.shared.open(url)
                }
                decisionHandler(.cancel)
                return
            }
            decisionHandler(navigationAction.navigationType == .other ? .allow : .cancel)
        }
    }
}
