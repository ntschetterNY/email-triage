using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly ActionItemRepository _actions;
    private readonly SnoozeRepository _snoozes;
    private readonly ScheduledSendRepository _sends;

    public RetentionTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _actions = new ActionItemRepository(_db, _clock);
        _snoozes = new SnoozeRepository(_db, _clock);
        _sends = new ScheduledSendRepository(_db, _clock);
    }

    [Fact]
    public void A_positive_window_gives_a_cutoff_and_zero_keeps_everything()
    {
        Assert.Equal(_clock.UtcNow.AddDays(-90), Retention.Cutoff(_clock.UtcNow, 90));
        Assert.Null(Retention.Cutoff(_clock.UtcNow, 0));
        Assert.Null(Retention.Cutoff(_clock.UtcNow, -5));
    }

    private async Task<ActionItem> ItemAsync(string id, bool withWait = false)
    {
        var item = await _actions.CaptureAsync(new ActionItem
        {
            InternetMessageId = id,
            Subject = id,
            SenderName = "Alice",
            SenderAddress = "alice@corp.com",
            ReceivedUtc = _clock.UtcNow,
        }, withWait ? new CaptureRequest { Who = new Recipient("Sam", "sam@x.com") } : new CaptureRequest());
        return item;
    }

    [Fact]
    public async Task Old_done_cards_go_with_their_waits_and_everything_else_stays()
    {
        var oldDone = await ItemAsync("old-done", withWait: true);
        await _actions.SetCompletedAsync(oldDone.Id, true);

        _clock.Advance(TimeSpan.FromDays(100));

        var recentDone = await ItemAsync("recent-done");
        await _actions.SetCompletedAsync(recentDone.Id, true);
        var stillOpen = await ItemAsync("open", withWait: true);

        var removed = await _actions.PurgeCompletedBeforeAsync(_clock.UtcNow.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Null(await _actions.GetByMessageIdAsync("old-done"));
        Assert.NotNull(await _actions.GetByMessageIdAsync("recent-done"));
        Assert.NotNull(await _actions.GetByMessageIdAsync("open"));
        Assert.Single(await _actions.GetOpenAsync());
        Assert.Single(await _actions.GetCompletedAsync(10));

        // The hand-off on the purged card is gone too, not orphaned.
        var people = await _actions.GetKnownAssigneesAsync();
        Assert.Single(people);
        Assert.Single((await _actions.GetByMessageIdAsync("open"))!.Assignments);
    }

    private async Task<SnoozeEntry> SnoozeAsync(string id) => await _snoozes.AddAsync(new SnoozeEntry
    {
        InternetMessageId = id,
        EntryId = id,
        StoreId = "store",
        Subject = id,
        SenderName = "Alice",
        OriginFolderEntryId = "inbox",
        OriginFolderStoreId = "store",
        SnoozedUtc = _clock.UtcNow,
        ReturnUtc = _clock.UtcNow.AddHours(1),
    });

    [Fact]
    public async Task Only_snoozes_long_since_returned_are_removed()
    {
        var oldRestored = await SnoozeAsync("old");
        await _snoozes.MarkRestoredAsync(oldRestored.Id);

        _clock.Advance(TimeSpan.FromDays(40));

        var recentRestored = await SnoozeAsync("recent");
        await _snoozes.MarkRestoredAsync(recentRestored.Id);
        await SnoozeAsync("pending");

        var removed = await _snoozes.PurgeRestoredBeforeAsync(_clock.UtcNow.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Single(await _snoozes.GetPendingAsync());
    }

    private async Task<ScheduledSend> SendAsync(string id) => await _sends.AddAsync(new ScheduledSend
    {
        DraftEntryId = id,
        DraftStoreId = "store",
        Subject = id,
        CreatedUtc = _clock.UtcNow,
        SendAtUtc = _clock.UtcNow.AddHours(1),
    });

    [Fact]
    public async Task Only_scheduled_sends_settled_long_ago_are_removed()
    {
        var oldSent = await SendAsync("old-sent");
        await _sends.CompleteAsync(oldSent.Id, ScheduledSendState.Sent, "");
        var oldHeld = await SendAsync("old-held");
        await _sends.CompleteAsync(oldHeld.Id, ScheduledSendState.Held, "they replied");

        _clock.Advance(TimeSpan.FromDays(40));

        var recentSent = await SendAsync("recent-sent");
        await _sends.CompleteAsync(recentSent.Id, ScheduledSendState.Sent, "");
        await SendAsync("pending");

        var removed = await _sends.PurgeSettledBeforeAsync(_clock.UtcNow.AddDays(-30));

        Assert.Equal(2, removed);
        Assert.Single(await _sends.GetPendingAsync());
    }

    [Fact]
    public async Task The_sweep_applies_the_policy_and_reports_what_went()
    {
        var done = await ItemAsync("done");
        await _actions.SetCompletedAsync(done.Id, true);
        var snooze = await SnoozeAsync("snooze");
        await _snoozes.MarkRestoredAsync(snooze.Id);
        var send = await SendAsync("send");
        await _sends.CompleteAsync(send.Id, ScheduledSendState.Cancelled, "");

        _clock.Advance(TimeSpan.FromDays(100));

        var sweep = new RetentionSweep(_actions, _snoozes, _sends, _db, _clock);
        var result = await sweep.RunAsync(new RetentionPolicy(DoneDays: 90, SnoozeDays: 30, ScheduledSendDays: 0));

        Assert.Equal(1, result.DoneRemoved);
        Assert.Equal(1, result.SnoozesRemoved);
        Assert.Equal(0, result.SendsRemoved); // kept forever by the policy
        Assert.Equal(2, result.TotalRemoved);
        Assert.False(result.Vacuumed);
    }

    [Fact]
    public async Task A_large_sweep_compacts_the_file()
    {
        for (var i = 0; i < RetentionSweep.VacuumThreshold; i++)
        {
            var item = await ItemAsync($"done-{i}");
            await _actions.SetCompletedAsync(item.Id, true);
        }

        _clock.Advance(TimeSpan.FromDays(100));

        var sweep = new RetentionSweep(_actions, _snoozes, _sends, _db, _clock);
        var result = await sweep.RunAsync(RetentionPolicy.Default);

        Assert.Equal(RetentionSweep.VacuumThreshold, result.DoneRemoved);
        Assert.True(result.Vacuumed);
        Assert.Empty(await _actions.GetCompletedAsync(10));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
