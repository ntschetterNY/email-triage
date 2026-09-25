namespace EmailTriage.Core.Models;

public sealed record MailBody
{
    public required MailRef Ref { get; init; }
    public required string Subject { get; init; }
    public required string SenderName { get; init; }
    public required string SenderAddress { get; init; }
    public required DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>Raw HTML as Outlook holds it, or null when the mail is plain text.</summary>
    public string? Html { get; init; }

    public string PlainText { get; init; } = "";

    public IReadOnlyList<Recipient> To { get; init; } = Array.Empty<Recipient>();
    public IReadOnlyList<Recipient> Cc { get; init; } = Array.Empty<Recipient>();
    /// <summary>
    /// Real attachments, in Outlook's order. Images the body embeds are not
    /// listed here - they are in <see cref="InlineImages"/> and render in place.
    /// </summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = Array.Empty<AttachmentInfo>();

    /// <summary>
    /// Embedded images the HTML refers to as <c>cid:</c>, saved to the local
    /// <see cref="InlineImageCache"/> so the reading pane can show them.
    /// </summary>
    public IReadOnlyList<InlineImage> InlineImages { get; init; } = Array.Empty<InlineImage>();
}

/// <summary>
/// One attachment. <see cref="Index"/> is Outlook's 1-based position, used to
/// fetch it again when the user opens it.
/// </summary>
public readonly record struct AttachmentInfo(int Index, string FileName, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        <= 0 => "",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):0.0} MB",
    };
}

/// <summary>
/// One embedded image. <see cref="RelativePath"/> is relative to
/// <see cref="InlineImageCache.Root"/>, with forward slashes.
/// </summary>
public readonly record struct InlineImage(string ContentId, string RelativePath);

/// <summary>
/// Where embedded images are written for display. The reading pane maps this
/// folder to <see cref="HostName"/>, because a browser cannot resolve
/// <c>cid:</c> references on its own and data: URIs would push large mails
/// past WebView2's 2 MB NavigateToString limit.
/// </summary>
public static class InlineImageCache
{
    /// <summary>A reserved .example name, so it can never collide with a real site.</summary>
    public const string HostName = "inline-images.example";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmailTriage", "InlineImages");

    /// <summary>Clears images left from earlier sessions; they are re-extracted on demand.</summary>
    public static void Reset()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch { /* a file still in use; stale images are harmless */ }

        Directory.CreateDirectory(Root);
    }
}

public readonly record struct Recipient(string Name, string Address)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
    public override string ToString() => Display;
}
