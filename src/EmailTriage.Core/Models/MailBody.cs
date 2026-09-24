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
    public IReadOnlyList<string> AttachmentNames { get; init; } = Array.Empty<string>();
}

public readonly record struct Recipient(string Name, string Address)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
    public override string ToString() => Display;
}
