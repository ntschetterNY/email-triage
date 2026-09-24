using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EmailTriage.Core.Models;

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
    public static string Render(MailBody body, bool blockRemoteImages, bool darkTheme = true)
    {
        var content = !string.IsNullOrWhiteSpace(body.Html)
            ? StripDangerousMarkup(body.Html!)
            : PlainTextToHtml(body.PlainText);

        // img-src data: allows embedded/inline images through while still
        // refusing anything fetched over the network.
        var imgPolicy = blockRemoteImages ? "data: cid:" : "data: cid: https: http:";

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
              a { color: {{(darkTheme ? "#7aa2f7" : "#0b57d0")}}; }
              img { max-width: 100%; height: auto; }
              table { max-width: 100%; border-collapse: collapse; }
              pre, code { white-space: pre-wrap; font-family: Consolas, monospace; font-size: 13px; }
              blockquote {
                margin: 8px 0; padding-left: 12px;
                border-left: 3px solid {{(darkTheme ? "#3a3f4b" : "#d0d0d0")}};
                color: {{(darkTheme ? "#9aa0ac" : "#555")}};
              }
              /* Mail HTML routinely hard-codes white backgrounds on its own
                 tables, which looks broken inside a dark shell. */
              {{(darkTheme ? "[bgcolor], [style*='background'] { background-color: transparent !important; }" : "")}}
            </style>
            </head>
            <body>{{content}}</body>
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
    /// Wraps the user's reply text as HTML to sit above Outlook's quoted history.
    /// </summary>
    public static string ComposeReplyFragment(string plainText)
    {
        var encoded = WebUtility.HtmlEncode(plainText).Replace("\r\n", "\n");
        var paragraphs = encoded.Split('\n')
            .Select(line => line.Length == 0 ? "<div>&nbsp;</div>" : $"<div>{line}</div>");

        return $"<div style=\"font-family:Calibri,sans-serif;font-size:11pt\">{string.Join("", paragraphs)}<br></div>";
    }
}
