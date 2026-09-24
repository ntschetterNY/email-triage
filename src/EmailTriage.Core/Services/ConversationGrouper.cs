using System.Text.RegularExpressions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Folds Inbox and Sent Items mail into conversations for the triage list.
/// </summary>
public static partial class ConversationGrouper
{
    /// <summary>
    /// Groups by Outlook's conversation ID, falling back to the normalised
    /// subject for mail that has none. Only threads with something still in
    /// the Inbox are returned - a thread you have filed away should not come
    /// back just because you sent in it - ordered by their latest activity,
    /// your own sent messages included, so a fresh follow-up floats up.
    /// </summary>
    public static IReadOnlyList<ConversationThread> Group(
        IEnumerable<MailSummary> inbox, IEnumerable<MailSummary> sent)
    {
        return inbox.Select(m => m with { IsSent = false })
            .Concat(sent.Select(m => m with { IsSent = true }))
            .GroupBy(KeyFor, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(m => !m.IsSent))
            .Select(g => new ConversationThread(
                g.Key,
                g.OrderByDescending(m => m.ReceivedUtc).ToList()))
            .OrderByDescending(t => t.LastActivityUtc)
            .ToList();
    }

    public static string KeyFor(MailSummary m) =>
        m.ConversationKey.Length > 0 ? m.ConversationKey : "subject:" + NormaliseSubject(m.Subject);

    /// <summary>
    /// "RE: FW: [External] Budget" and "Budget" are the same thread: strips
    /// reply and forward prefixes and gateway tags like [External].
    /// </summary>
    public static string NormaliseSubject(string subject)
    {
        var s = subject;
        string previous;
        do
        {
            previous = s;
            s = PrefixRegex().Replace(s, "");
            s = TagRegex().Replace(s, "");
        }
        while (s != previous);

        return WhitespaceRegex().Replace(s, " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"^\s*(re|fw|fwd|aw|wg|sv|vs)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"^\s*\[(external|ext|external email|caution)\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
