namespace EmailTriage.Core.Models;

/// <summary>
/// The message being written, as the composer holds it: paragraphs, lists
/// and pasted images, with the formatting the mail will carry. The view
/// builds one from its editor at send time; plain text becomes one
/// paragraph per line.
/// </summary>
public sealed record ComposeDocument(IReadOnlyList<ComposeBlock> Blocks)
{
    public static readonly ComposeDocument Empty = new(Array.Empty<ComposeBlock>());

    /// <summary>One paragraph per line, as the plain reply box always sent it.</summary>
    public static ComposeDocument FromPlainText(string text)
    {
        if (string.IsNullOrEmpty(text)) return Empty;

        var blocks = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => (ComposeBlock)new ComposeParagraph(
                line.Length == 0 ? Array.Empty<ComposeInline>() : new ComposeInline[] { new ComposeRun(line) }))
            .ToList();
        return new ComposeDocument(blocks);
    }

    /// <summary>Nothing to send: no image, and no text beyond whitespace.</summary>
    public bool IsBlank => !HasImages && string.IsNullOrWhiteSpace(PlainText);

    public bool HasImages => Images.Any();

    /// <summary>Every pasted image, in reading order.</summary>
    public IEnumerable<ComposeImage> Images => Blocks.SelectMany(ImagesIn);

    /// <summary>
    /// The text alone, one line per paragraph and list items marked "- " or
    /// "1. ", for the AI draft and anything else that reads the message as text.
    /// </summary>
    public string PlainText => string.Join("\n", Blocks.SelectMany(b => Lines(b, "")));

    private static IEnumerable<ComposeImage> ImagesIn(ComposeBlock block) => block switch
    {
        ComposeParagraph p => p.Inlines.OfType<ComposeImage>(),
        ComposeList l => l.Items.SelectMany(i => i.Blocks).SelectMany(ImagesIn),
        _ => Enumerable.Empty<ComposeImage>(),
    };

    private static IEnumerable<string> Lines(ComposeBlock block, string indent)
    {
        switch (block)
        {
            case ComposeParagraph p:
                foreach (var line in p.Text.Split('\n')) yield return indent + line;
                break;

            case ComposeList list:
                var n = 0;
                foreach (var item in list.Items)
                {
                    n++;
                    var marker = list.Style == ComposeListStyle.Number ? $"{n}. " : "- ";
                    var first = true;
                    foreach (var inner in item.Blocks)
                    {
                        foreach (var line in Lines(inner, first ? indent + marker : indent + "  "))
                        {
                            yield return line;
                            first = false;
                        }
                    }
                    if (first) yield return indent + marker.TrimEnd();
                }
                break;
        }
    }
}

public abstract record ComposeBlock;

/// <summary>A paragraph: runs of text, line breaks and images, optionally indented.</summary>
public sealed record ComposeParagraph(IReadOnlyList<ComposeInline> Inlines) : ComposeBlock
{
    /// <summary>How far the paragraph is pushed in from the left, in points.</summary>
    public double IndentPt { get; init; }

    /// <summary>The paragraph's text with line breaks as "\n" and images left out.</summary>
    public string Text => string.Concat(Inlines.Select(i => i switch
    {
        ComposeRun r => r.Text,
        ComposeLineBreak => "\n",
        _ => "",
    }));
}

public enum ComposeListStyle { Bullet, Number }

public sealed record ComposeList(ComposeListStyle Style, IReadOnlyList<ComposeListItem> Items) : ComposeBlock;

public sealed record ComposeListItem(IReadOnlyList<ComposeBlock> Blocks);

public abstract record ComposeInline;

[Flags]
public enum ComposeStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
}

/// <summary>
/// A run of text in one style. With <see cref="Url"/> it is a link; with
/// <see cref="Mention"/> it is an @mention of that person and goes out the
/// way Outlook writes one.
/// </summary>
public sealed record ComposeRun(string Text, ComposeStyle Style = ComposeStyle.None) : ComposeInline
{
    public string? Url { get; init; }
    public ContactEntry? Mention { get; init; }
}

/// <summary>A line break within a paragraph (Shift+Enter).</summary>
public sealed record ComposeLineBreak : ComposeInline;

/// <summary>
/// A pasted picture: the PNG on disk to send, and the size it shows at in
/// CSS pixels. Any drop shadow is already painted into the file, since mail
/// clients do not agree on CSS shadows.
/// </summary>
public sealed record ComposeImage(string Path, int Width, int Height) : ComposeInline;

/// <summary>A picture to embed in the outgoing mail, referenced from its HTML as <c>cid:ContentId</c>.</summary>
public sealed record InlineImage(string ContentId, string Path);
