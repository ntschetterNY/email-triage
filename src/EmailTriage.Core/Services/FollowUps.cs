using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>One overdue wait: who has the ball, what they owe, and for how long.</summary>
public sealed record FollowUpDue(
    ActionItem Item, string Who, string What, DateTimeOffset SinceUtc, int DaysWaiting);

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
