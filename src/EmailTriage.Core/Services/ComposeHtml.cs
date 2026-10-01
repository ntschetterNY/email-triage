using System.Net;
using System.Text;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Turns the written message into the HTML that sits above Outlook's quoted
/// history. Formatting is kept to what every mail client shows the same way:
/// bold, italic, underline, strikethrough, real bullet and numbered lists,
/// indents, links, Outlook-style @mentions, and pictures embedded by
/// Content-ID. Lines typed as "- item" or "1. item" get a hanging indent.
/// </summary>
public static class ComposeHtml
{
    /// <summary>The HTML, and the pictures it refers to that must travel with the mail.</summary>
    public sealed record Result(string Html, IReadOnlyList<InlineImage> Images);

    public static Result Render(ComposeDocument document, IReadOnlyCollection<ContactEntry>? mentions = null)
    {
        var images = new List<InlineImage>();
        var sb = new StringBuilder("<div style=\"font-family:Calibri,sans-serif;font-size:11pt\">");
        AppendBlocks(sb, document.Blocks, mentions, images);
        sb.Append("<br></div>");
        return new Result(sb.ToString(), images);
    }

    private static void AppendBlocks(
        StringBuilder sb, IEnumerable<ComposeBlock> blocks, IReadOnlyCollection<ContactEntry>? mentions, List<InlineImage> images)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ComposeParagraph p: AppendParagraph(sb, p, mentions, images); break;
                case ComposeList l: AppendList(sb, l, mentions, images); break;
            }
        }
    }

    private static void AppendParagraph(
        StringBuilder sb, ComposeParagraph p, IReadOnlyCollection<ContactEntry>? mentions, List<InlineImage> images)
    {
        var inner = InlinesHtml(p.Inlines, mentions, images, out var marker);
        var indent = p.IndentPt;
        var hanging = 0.0;

        if (marker is { } m)
        {
            // Two spaces of indent typed before the marker is a nested item.
            indent += 12 * (m.Leading.Length / 2);
            hanging = m.Numbered ? 18 : 12;
            indent += hanging;
        }

        var style = new StringBuilder();
        if (indent > 0) style.Append($"margin-left:{Pt(indent)}pt;");
        if (hanging > 0) style.Append($"text-indent:-{Pt(hanging)}pt;");

        if (inner.Length == 0) inner = "&nbsp;";
        sb.Append(style.Length == 0 ? "<div>" : $"<div style=\"{style}\">").Append(inner).Append("</div>");
    }

    private static string Pt(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static void AppendList(
        StringBuilder sb, ComposeList list, IReadOnlyCollection<ContactEntry>? mentions, List<InlineImage> images)
    {
        var tag = list.Style == ComposeListStyle.Number ? "ol" : "ul";
        sb.Append('<').Append(tag).Append('>');

        foreach (var item in list.Items)
        {
            sb.Append("<li>");
            var first = true;
            foreach (var block in item.Blocks)
            {
                // The item's first line sits in the <li> itself, so the marker is beside it.
                if (first && block is ComposeParagraph p && p.IndentPt == 0)
                {
                    var inner = InlinesHtml(p.Inlines, mentions, images, out _);
                    sb.Append(inner.Length == 0 ? "&nbsp;" : inner);
                }
                else
                {
                    AppendBlocks(sb, new[] { block }, mentions, images);
                }
                first = false;
            }
            if (first) sb.Append("&nbsp;");
            sb.Append("</li>");
        }

        sb.Append("</").Append(tag).Append('>');
    }

    /// <summary>
    /// The paragraph's inlines as HTML. A typed list marker at the start is
    /// reported so the paragraph can hang its indent from it.
    /// </summary>
    private static string InlinesHtml(
        IReadOnlyList<ComposeInline> inlines, IReadOnlyCollection<ContactEntry>? mentions,
        List<InlineImage> images, out ListMarker.Match? marker)
    {
        marker = inlines.FirstOrDefault() is ComposeRun { Url: null, Mention: null } lead ? ListMarker.Find(lead.Text) : null;

        var html = new StringBuilder();
        var first = true;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case ComposeRun run:
                    var text = run.Text;
                    // The whitespace before a marker is turned into indent; HTML would collapse it anyway.
                    if (first && marker is { } m) text = text[m.Leading.Length..];
                    html.Append(RunHtml(run with { Text = text }, mentions));
                    break;
                case ComposeLineBreak:
                    html.Append("<br>");
                    break;
                case ComposeImage image:
                    html.Append(ImageHtml(image, images));
                    break;
            }
            first = false;
        }
        return html.ToString();
    }

    private static string RunHtml(ComposeRun run, IReadOnlyCollection<ContactEntry>? mentions)
    {
        if (run.Text.Length == 0) return "";

        string inner;
        if (run.Mention is { } who) inner = MentionLink(run.Text, who);
        else if (run.Url is { } url) inner = Hyperlink(run.Text, url);
        else inner = LineHtml(run.Text, mentions);

        return Styled(inner, run.Style);
    }

    private static string Styled(string html, ComposeStyle style)
    {
        if (style.HasFlag(ComposeStyle.Strikethrough)) html = $"<s>{html}</s>";
        if (style.HasFlag(ComposeStyle.Underline)) html = $"<u>{html}</u>";
        if (style.HasFlag(ComposeStyle.Italic)) html = $"<i>{html}</i>";
        if (style.HasFlag(ComposeStyle.Bold)) html = $"<b>{html}</b>";
        return html;
    }

    /// <summary>
    /// Pictures go out as attachments the HTML points at by Content-ID, which
    /// Outlook shows inline; a data: URL would be stripped by Outlook itself.
    /// </summary>
    private static string ImageHtml(ComposeImage image, List<InlineImage> images)
    {
        // The file name alone, whichever separator the path uses; files are named by GUID so it is unique.
        var name = image.Path[(image.Path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        var cid = $"{Path.GetFileNameWithoutExtension(name)}@emailtriage";
        images.Add(new InlineImage(cid, image.Path));
        return $"<img src=\"cid:{WebUtility.HtmlEncode(cid)}\" width=\"{image.Width}\" height=\"{image.Height}\" style=\"border:0\">";
    }

    /// <summary>Plain text, with any written or bare links and @mentions in it made into links.</summary>
    private static string LineHtml(string line, IReadOnlyCollection<ContactEntry>? mentions)
    {
        var html = new StringBuilder();
        foreach (var part in LinkText.Split(line))
        {
            if (part.Url is { } url) html.Append(Hyperlink(part.Text, url));
            else html.Append(MentionsHtml(part.Text, mentions));
        }
        return html.ToString();
    }

    private static string MentionsHtml(string text, IReadOnlyCollection<ContactEntry>? mentions)
    {
        if (mentions is null || mentions.Count == 0) return WebUtility.HtmlEncode(text);

        var html = new StringBuilder();
        foreach (var segment in MentionText.Split(text, mentions))
        {
            html.Append(segment.Contact is { } who ? MentionLink(segment.Text, who) : WebUtility.HtmlEncode(segment.Text));
        }
        return html.ToString();
    }

    private static string Hyperlink(string text, string url) =>
        $"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(text)}</a>";

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
