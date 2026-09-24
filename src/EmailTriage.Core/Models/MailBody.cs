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
    /// <summary>Files the user can open. Images already shown in the body are left out.</summary>
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = Array.Empty<MailAttachment>();

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Embedded images saved to disk: Content-ID (lower case) to a path under
    /// the inline image folder, for resolving the HTML's <c>cid:</c> links.
    /// </summary>
    public IReadOnlyDictionary<string, string> InlineImages { get; init; } = new Dictionary<string, string>();

    /// <summary>Names only, for the reading-pane header.</summary>
    public string ToSummary => string.Join(", ", To.Select(r => r.Display));
    public string CcSummary => string.Join(", ", Cc.Select(r => r.Display));
    public bool HasCc => Cc.Count > 0;

    /// <summary>Full "Name &lt;address&gt;" lines, shown on hover.</summary>
    public string ToDetail => Detail(To);
    public string CcDetail => Detail(Cc);

    public string ReceivedDisplay => ReceivedUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm");

    private static string Detail(IEnumerable<Recipient> list) =>
        string.Join("\n", list.Select(r =>
            string.IsNullOrWhiteSpace(r.Name) || r.Name == r.Address ? r.Address : $"{r.Name} <{r.Address}>"));
}

public readonly record struct Recipient(string Name, string Address)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
    public override string ToString() => Display;
}
