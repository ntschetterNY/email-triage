namespace EmailTriage.Core.Models;

/// <summary>
/// A mail the user has committed to doing something about, plus the working
/// notes that hang off it. Keyed by <see cref="InternetMessageId"/> rather than
/// EntryId so the record survives the mail being moved between folders.
/// </summary>
public sealed class ActionItem
{
    public long Id { get; set; }

    public required string InternetMessageId { get; set; }

    /// <summary>Last known location. Refreshed opportunistically; may be stale.</summary>
    public string EntryId { get; set; } = "";
    public string StoreId { get; set; } = "";

    public required string Subject { get; set; }
    public required string SenderName { get; set; }
    public required string SenderAddress { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public ActionPriority Priority { get; set; } = ActionPriority.Normal;
    public string Notes { get; set; } = "";

    public List<BlockingTask> Blockers { get; set; } = new();
    public List<Assignment> Assignments { get; set; } = new();

    public bool IsComplete => CompletedUtc is not null;

    public bool IsBlocked => Blockers.Any(b => !b.IsResolved);

    public int OpenAssignmentCount => Assignments.Count(a => !a.IsDone);
}

public enum ActionPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}
