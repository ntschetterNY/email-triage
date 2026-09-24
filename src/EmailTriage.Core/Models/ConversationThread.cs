namespace EmailTriage.Core.Models;

/// <summary>
/// One conversation in the triage list: every recent message in the thread,
/// from the Inbox and from Sent Items, newest first. A thread only appears
/// while at least one of its messages is still in the Inbox.
/// </summary>
public sealed record ConversationThread(string Key, IReadOnlyList<MailSummary> Messages)
{
    public MailSummary Latest => Messages[0];

    /// <summary>The Inbox side of the thread: what triage actions move, read and flag.</summary>
    public IReadOnlyList<MailSummary> InboxMessages => Messages.Where(m => !m.IsSent).ToList();

    /// <summary>The newest message someone else sent - the one a reply answers.</summary>
    public MailSummary LatestInbox => Messages.First(m => !m.IsSent);

    /// <summary>Your own follow-up is the newest thing in the thread.</summary>
    public bool LatestIsMine => Latest.IsSent;

    public DateTimeOffset LastActivityUtc => Latest.ReceivedUtc;

    public bool IsUnread => Messages.Any(m => !m.IsSent && m.IsUnread);

    public bool HasAttachments => Messages.Any(m => m.HasAttachments);

    public int Count => Messages.Count;

    /// <summary>A copy with every Inbox message marked read or unread.</summary>
    public ConversationThread WithInboxUnread(bool unread) =>
        this with { Messages = Messages.Select(m => m.IsSent ? m : m with { IsUnread = unread }).ToList() };

    /// <summary>A copy with only the newest Inbox message marked unread.</summary>
    public ConversationThread WithLatestInboxUnread()
    {
        var latest = LatestInbox;
        return this with
        {
            Messages = Messages.Select(m => m.IsSent ? m : m with { IsUnread = ReferenceEquals(m, latest) }).ToList(),
        };
    }
}
