using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Finds the copy of a message the user just sent. Outlook only gives a new
/// message its Message-ID once it has gone, so a follow-up set on one is
/// filed under a placeholder and repointed when its copy turns up in Sent
/// Items - by subject, recipient and time sent.
/// </summary>
public static class SentMailMatcher
{
    private const string Prefix = "<pending-sent:";

    /// <summary>
    /// How far before the recorded send time a copy may be stamped: Outlook's
    /// clock and ours can disagree by a little.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    public static string NewPlaceholder() => $"{Prefix}{Guid.NewGuid():N}>";

    public static bool IsPlaceholder(string internetMessageId) =>
        internetMessageId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The earliest sent message after <paramref name="sentUtc"/> with this
    /// subject that went to the person (matched on name or address against
    /// Outlook's To and Cc display lines). Null when none has arrived yet.
    /// </summary>
    public static MailSummary? Find(
        IEnumerable<MailSummary> sent, string subject, string personName, string personAddress, DateTimeOffset sentUtc)
    {
        subject = subject.Trim();

        bool ToThem(MailSummary m)
        {
            var line = $"{m.DisplayTo}; {m.DisplayCc}";
            var name = personName.Trim();
            var address = personAddress.Trim();
            if (name.Length == 0 && address.Length == 0) return true;
            return (name.Length > 0 && line.Contains(name, StringComparison.OrdinalIgnoreCase))
                || (address.Length > 0 && line.Contains(address, StringComparison.OrdinalIgnoreCase));
        }

        return sent
            .Where(m => m.InternetMessageId.Length > 0 && !IsPlaceholder(m.InternetMessageId))
            .Where(m => m.ReceivedUtc >= sentUtc - Slack)
            .Where(m => string.Equals(m.Subject.Trim(), subject, StringComparison.OrdinalIgnoreCase))
            .Where(ToThem)
            .OrderBy(m => m.ReceivedUtc)
            .FirstOrDefault();
    }
}
