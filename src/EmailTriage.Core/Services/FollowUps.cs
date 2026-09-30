using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>One overdue wait: who has the ball, what they owe, and for how long.</summary>
public sealed record FollowUpDue(
    ActionItem Item, string Who, string What, DateTimeOffset SinceUtc, int DaysWaiting)
{
    /// <summary>The follow-up date the wait was given, for a scheduled one.</summary>
    public DateTimeOffset? DueUtc { get; init; }

    /// <summary>The hand-off being chased, when it is one rather than a blocker.</summary>
    public Assignment? Handoff { get; init; }
}

/// <summary>
/// Decides which waiting action items are owed a follow-up. Purely local
/// arithmetic over timestamps already in the database - no model call - so the
/// board can mark stale cards for free; the AI only runs when the user asks
/// for the chase draft itself.
/// </summary>
public static class FollowUpPlanner
{
    /// <summary>
    /// The items that have sat waiting for at least <paramref name="afterDays"/>
    /// days since the wait began or the last follow-up, longest wait first.
    /// Zero or negative days turns the feature off.
    /// </summary>
    public static IReadOnlyList<FollowUpDue> FindDue(
        IEnumerable<ActionItem> items, int afterDays, DateTimeOffset nowUtc)
    {
        if (afterDays <= 0) return Array.Empty<FollowUpDue>();

        return items
            .Select(i => Describe(i, nowUtc))
            .Where(d => d is not null)
            .Select(d => d!)
            .Where(d =>
            {
                var anchor = d.Item.LastFollowUpUtc is { } followed && followed > d.SinceUtc
                    ? followed
                    : d.SinceUtc;
                return (nowUtc - anchor).TotalDays >= afterDays;
            })
            .OrderByDescending(d => d.DaysWaiting)
            .ToList();
    }

    /// <summary>
    /// What an item is waiting on - the oldest open blocker or hand-off - with
    /// no staleness test, for chasing on demand. Null when nothing is open.
    /// </summary>
    public static FollowUpDue? Describe(ActionItem item, DateTimeOffset nowUtc)
    {
        if (item.IsComplete) return null;

        var waits = item.Blockers
            .Where(b => !b.IsResolved)
            .Select(b => (Who: b.WaitingOn, What: b.Description, Since: b.CreatedUtc))
            .Concat(item.Assignments
                .Where(a => !a.IsDone)
                // A hand-off's clock starts when they were actually told.
                .Select(a => (Who: a.PersonName, What: a.Task, Since: a.NotifiedUtc ?? a.CreatedUtc)))
            .OrderBy(w => w.Since)
            .ToList();

        if (waits.Count == 0) return null;

        var oldest = waits[0];
        return new FollowUpDue(
            item, oldest.Who, oldest.What, oldest.Since,
            Math.Max(0, (int)(nowUtc - oldest.Since).TotalDays));
    }

    /// <summary>
    /// The waits whose follow-up day has come: a blocker or hand-off dated
    /// today or earlier (in <paramref name="now"/>'s time zone) that has not
    /// been chased on or after that day. Earliest date first. These fill the
    /// board's Follow up column; chasing or clearing the wait takes them out.
    /// </summary>
    public static IReadOnlyList<FollowUpDue> FindScheduled(IEnumerable<ActionItem> items, DateTimeOffset now) =>
        items
            .Select(i => Scheduled(i, now))
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => d.DueUtc)
            .ToList();

    /// <summary>The item's earliest dated wait whose follow-up day has come, or null.</summary>
    public static FollowUpDue? Scheduled(ActionItem item, DateTimeOffset now)
    {
        if (item.IsComplete) return null;

        DateTime Day(DateTimeOffset t) => t.ToOffset(now.Offset).Date;
        var today = now.Date;

        // A chase made on or after the follow-up day answers it; one made
        // before (an early nudge) does not.
        bool Pending(DateTimeOffset due) =>
            Day(due) <= today && (item.LastFollowUpUtc is not { } chased || Day(chased) < Day(due));

        var blockers = item.Blockers
            .Where(b => !b.IsResolved && b.DueUtc is { } due && Pending(due))
            .Select(b => new FollowUpDue(item, b.WaitingOn, b.Description, b.CreatedUtc,
                Math.Max(0, (int)(now - b.CreatedUtc).TotalDays)) { DueUtc = b.DueUtc });

        var handoffs = item.Assignments
            .Where(a => !a.IsDone && a.DueUtc is { } due && Pending(due))
            .Select(a =>
            {
                var since = a.NotifiedUtc ?? a.CreatedUtc;
                return new FollowUpDue(item, a.PersonName, a.Task, since,
                    Math.Max(0, (int)(now - since).TotalDays)) { DueUtc = a.DueUtc, Handoff = a };
            });

        return blockers.Concat(handoffs).OrderBy(d => d.DueUtc).FirstOrDefault();
    }

    /// <summary>The card's line for a scheduled follow-up, e.g. "follow up with Sam · due Mon 28 Sep".</summary>
    public static string Label(FollowUpDue due, DateTimeOffset now)
    {
        var with = due.Who.Trim().Length > 0 ? $"follow up with {due.Who.Trim()}" : "follow up";
        if (due.DueUtc is not { } d) return with;

        var day = d.ToOffset(now.Offset).Date;
        return day == now.Date ? $"{with} today" : $"{with} · due {day:ddd d MMM}";
    }

    /// <summary>
    /// The brief handed to the draft prompt: what is owed, by whom, since when.
    /// The draft rules (tone, no invented facts) live in <see cref="AiDraftService"/>.
    /// </summary>
    public static string BuildInstructions(FollowUpDue due)
    {
        var who = due.Who.Length > 0 ? $" on {due.Who}" : "";
        var wait = due.DaysWaiting == 1 ? "1 day" : $"{due.DaysWaiting} days";

        return $"This is a follow-up nudge. We have been waiting{who} for: {due.What} - " +
               $"since {due.SinceUtc.ToLocalTime():ddd d MMM} ({wait} now). " +
               "Write a short, friendly chase: refer to what is outstanding, ask for an update or a date, " +
               "and offer to help unblock it. No guilt-tripping, and do not restate the whole history.";
    }
}
