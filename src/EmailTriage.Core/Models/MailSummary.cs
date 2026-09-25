namespace EmailTriage.Core.Models;

/// <summary>
/// The cheap projection of a mail item used to paint list rows. Deliberately
/// excludes the body: pulling bodies for a whole folder is what makes naive
/// Outlook tools crawl.
/// </summary>
public sealed record MailSummary
{
    public required MailRef Ref { get; init; }

    /// <summary>RFC 5322 Message-ID. Stable across folder moves; our join key.</summary>
    public required string InternetMessageId { get; init; }

    public required string Subject { get; init; }
    public required string SenderName { get; init; }
    public required string SenderAddress { get; init; }
    public required DateTimeOffset ReceivedUtc { get; init; }
    public required bool IsUnread { get; init; }
    public required bool HasAttachments { get; init; }

    /// <summary>First ~200 chars of the plain-text body, for the list preview.</summary>
    public string Preview { get; init; } = "";

    /// <summary>
    /// Outlook's display line for To and CC: names joined by "; ", with an
    /// address only where no name is known. Cheap to read for a whole folder,
    /// so the filter box can search recipients without opening each message.
    /// </summary>
    public string DisplayTo { get; init; } = "";
    public string DisplayCc { get; init; } = "";

    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public int ConversationSize { get; init; } = 1;

    /// <summary>Outlook's conversation ID as hex; empty when the store has none.</summary>
    public string ConversationKey { get; init; } = "";

    /// <summary>True for your own messages, read from Sent Items.</summary>
    public bool IsSent { get; init; }

    /// <summary>Ordinary mail, or a meeting request, cancellation or response.</summary>
    public MailKind Kind { get; init; } = MailKind.Mail;

    public string DisplaySender =>
        string.IsNullOrWhiteSpace(SenderName) ? SenderAddress : SenderName;
}
