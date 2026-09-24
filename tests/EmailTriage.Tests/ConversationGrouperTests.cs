using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class ConversationGrouperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private static MailSummary Mail(string id, string conversation, int minutes, string subject = "Budget", bool unread = false) => new()
    {
        Ref = new MailRef(id, "store"),
        InternetMessageId = $"<{id}@x>",
        Subject = subject,
        SenderName = id,
        SenderAddress = $"{id}@x.com",
        ReceivedUtc = T0.AddMinutes(minutes),
        IsUnread = unread,
        HasAttachments = false,
        ConversationKey = conversation,
    };

    [Fact]
    public void Messages_in_a_conversation_become_one_thread_newest_first()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("a", "C1", 0), Mail("b", "C1", 30) },
            new[] { Mail("me", "C1", 10) });

        var thread = Assert.Single(threads);
        Assert.Equal(new[] { "b", "me", "a" }, thread.Messages.Select(m => m.Ref.EntryId));
        Assert.Equal(2, thread.InboxMessages.Count);
    }

    [Fact]
    public void A_recent_follow_up_of_yours_moves_the_thread_to_the_top()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("old", "C1", 0), Mail("newer", "C2", 20) },
            new[] { Mail("my-follow-up", "C1", 60) });

        Assert.Equal("C1", threads[0].Key);
        Assert.True(threads[0].LatestIsMine);
        Assert.Equal("old", threads[0].LatestInbox.Ref.EntryId);
        Assert.False(threads[1].LatestIsMine);
    }

    [Fact]
    public void Threads_with_nothing_left_in_the_inbox_are_not_shown()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("a", "C1", 0) },
            new[] { Mail("filed-thread-reply", "C9", 50) });

        Assert.Equal("C1", Assert.Single(threads).Key);
    }

    [Fact]
    public void Unread_is_judged_on_the_inbox_side_only()
    {
        var thread = ConversationGrouper.Group(
            new[] { Mail("a", "C1", 0, unread: false) },
            new[] { Mail("me", "C1", 5, unread: true) })[0];

        // Your own unread sent copy does not make the conversation unread...
        Assert.False(thread.IsUnread);

        // ...and marking the conversation leaves your sent copy as it was.
        var marked = thread.WithInboxUnread(true);
        Assert.True(marked.IsUnread);
        Assert.True(marked.Messages.Single(m => m.IsSent).IsUnread);
        Assert.False(thread.WithInboxUnread(false).IsUnread);
    }

    [Fact]
    public void Marking_unread_again_touches_only_the_newest_inbox_message()
    {
        var thread = ConversationGrouper.Group(new[] { Mail("a", "C1", 0), Mail("b", "C1", 5) }, Array.Empty<MailSummary>())[0];

        var marked = thread.WithLatestInboxUnread();

        Assert.True(marked.Messages.Single(m => m.Ref.EntryId == "b").IsUnread);
        Assert.False(marked.Messages.Single(m => m.Ref.EntryId == "a").IsUnread);
    }

    [Fact]
    public void Mail_without_a_conversation_id_groups_by_subject()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("a", "", 0, "[External] Budget"), Mail("b", "", 10, "RE: [External] RE: budget") },
            new[] { Mail("me", "", 5, "FW: Budget") });

        Assert.Equal(3, Assert.Single(threads).Count);
    }

    [Theory]
    [InlineData("RE: [External] RE: Con Edison Inquiry", "con edison inquiry")]
    [InlineData("FW: Fwd: Site  Storm", "site storm")]
    [InlineData("Re[2]: Meeting", "meeting")]
    [InlineData("Invoice RE: April", "invoice re: april")]
    public void Subjects_normalise(string subject, string expected)
    {
        Assert.Equal(expected, ConversationGrouper.NormaliseSubject(subject));
    }

    [Theory]
    [InlineData("RE: FW: [External] Q3 Budget", "Q3 Budget")]
    [InlineData("Re:  Site walk   Thursday", "Site walk Thursday")]
    [InlineData("Invoice RE: April", "Invoice RE: April")]
    public void Prefixes_strip_keeping_case(string subject, string expected)
    {
        Assert.Equal(expected, ConversationGrouper.StripPrefixes(subject));
    }
}
