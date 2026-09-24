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

    /// <summary>Where it sits on the board. Kept in step with completion: Done iff completed.</summary>
    public ActionStage Stage { get; set; } = ActionStage.ToDo;

    /// <summary>When the item itself should be finished, separate from any blocker's date.</summary>
    public DateTimeOffset? DueUtc { get; set; }

    public List<BlockingTask> Blockers { get; set; } = new();
    public List<Assignment> Assignments { get; set; } = new();

    public bool IsComplete => CompletedUtc is not null;

    public bool IsBlocked => Blockers.Any(b => !b.IsResolved);

    public int OpenAssignmentCount => Assignments.Count(a => !a.IsDone);

    /// <summary>Held up by something or someone: an open blocker or hand-off.</summary>
    public bool IsWaiting => IsBlocked || OpenAssignmentCount > 0;

    public bool IsOverdue =>
        !IsComplete && ((DueUtc is { } due && due < DateTimeOffset.UtcNow)
                        || Blockers.Any(b => b.IsOverdue) || Assignments.Any(a => a.IsOverdue));

    /// <summary>The soonest date anything on this item is due.</summary>
    public DateTimeOffset? NextDueUtc => new[] { DueUtc }
        .Concat(Blockers.Where(b => !b.IsResolved).Select(b => b.DueUtc))
        .Concat(Assignments.Where(a => !a.IsDone).Select(a => a.DueUtc))
        .Where(d => d is not null)
        .Min();

    public string DueDisplay => NextDueUtc is { } d ? d.ToLocalTime().ToString("ddd d MMM") : "";

    /// <summary>
    /// One line for a board card saying what it is waiting on, e.g.
    /// "Blocked by Dina Brown · Assigned to Bobby K".
    /// </summary>
    public string WaitingLine
    {
        get
        {
            var blockedBy = Blockers.Where(b => !b.IsResolved)
                .Select(b => b.WaitingOn.Length > 0 ? b.WaitingOn : b.Description).Distinct().ToList();
            var assigned = Assignments.Where(a => !a.IsDone).Select(a => a.PersonName).Distinct().ToList();

            var parts = new List<string>();
            if (blockedBy.Count > 0) parts.Add("Blocked by " + string.Join(", ", blockedBy));
            if (assigned.Count > 0) parts.Add("With " + string.Join(", ", assigned));
            return string.Join("  ·  ", parts);
        }
    }
}

public enum ActionStage
{
    ToDo = 0,
    Doing = 1,

    /// <summary>Held up by a blocker or handed to someone else.</summary>
    Waiting = 2,
    Done = 3,
}

public enum ActionPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}
