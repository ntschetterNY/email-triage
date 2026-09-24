using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public sealed class FakeScheduledSends : IScheduledSendRepository
{
    public List<ScheduledSend> All { get; } = new();

    public Task<ScheduledSend> AddAsync(ScheduledSend entry, CancellationToken ct = default)
    {
        entry.Id = All.Count + 1;
        All.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ScheduledSend>> GetPendingAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScheduledSend>>(All.Where(e => e.State == ScheduledSendState.Pending).ToList());

    public Task<IReadOnlyList<ScheduledSend>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ScheduledSend>>(
            All.Where(e => e.State == ScheduledSendState.Pending && e.SendAtUtc <= now).ToList());

    public Task CompleteAsync(long id, ScheduledSendState state, string note, CancellationToken ct = default)
    {
        var e = All.Single(x => x.Id == id);
        e.State = state;
        e.Note = note;
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(long id, string error, CancellationToken ct = default)
    {
        var e = All.Single(x => x.Id == id);
        e.Note = error;
        e.FailureCount++;
        return Task.CompletedTask;
    }
}

public class ScheduledSenderTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeMailStore _store = new();
    private readonly FakeScheduledSends _repo = new();

    public ScheduledSenderTests() => _store.ConnectAsync().Wait();

    private ScheduledSender Sender() => new(_store, _repo, _clock);

    private async Task<ScheduledSend> ScheduleAsync(string id, TimeSpan inFuture, bool holdIfReplied = true)
    {
        _store.SavedDrafts[id] = SavedDraftState.Waiting;
        return await _repo.AddAsync(new ScheduledSend
        {
            DraftEntryId = id,
            DraftStoreId = "store",
            Subject = $"Follow-up {id}",
            CreatedUtc = _clock.UtcNow,
            SendAtUtc = _clock.UtcNow + inFuture,
            HoldIfReplied = holdIfReplied,
        });
    }

    [Fact]
    public async Task Nothing_is_sent_before_its_time()
    {
        await ScheduleAsync("a", TimeSpan.FromHours(2));

        await Sender().ProcessDueAsync();

        Assert.Empty(_store.SentDrafts);
    }

    [Fact]
    public async Task Sends_at_its_time_when_nobody_replied()
    {
        var entry = await ScheduleAsync("a", TimeSpan.FromHours(2));
        _clock.Advance(TimeSpan.FromHours(2));

        await Sender().ProcessDueAsync();

        Assert.Equal(new[] { "a" }, _store.SentDrafts);
        Assert.Equal(ScheduledSendState.Sent, entry.State);
    }

    [Fact]
    public async Task A_reply_holds_the_follow_up_and_opens_it_for_review()
    {
        var entry = await ScheduleAsync("a", TimeSpan.FromHours(2));
        _store.RepliedTo.Add("a");
        _clock.Advance(TimeSpan.FromHours(3));

        ScheduledSendOutcome? outcome = null;
        var sender = Sender();
        sender.Settled += (_, o) => outcome = o;
        await sender.ProcessDueAsync();

        Assert.Empty(_store.SentDrafts);
        Assert.Equal(new[] { "a" }, _store.ShownDrafts);
        Assert.Equal(ScheduledSendState.Held, entry.State);
        Assert.Equal(ScheduledSendState.Held, outcome?.State);
    }

    [Fact]
    public async Task A_reply_is_ignored_when_the_send_is_unconditional()
    {
        await ScheduleAsync("a", TimeSpan.FromHours(1), holdIfReplied: false);
        _store.RepliedTo.Add("a");
        _clock.Advance(TimeSpan.FromHours(1));

        await Sender().ProcessDueAsync();

        Assert.Equal(new[] { "a" }, _store.SentDrafts);
    }

    [Fact]
    public async Task Missed_by_a_little_while_closed_still_sends()
    {
        await ScheduleAsync("a", TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(5));

        await Sender().ProcessDueAsync();

        Assert.Equal(new[] { "a" }, _store.SentDrafts);
    }

    [Fact]
    public async Task Missed_by_a_lot_is_held_rather_than_sent_stale()
    {
        var entry = await ScheduleAsync("a", TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromDays(2));

        await Sender().ProcessDueAsync();

        Assert.Empty(_store.SentDrafts);
        Assert.Equal(ScheduledSendState.Held, entry.State);
        Assert.Contains("a", _store.ShownDrafts);
    }

    [Theory]
    [InlineData(SavedDraftState.Deleted, ScheduledSendState.Cancelled)]
    [InlineData(SavedDraftState.Missing, ScheduledSendState.Cancelled)]
    [InlineData(SavedDraftState.AlreadySent, ScheduledSendState.Sent)]
    public async Task A_draft_dealt_with_in_Outlook_is_left_alone(SavedDraftState draftState, ScheduledSendState expected)
    {
        var entry = await ScheduleAsync("a", TimeSpan.FromHours(1));
        _store.SavedDrafts["a"] = draftState;
        _clock.Advance(TimeSpan.FromHours(1));

        await Sender().ProcessDueAsync();

        Assert.Empty(_store.SentDrafts);
        Assert.Empty(_store.ShownDrafts);
        Assert.Equal(expected, entry.State);
    }

    [Fact]
    public async Task A_failed_send_stays_pending_and_retries()
    {
        var entry = await ScheduleAsync("a", TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromHours(1));
        _store.NextSendFailure = new InvalidOperationException("Outlook busy");

        var sender = Sender();
        await sender.ProcessDueAsync();
        Assert.Equal(ScheduledSendState.Pending, entry.State);
        Assert.Equal(1, entry.FailureCount);

        await sender.ProcessDueAsync();
        Assert.Equal(ScheduledSendState.Sent, entry.State);
    }

    [Fact]
    public async Task Repository_round_trips_through_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sched-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            var repo = new ScheduledSendRepository(db, _clock);

            var added = await repo.AddAsync(new ScheduledSend
            {
                DraftEntryId = "e1", DraftStoreId = "s1", Subject = "Hi",
                SendAtUtc = _clock.UtcNow.AddHours(1), HoldIfReplied = true,
            });

            Assert.Empty(await repo.GetDueAsync(_clock.UtcNow));
            var due = Assert.Single(await repo.GetDueAsync(_clock.UtcNow.AddHours(2)));
            Assert.True(due.HoldIfReplied);
            Assert.Equal(_clock.UtcNow.AddHours(1), due.SendAtUtc);

            await repo.CompleteAsync(added.Id, ScheduledSendState.Held, "They replied");
            Assert.Empty(await repo.GetPendingAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }
}
