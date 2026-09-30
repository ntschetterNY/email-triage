using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Finds the open cards nobody has touched for a while - no stage move, no
/// note, no wait added or cleared, no chase - so the board can say so and
/// the review walk can put them in front of the user one at a time. Pure
/// timestamp arithmetic; the user decides what each one still is.
/// </summary>
public static class StaleItems
{
    /// <summary>When the item was last worked on: its touched stamp, or when it was made.</summary>
    public static DateTimeOffset LastTouched(ActionItem item) => item.TouchedUtc ?? item.CreatedUtc;

    /// <summary>Whole days since the item was last worked on.</summary>
    public static int DaysIdle(ActionItem item, DateTimeOffset nowUtc) =>
        Math.Max(0, (int)(nowUtc - LastTouched(item)).TotalDays);

    /// <summary>
    /// The open items idle for at least <paramref name="afterDays"/> days,
    /// longest idle first. A card in the Follow up column is already asking
    /// for attention, so it is left out. Zero or negative days turns it off.
    /// </summary>
    public static IReadOnlyList<ActionItem> Find(IEnumerable<ActionItem> items, int afterDays, DateTimeOffset nowUtc)
    {
        if (afterDays <= 0) return Array.Empty<ActionItem>();

        return items
            .Where(i => !i.IsComplete && !i.IsInFollowUp)
            .Where(i => DaysIdle(i, nowUtc) >= afterDays)
            .OrderByDescending(i => DaysIdle(i, nowUtc))
            .ThenBy(i => i.ReceivedUtc)
            .ToList();
    }
}
