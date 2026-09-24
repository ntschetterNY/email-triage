using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Services;

/// <summary>
/// Prepares mail bodies for display in WebView2.
///
/// Mail HTML is hostile input. Everything rendered here goes through a strict
/// Content-Security-Policy that forbids scripts outright and, by default,
/// refuses to fetch remote images - which in an inbox are overwhelmingly
/// tracking pixels rather than content.
/// </summary>
public static partial class HtmlPresenter
{
    /// <summary>
    /// WebView2's NavigateToString refuses documents over 2 MB, so a long
    /// thread stops adding older messages before it reaches that.
    /// </summary>
    private const int MaxDocumentChars = 1_500_000;

    public static string Render(MailBody body, bool blockRemoteImages, bool darkTheme = true)
        => Document(PrepareContent(body, darkTheme), blockRemoteImages, darkTheme);

    /// <summary>
    /// A whole conversation, newest first. The newest message is open; older
    /// ones are collapsed to a one-line summary that expands on click - plain
    /// HTML details elements, so nothing here needs script.
    /// </summary>
    public static string RenderThread(
        IReadOnlyList<MailBody> bodies, bool blockRemoteImages, int hiddenOlder = 0, bool darkTheme = true)
    {
        if (bodies.Count == 1 && hiddenOlder == 0) return Render(bodies[0], blockRemoteImages, darkTheme);

        var sb = new StringBuilder();
        var shown = 0;

        foreach (var body in bodies)
        {
            var content = PrepareContent(body, darkTheme);
            if (shown > 0 && sb.Length + content.Length > MaxDocumentChars) break;

            sb.Append(shown == 0 ? "<details class=\"msg\" open>" : "<details class=\"msg\">");
            sb.Append("<summary>").Append(MessageHeader(body)).Append("</summary>");
            sb.Append("<div class=\"msg-body\">").Append(content).Append("</div></details>");
            shown++;
        }

        var hidden = hiddenOlder + bodies.Count - shown;
        if (hidden > 0)
        {
            sb.Append($"<p class=\"older\">{hidden} older message{(hidden == 1 ? "" : "s")} in this conversation " +
                      "not shown here - open it in Outlook to see them.</p>");
        }

        return Document(sb.ToString(), blockRemoteImages, darkTheme);
    }

    private static string MessageHeader(MailBody body)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);

        var from = string.IsNullOrWhiteSpace(body.SenderName) ? body.SenderAddress : body.SenderName;
        var snippet = body.PlainText.Length > 160 ? body.PlainText[..160] : body.PlainText;
        snippet = string.Join(' ', snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var to = body.ToSummary.Length == 0 ? "" : $"<span class=\"to\">to {E(body.ToSummary)}</span>";
        var cc = body.HasCc ? $"<span class=\"to\">cc {E(body.CcSummary)}</span>" : "";
        var clip = body.HasAttachments ? $"<span class=\"to\">&#128206; {body.Attachments.Count}</span>" : "";

        return $"<span class=\"from\">{E(from)}</span>{to}{cc}{clip}" +
               $"<span class=\"when\">{E(body.ReceivedDisplay)}</span>" +
               $"<span class=\"snippet\">{E(snippet)}</span>";
    }

    /// <summary>One message's body, made safe and ready for the dark surface.</summary>
    private static string PrepareContent(MailBody body, bool darkTheme)
    {
        var content = !string.IsNullOrWhiteSpace(body.Html)
            ? StripDangerousMarkup(body.Html!)
            : PlainTextToHtml(body.PlainText);

        content = MailImages.ResolveInlineImages(content, body.InlineImages);
        content = MailImages.StripTrackingPixels(content);

        // Mail is authored for white paper; move its colors onto the dark surface.
        if (darkTheme) content = DarkMailColors.Adapt(content);
        return content;
    }

    private static string Document(string content, bool blockRemoteImages, bool darkTheme)
    {
        // Embedded images arrive as data: or from the private inline host;
        // remote ones only when the setting allows network fetches.
        var imgPolicy = blockRemoteImages
            ? $"data: https://{MailImages.InlineImageHost}"
            : "data: https: http:";

        var csp =
            "default-src 'none'; " +
            "style-src 'unsafe-inline'; " +
            $"img-src {imgPolicy}; " +
            "font-src data:; " +
            "script-src 'none'; " +
            "form-action 'none'; " +
            "frame-src 'none'; " +
            "object-src 'none'";

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <style>
              :root { color-scheme: {{(darkTheme ? "dark" : "light")}}; }
              html, body {
                margin: 0; padding: 18px 22px;
                background: {{(darkTheme ? "#16181d" : "#ffffff")}};
                color: {{(darkTheme ? "#d6d9e0" : "#1a1a1a")}};
                font: 14px/1.6 -apple-system, "Segoe UI", system-ui, sans-serif;
                word-wrap: break-word; overflow-wrap: anywhere;
              }
              /* A comfortable measure; wide panes otherwise give 200-character lines. */
              .mail { max-width: 860px; }
              a { color: {{(darkTheme ? "#7aa2f7" : "#0b57d0")}}; text-underline-offset: 2px; }
              ::selection { background: {{(darkTheme ? "rgba(122,162,247,.30)" : "rgba(11,87,208,.18)")}}; }
              img { max-width: 100%; height: auto; }
              table { max-width: 100%; border-collapse: collapse; }
              pre, code { white-space: pre-wrap; font-family: Consolas, monospace; font-size: 13px; }
              hr { border: 0; border-top: 1px solid {{(darkTheme ? "#2a3040" : "#d0d0d0")}}; }
              blockquote {
                margin: 8px 0; padding-left: 12px;
                border-left: 3px solid {{(darkTheme ? "#3a3f4b" : "#d0d0d0")}};
                color: {{(darkTheme ? "#9aa0ac" : "#555")}};
              }
              /* conversation view */
              details.msg {
                border: 1px solid {{(darkTheme ? "#2a3040" : "#d8d8d8")}};
                border-radius: 8px; margin: 0 0 12px; overflow: hidden;
              }
              details.msg > summary {
                list-style: none; cursor: pointer; padding: 9px 14px;
                background: {{(darkTheme ? "#1f2430" : "#f3f4f6")}};
                font-size: 12.5px; line-height: 1.5;
              }
              details.msg > summary::-webkit-details-marker { display: none; }
              details.msg > summary::before { content: "\25B8"; opacity: .6; margin-right: 8px; }
              details.msg[open] > summary::before { content: "\25BE"; }
              details.msg .from { font-weight: 600; }
              details.msg .to { opacity: .7; margin-left: 10px; }
              details.msg .when { float: right; opacity: .7; }
              details.msg .snippet { display: block; opacity: .6; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
              details.msg[open] .snippet { display: none; }
              details.msg > .msg-body { padding: 12px 16px; }
              p.older { opacity: .6; font-size: 12.5px; }
            </style>
            </head>
            <body><div class="mail">{{content}}</div></body>
            </html>
            """;
    }

    /// <summary>
    /// Defence in depth behind the CSP: removes script and event-handler markup
    /// before it ever reaches the renderer.
    /// </summary>
    private static string StripDangerousMarkup(string html)
    {
        html = ScriptRegex().Replace(html, "");
        html = EventHandlerRegex().Replace(html, "");
        html = JavascriptUrlRegex().Replace(html, "href=\"#\"");
        html = MetaRefreshRegex().Replace(html, "");
        return html;
    }

    private static string PlainTextToHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "<p style='opacity:.6'>(no content)</p>";

        var sb = new StringBuilder("<div style=\"white-space:pre-wrap\">");
        sb.Append(WebUtility.HtmlEncode(text));
        sb.Append("</div>");
        return sb.ToString();
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    [GeneratedRegex(@"href\s*=\s*(""|')?\s*javascript:[^""'>]*(""|')?", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrlRegex();

    [GeneratedRegex(@"<meta[^>]+http-equiv\s*=\s*[""']?refresh[""']?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();


    /// <summary>
    /// Wraps the user's reply text as HTML to sit above Outlook's quoted history,
    /// with any @mentions written as Outlook writes them.
    /// </summary>
    public static string ComposeReplyFragment(string plainText, IReadOnlyCollection<ContactEntry>? mentions = null)
    {
        var paragraphs = plainText.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0 ? "<div>&nbsp;</div>" : $"<div>{LineHtml(line, mentions)}</div>");

        return $"<div style=\"font-family:Calibri,sans-serif;font-size:11pt\">{string.Join("", paragraphs)}<br></div>";
    }

    private static string LineHtml(string line, IReadOnlyCollection<ContactEntry>? mentions)
    {
        if (mentions is null || mentions.Count == 0) return WebUtility.HtmlEncode(line);

        var html = new StringBuilder();
        foreach (var segment in MentionText.Split(line, mentions))
        {
            html.Append(segment.Contact is { } who ? MentionLink(segment.Text, who) : WebUtility.HtmlEncode(segment.Text));
        }
        return html.ToString();
    }

    /// <summary>
    /// A mailto link with an "OWAAM" id is how Outlook marks a mention, and
    /// what lets the recipient's Outlook flag the message with an @.
    /// </summary>
    private static string MentionLink(string text, ContactEntry who) =>
        $"<a id=\"OWAAM{Guid.NewGuid().ToString("N").ToUpperInvariant()}Z\" href=\"mailto:{WebUtility.HtmlEncode(who.Address)}\">"
        + "<span style=\"font-family:Calibri,sans-serif;text-decoration:none\">"
        + WebUtility.HtmlEncode(text)
        + "</span></a>";
}
