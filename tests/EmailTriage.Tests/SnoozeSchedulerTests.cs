using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class SnoozeSchedulerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly FakeMailStore _store = new();
    private readonly SnoozeRepository _repo;

    public SnoozeSchedulerTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _repo = new SnoozeRepository(_db, _clock);
        _store.ConnectAsync().GetAwaiter().GetResult();
    }

    private SnoozeScheduler NewScheduler() => new(_store, _repo, _clock);

    private async Task<SnoozeEntry> AddSnoozeAsync(TimeSpan dueIn, string messageId = "mid-1")
        => await _repo.AddAsync(new SnoozeEntry
        {
            InternetMessageId = messageId,
            EntryId = "entry-1",
            StoreId = "store",
            Subject = "Quarterly numbers",
            SenderName = "Alice",
            OriginFolderEntryId = "inbox",
            OriginFolderStoreId = "store",
            OriginFolderPath = "Mailbox\\Inbox",
            SnoozedUtc = _clock.UtcNow,
            ReturnUtc = _clock.UtcNow + dueIn,
        });

    [Fact]
    public async Task Leaves_snoozes_alone_before_they_are_due()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(2));

        var restored = await NewScheduler().ProcessDueAsync();

        Assert.Equal(0, restored);
        Assert.Equal(0, _store.MoveCount);
    }

    [Fact]
    public async Task Returns_a_snooze_once_it_comes_due()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(2));
        _clock.Advance(TimeSpan.FromHours(3));

        var restored = await NewScheduler().ProcessDueAsync();

        Assert.Equal(1, restored);
        Assert.Single(_store.Moves);
        Assert.Equal("Mailbox\\Inbox", _store.Moves[0].Target.Path);
    }

    [Fact]
    public async Task Does_not_restore_the_same_item_twice()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(2));

        var scheduler = NewScheduler();
        await scheduler.ProcessDueAsync();
        var second = await scheduler.ProcessDueAsync();

        Assert.Equal(0, second);
        Assert.Single(_store.Moves);
    }

    [Fact]
    public async Task Catches_up_on_everything_missed_while_the_app_was_closed()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(1), "mid-1");
        await AddSnoozeAsync(TimeSpan.FromHours(2), "mid-2");
        await AddSnoozeAsync(TimeSpan.FromHours(3), "mid-3");

        // Simulate the app being shut for a day.
        _clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(3, await NewScheduler().ProcessDueAsync());
    }

    [Fact]
    public async Task Prefers_the_message_id_when_the_entry_id_has_gone_stale()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(1), "mid-stable");
        _store.ByMessageId["mid-stable"] = new MailRef("entry-relocated", "store");

        _clock.Advance(TimeSpan.FromHours(2));
        await NewScheduler().ProcessDueAsync();

        // The item the user moved by hand is the one that gets restored.
        Assert.Equal("entry-relocated", _store.Moves[0].Mail.EntryId);
    }

    [Fact]
    public async Task Records_a_failure_instead_of_losing_the_snooze()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(2));

        _store.NextMoveFailure = new InvalidOperationException("Outlook is busy");

        var scheduler = NewScheduler();
        string? reported = null;
        scheduler.RestoreFailed += (_, e) => reported = e.Error;

        var restored = await scheduler.ProcessDueAsync();

        Assert.Equal(0, restored);
        Assert.Equal("Outlook is busy", reported);

        // Still pending, so the next sweep will try again.
        Assert.Single(await _repo.GetPendingAsync());
    }

    [Fact]
    public async Task Raises_an_event_naming_what_came_back()
    {
        await AddSnoozeAsync(TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(2));

        var scheduler = NewScheduler();
        SnoozeRestored? seen = null;
        scheduler.Restored += (_, e) => seen = e;

        await scheduler.ProcessDueAsync();

        Assert.NotNull(seen);
        Assert.Equal("Quarterly numbers", seen!.Entry.Subject);
    }

    [Fact]
    public async Task Does_nothing_while_the_store_is_disconnected()
    {
        var disconnected = new FakeMailStore();   // never connected
        await AddSnoozeAsync(TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(2));

        var scheduler = new SnoozeScheduler(disconnected, _repo, _clock);

        Assert.Equal(0, await scheduler.ProcessDueAsync());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
