namespace EmailTriage.Core.Models;

/// <summary>
/// A mail parked out of the Inbox until <see cref="ReturnUtc"/>. Outlook has no
/// native snooze for received mail, so the app moves the item to a holding
/// folder and moves it back itself.
/// </summary>
public sealed class SnoozeEntry
{
    public long Id { get; set; }

    public required string InternetMessageId { get; set; }
    public required string EntryId { get; set; }
    public required string StoreId { get; set; }

    public required string Subject { get; set; }
    public required string SenderName { get; set; }

    /// <summary>Where it came from, so it can be returned to the right place.</summary>
    public required string OriginFolderEntryId { get; set; }
    public required string OriginFolderStoreId { get; set; }
    public string OriginFolderPath { get; set; } = "";

    public DateTimeOffset SnoozedUtc { get; set; }
    public required DateTimeOffset ReturnUtc { get; set; }
    public DateTimeOffset? RestoredUtc { get; set; }

    /// <summary>Non-empty once a restore attempt has failed, for surfacing in the UI.</summary>
    public string LastError { get; set; } = "";
    public int FailureCount { get; set; }

    public bool IsPending => RestoredUtc is null;

    public bool IsDue(DateTimeOffset now) => IsPending && ReturnUtc <= now;
}
