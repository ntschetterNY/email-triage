namespace EmailTriage.Core.Models;

/// <summary>
/// One person or list the composer can suggest. <see cref="Weight"/> ranks
/// people you actually correspond with above the rest of the directory.
/// An empty <see cref="Address"/> marks a name-only hint (from Sent Items'
/// display names) that boosts a matching entry rather than standing alone.
/// </summary>
public sealed record ContactEntry(string Name, string Address, int Weight)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    /// <summary>The form written into a To/Cc/Bcc line.</summary>
    public string LineText => string.IsNullOrWhiteSpace(Name) || Name == Address
        ? Address
        : $"{Name} <{Address}>";
}

/// <summary>One slice of the global address list, read a little at a time.</summary>
public sealed record AddressBookBatch(IReadOnlyList<ContactEntry> Entries, int Total);

/// <summary>
/// Recipient lines, and for a new message the subject, to write onto a draft
/// before sending. A null line is left exactly as Outlook built it.
/// </summary>
public sealed record RecipientOverrides(
    IReadOnlyList<string>? To,
    IReadOnlyList<string>? Cc,
    IReadOnlyList<string>? Bcc)
{
    public string? Subject { get; init; }

    /// <summary>Files on disk to attach, on top of any the draft already carries.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();

    public bool ChangesRecipients => To is not null || Cc is not null || Bcc is not null;
}
