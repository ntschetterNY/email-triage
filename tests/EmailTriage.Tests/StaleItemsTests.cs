using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class StaleItemsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private static ActionItem Item(long id, int idleDays, bool touched = true) => new()
    {
        Id = id,
        InternetMessageId = $"<{id}@x>",
        Subject = $"Item {id}",
        SenderName = "Sender",
        SenderAddress = "s@x.com",
        CreatedUtc = Now.AddDays(-idleDays - 10),
        TouchedUtc = touched ? Now.AddDays(-idleDays) : null,
        ReceivedUtc = Now.AddDays(-idleDays - 10),
    };

    [Fact]
    public void Idle_days_count_from_the_touched_stamp_or_creation_when_never_touched()
    {
        Assert.Equal(12, StaleItems.DaysIdle(Item(1, 12), Now));
        Assert.Equal(22, StaleItems.DaysIdle(Item(2, 12, touched: false), Now));
        Assert.Equal(0, StaleItems.DaysIdle(Item(3, -3), Now));
    }

    [Fact]
    public void Finds_open_cards_idle_past_the_threshold_longest_first()
    {
        var items = new[] { Item(1, 31), Item(2, 5), Item(3, 60), Item(4, 30) };

        var stale = StaleItems.Find(items, 30, Now);

        Assert.Equal(new long[] { 3, 1, 4 }, stale.Select(i => i.Id));
    }

    [Fact]
    public void Leaves_out_finished_cards_and_cards_already_in_follow_up()
    {
        var done = Item(1, 40);
        done.CompletedUtc = Now;
        var chasing = Item(2, 40);
        chasing.ScheduledFollowUp = "follow up with Sam today";
        var plain = Item(3, 40);

        var stale = StaleItems.Find(new[] { done, chasing, plain }, 30, Now);

        Assert.Equal(3, Assert.Single(stale).Id);
    }

    [Fact]
    public void Zero_days_turns_the_feature_off()
    {
        Assert.Empty(StaleItems.Find(new[] { Item(1, 400) }, 0, Now));
    }
}

public class TouchedStampTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly ActionItemRepository _repo;

    public TouchedStampTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _repo = new ActionItemRepository(_db, _clock);
    }

    private async Task<ActionItem> NewItemAsync() => await _repo.UpsertAsync(new ActionItem
    {
        InternetMessageId = "mid-1",
        Subject = "Contract review",
        SenderName = "Alice",
        SenderAddress = "alice@corp.com",
        ReceivedUtc = _clock.UtcNow,
    });

    private async Task<DateTimeOffset?> TouchedAsync() => (await _repo.GetByMessageIdAsync("mid-1"))!.TouchedUtc;

    [Fact]
    public async Task A_new_card_is_touched_when_it_is_made()
    {
        await NewItemAsync();
        Assert.Equal(_clock.UtcNow, await TouchedAsync());
    }

    [Fact]
    public async Task Working_on_a_card_moves_its_touched_stamp()
    {
        var item = await NewItemAsync();
        var made = _clock.UtcNow;

        _clock.Advance(TimeSpan.FromDays(3));
        await _repo.UpdateNotesAsync(item.Id, "Called them");
        Assert.Equal(made.AddDays(3), await TouchedAsync());

        _clock.Advance(TimeSpan.FromDays(3));
        await _repo.AddBlockerAsync(new BlockingTask { ActionItemId = item.Id, Description = "Drawings" });
        Assert.Equal(made.AddDays(6), await TouchedAsync());

        _clock.Advance(TimeSpan.FromDays(3));
        await _repo.UpdateStageAsync(item.Id, ActionStage.Doing);
        Assert.Equal(made.AddDays(9), await TouchedAsync());

        _clock.Advance(TimeSpan.FromDays(3));
        await _repo.MarkFollowedUpAsync(item.Id);
        Assert.Equal(made.AddDays(12), await TouchedAsync());
    }

    [Fact]
    public async Task Keeping_a_card_in_review_touches_it_without_changing_anything_else()
    {
        var item = await NewItemAsync();
        await _repo.UpdateNotesAsync(item.Id, "Notes stay");
        _clock.Advance(TimeSpan.FromDays(40));

        await _repo.TouchAsync(item.Id);

        var kept = (await _repo.GetByMessageIdAsync("mid-1"))!;
        Assert.Equal(_clock.UtcNow, kept.TouchedUtc);
        Assert.Equal("Notes stay", kept.Notes);
        Assert.Equal(0, StaleItems.DaysIdle(kept, _clock.UtcNow));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
