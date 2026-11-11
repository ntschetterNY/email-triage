using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Puts conversations that just came back from a snooze at the top of the
/// triage list, the way Gmail does: a returned mail sorts as if it had
/// arrived the moment it came back, so it leads the list and then sinks
/// naturally as newer mail arrives, instead of landing back at its original
/// (often days-old) position where it would never be seen.
/// </summary>
public static class SnoozedReturns
{
    /// <summary>How long a returned conversation keeps its Snoozed tag.</summary>
    public static readonly TimeSpan TagFor = TimeSpan.FromDays(7);

    /// <summary>
    /// When each thread last came back from a snooze, keyed by thread key.
    /// Matched on Message-ID, which survives the move out and back.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ByThread(
        IEnumerable<ConversationThread> threads, IEnumerable<SnoozeEntry> returned)
    {
        var byMessage = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in returned)
        {
            if (entry.ReturnedUtc is not { } when || string.IsNullOrEmpty(entry.InternetMessageId)) continue;
            if (!byMessage.TryGetValue(entry.InternetMessageId, out var seen) || when > seen)
                byMessage[entry.InternetMessageId] = when;
        }

        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (byMessage.Count == 0) return result;

        foreach (var thread in threads)
        {
            DateTimeOffset? latest = null;
            foreach (var m in thread.InboxMessages)
                if (byMessage.TryGetValue(m.InternetMessageId, out var when) && (latest is null || when > latest))
                    latest = when;

            if (latest is { } l) result[thread.Key] = l;
        }

        return result;
    }

    /// <summary>
    /// The threads newest first, with a returned thread counted as active from
    /// the moment it came back. Otherwise the grouper's order is kept.
    /// </summary>
    public static IReadOnlyList<ConversationThread> Order(
        IReadOnlyList<ConversationThread> threads, IReadOnlyDictionary<string, DateTimeOffset> returned)
    {
        if (returned.Count == 0) return threads;

        return threads
            .OrderByDescending(t => returned.TryGetValue(t.Key, out var back) && back > t.LastActivityUtc
                ? back
                : t.LastActivityUtc)
            .ToList();
    }
}
