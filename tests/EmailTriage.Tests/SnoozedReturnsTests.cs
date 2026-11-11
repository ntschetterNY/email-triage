using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class SnoozedReturnsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static MailSummary Mail(string id, string conversation, int minutes) => new()
    {
        Ref = new MailRef(id, "store"),
        InternetMessageId = $"<{id}@x>",
        Subject = "Budget",
        SenderName = id,
        SenderAddress = $"{id}@x.com",
        ReceivedUtc = T0.AddMinutes(minutes),
        IsUnread = false,
        HasAttachments = false,
        ConversationKey = conversation,
    };

    private static SnoozeEntry Returned(string id, DateTimeOffset? when) => new()
    {
        InternetMessageId = $"<{id}@x>",
        EntryId = id,
        StoreId = "store",
        Subject = "Budget",
        SenderName = id,
        OriginFolderEntryId = "inbox",
        OriginFolderStoreId = "store",
        ReturnUtc = when ?? T0,
        RestoredUtc = when,
        ReturnedUtc = when,
    };

    [Fact]
    public void A_mail_back_from_snooze_goes_to_the_top()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("old", "C1", 0), Mail("newer", "C2", 60) }, Array.Empty<MailSummary>());

        var back = SnoozedReturns.ByThread(threads, new[] { Returned("old", T0.AddHours(5)) });
        var ordered = SnoozedReturns.Order(threads, back);

        Assert.Equal(new[] { "C1", "C2" }, ordered.Select(t => t.Key));
        Assert.True(back.ContainsKey("C1"));
        Assert.False(back.ContainsKey("C2"));
    }

    [Fact]
    public void Mail_arriving_after_the_return_still_lands_above_it()
    {
        var threads = ConversationGrouper.Group(
            new[] { Mail("old", "C1", 0), Mail("fresh", "C2", 600) }, Array.Empty<MailSummary>());

        var ordered = SnoozedReturns.Order(
            threads, SnoozedReturns.ByThread(threads, new[] { Returned("old", T0.AddHours(5)) }));

        Assert.Equal(new[] { "C2", "C1" }, ordered.Select(t => t.Key));
    }

    [Fact]
    public void An_undone_or_abandoned_snooze_does_not_count()
    {
        var threads = ConversationGrouper.Group(new[] { Mail("old", "C1", 0) }, Array.Empty<MailSummary>());

        Assert.Empty(SnoozedReturns.ByThread(threads, new[] { Returned("old", null) }));
    }

    [Fact]
    public async Task Only_mail_the_scheduler_returned_is_listed_as_returned()
    {
        var path = Path.Combine(Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            var clock = new FakeClock();
            var repo = new SnoozeRepository(db, clock);

            var back = await repo.AddAsync(Returned("back", null));
            var undone = await repo.AddAsync(Returned("undone", null));

            await repo.MarkRestoredAsync(back.Id);
            await repo.CancelAsync(undone.Id);

            var listed = await repo.GetReturnedSinceAsync(clock.UtcNow - SnoozedReturns.TagFor);
            Assert.Equal("<back@x>", Assert.Single(listed).InternetMessageId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
