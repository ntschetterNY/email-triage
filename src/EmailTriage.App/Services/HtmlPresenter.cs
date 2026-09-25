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
            ? ResolveInlineImages(StripDangerousMarkup(body.Html!), body.InlineImages)
            : PlainTextToHtml(body.PlainText);

        // Embedded images are served from the local cache folder the reading
        // pane maps to InlineImageCache.HostName; anything else fetched over
        // the network stays blocked unless the user asks for it.
        var inlineHost = $"https://{InlineImageCache.HostName}";
        var imgPolicy = blockRemoteImages
            ? $"data: {inlineHost}"
            : $"data: {inlineHost} https: http:";

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
              {{(darkTheme ? DarkOverrides : "")}}
            </style>
            </head>
            <body>{{content}}</body>
            </html>
            """;
    }

    /// <summary>
    /// Mail is written for a white page: it hard-codes black text (inline
    /// styles, &lt;font color&gt;) and white table backgrounds. Clearing only
    /// the backgrounds leaves black-on-dark, so text colours are overridden
    /// too. Links keep an accent so they still read as links; images are
    /// untouched.
    /// </summary>
    private const string DarkOverrides = """
        body *:not(img):not(svg) {
          color: #d6d9e0 !important;
          background-color: transparent !important;
          border-color: #3a3f4b !important;
        }
        body a, body a * { color: #7aa2f7 !important; }
        body blockquote, body blockquote * { color: #9aa0ac !important; }
        body hr { background-color: #3a3f4b !important; }
        """;

    /// <summary>
    /// True when the body pulls images from the web - the ones the default
    /// policy blocks - so the UI can offer to load them.
    /// </summary>
    public static bool HasRemoteImages(MailBody body) =>
        body.Html is not null && RemoteImageRegex().IsMatch(body.Html);

    /// <summary>
    /// Points each <c>cid:</c> reference at the copy of that image saved in
    /// the inline cache. References with no saved image are left as they are
    /// and simply do not load.
    /// </summary>
    private static string ResolveInlineImages(string html, IReadOnlyList<InlineImage> images)
    {
        if (images.Count == 0) return html;

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images) byId.TryAdd(image.ContentId, image.RelativePath);

        return CidRegex().Replace(html, m =>
        {
            var id = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!byId.TryGetValue(id, out var relative)) return m.Value;

            var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            return $"https://{InlineImageCache.HostName}/{escaped}";
        });
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

    [GeneratedRegex(@"cid:([^""'\s>)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRegex();

    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""']?\s*https?:", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteImageRegex();

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
