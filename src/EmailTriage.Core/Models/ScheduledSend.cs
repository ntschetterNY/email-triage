namespace EmailTriage.Core.Models;

public enum ScheduledSendState
{
    Pending = 0,
    Sent = 1,

    /// <summary>Not sent: parked in Drafts and opened for the user to review.</summary>
    Held = 2,

    /// <summary>The draft was deleted or sent by hand before its time.</summary>
    Cancelled = 3,
    Failed = 4,
}

/// <summary>
/// A draft saved to Outlook's Drafts folder, to be sent at <see cref="SendAtUtc"/>.
/// With <see cref="HoldIfReplied"/> set it is a follow-up: if anyone else
/// writes in the conversation after it was scheduled, it is held for review
/// instead of being sent.
/// </summary>
public sealed class ScheduledSend
{
    public long Id { get; set; }

    public required string DraftEntryId { get; set; }
    public required string DraftStoreId { get; set; }

    public required string Subject { get; set; }
    public string Recipients { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }
    public required DateTimeOffset SendAtUtc { get; set; }
    public bool HoldIfReplied { get; set; }

    public ScheduledSendState State { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Why it ended the way it did, or the last error while pending.</summary>
    public string Note { get; set; } = "";
    public int FailureCount { get; set; }

    public DraftRef Draft => new(DraftEntryId, DraftStoreId);
}

/// <summary>What has become of a saved draft since it was scheduled.</summary>
public enum SavedDraftState
{
    /// <summary>Still an unsent draft - ready to send.</summary>
    Waiting,
    AlreadySent,
    Deleted,
    Missing,
}
