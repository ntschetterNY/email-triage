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

    /// <summary>
    /// What has to happen, in the user's words ("Get Rimkus to confirm the
    /// reroute"); empty means the subject still says it best.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>The line a board card shows: the title when one was given, else the subject.</summary>
    public string DisplayTitle => Title.Length > 0 ? Title : Subject;

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

    /// <summary>When a follow-up chase was last drafted for this item's wait.</summary>
    public DateTimeOffset? LastFollowUpUtc { get; set; }

    /// <summary>
    /// When the user last did anything to this card: a stage move, a note, a
    /// wait added or cleared, a chase, or a "keep" in the stale review.
    /// </summary>
    public DateTimeOffset? TouchedUtc { get; set; }

    /// <summary>
    /// Days this card has sat untouched past the stale threshold; 0 when it
    /// is fine. Not persisted - the board sets it from <c>StaleItems</c> on load.
    /// </summary>
    public int StaleDays { get; set; }

    public bool IsStale => StaleDays > 0;

    public string StaleLabel => $"stale · {StaleDays}d";

    /// <summary>
    /// Days this item has sat waiting past the follow-up threshold; 0 when not
    /// due. Not persisted - the board sets it from <c>FollowUpPlanner</c> on load.
    /// </summary>
    public int FollowUpDays { get; set; }

    public bool IsFollowUpDue => FollowUpDays > 0;

    public string FollowUpLabel => $"follow up · waiting {FollowUpDays}d";

    /// <summary>
    /// Set when a dated wait's follow-up day has come, e.g. "follow up with
    /// Sam today"; empty otherwise. Not persisted - the board sets it from
    /// <c>FollowUpPlanner.FindScheduled</c> on load, and such items sit in its
    /// Follow up column until chased or cleared.
    /// </summary>
    public string ScheduledFollowUp { get; set; } = "";

    public bool IsInFollowUp => ScheduledFollowUp.Length > 0;

    /// <summary>
    /// A message the user sent, filed before its copy reached Sent Items and
    /// so still under a placeholder Message-ID.
    /// </summary>
    public bool IsAwaitingSentCopy => Services.SentMailMatcher.IsPlaceholder(InternetMessageId);

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

    /// <summary>The day it was finished, for the done log; empty while open.</summary>
    public string CompletedDisplay => CompletedUtc is { } d ? d.ToLocalTime().ToString("ddd d MMM") : "";

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
