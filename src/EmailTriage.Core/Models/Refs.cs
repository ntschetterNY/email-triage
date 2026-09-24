namespace EmailTriage.Core.Models;

/// <summary>
/// Locates a MAPI item. EntryId is NOT stable across a move between stores,
/// so anything persisted long-term must additionally carry
/// <see cref="MailSummary.InternetMessageId"/>, which is.
/// </summary>
public readonly record struct MailRef(string EntryId, string StoreId)
{
    public bool IsEmpty => string.IsNullOrEmpty(EntryId);
}

public readonly record struct FolderRef(string EntryId, string StoreId, string Path)
{
    public bool IsEmpty => string.IsNullOrEmpty(EntryId);
}

public readonly record struct DraftRef(string EntryId, string StoreId);
