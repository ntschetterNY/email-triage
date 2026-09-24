using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using Xunit;

namespace EmailTriage.Tests;

public class ActionItemRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly ActionItemRepository _repo;

    public ActionItemRepositoryTests()
    {
        _db = new Database(_dbPath);
        _db.Migrate();
        _repo = new ActionItemRepository(_db, _clock);
    }

    private ActionItem NewItem(string messageId = "mid-1") => new()
    {
        InternetMessageId = messageId,
        EntryId = "entry-1",
        StoreId = "store",
        Subject = "Contract review",
        SenderName = "Alice",
        SenderAddress = "alice@corp.com",
        ReceivedUtc = _clock.UtcNow,
        CreatedUtc = _clock.UtcNow,
    };

    [Fact]
    public async Task Migration_is_idempotent()
    {
        _db.Migrate();
        _db.Migrate();

        Assert.Empty(await _repo.GetOpenAsync());
    }

    [Fact]
    public async Task Round_trips_an_action_item()
    {
        var saved = await _repo.UpsertAsync(NewItem());

        Assert.True(saved.Id > 0);

        var loaded = await _repo.GetByMessageIdAsync("mid-1");
        Assert.NotNull(loaded);
        Assert.Equal("Contract review", loaded!.Subject);
        Assert.False(loaded.IsComplete);
    }

    [Fact]
    public async Task Reflagging_the_same_mail_does_not_duplicate_or_wipe_notes()
    {
        var first = await _repo.UpsertAsync(NewItem());
        await _repo.UpdateNotesAsync(first.Id, "Chase legal before Friday");

        // Same Message-ID arriving again, e.g. after a refresh.
        var second = await _repo.UpsertAsync(NewItem());

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _repo.GetOpenAsync());

        var loaded = await _repo.GetByMessageIdAsync("mid-1");
        Assert.Equal("Chase legal before Friday", loaded!.Notes);
    }

    [Fact]
    public async Task Completing_removes_an_item_from_the_open_list()
    {
        var item = await _repo.UpsertAsync(NewItem());

        await _repo.SetCompletedAsync(item.Id, true);
        Assert.Empty(await _repo.GetOpenAsync());

        var done = await _repo.GetCompletedAsync(10);
        Assert.Single(done);
        Assert.True(done[0].IsComplete);

        await _repo.SetCompletedAsync(item.Id, false);
        Assert.Single(await _repo.GetOpenAsync());
    }

    [Fact]
    public async Task Blockers_load_with_their_parent_and_mark_it_blocked()
    {
        var item = await _repo.UpsertAsync(NewItem());

        await _repo.AddBlockerAsync(new BlockingTask
        {
            ActionItemId = item.Id,
            Description = "Waiting on signed NDA",
            WaitingOn = "Legal",
            CreatedUtc = _clock.UtcNow,
        });

        var loaded = (await _repo.GetOpenAsync()).Single();
        Assert.Single(loaded.Blockers);
        Assert.True(loaded.IsBlocked);

        await _repo.SetBlockerResolvedAsync(loaded.Blockers[0].Id, true);

        Assert.False((await _repo.GetOpenAsync()).Single().IsBlocked);
    }

    [Fact]
    public async Task Assignments_track_their_own_completion_and_draft_state()
    {
        var item = await _repo.UpsertAsync(NewItem());

        var assignment = await _repo.AddAssignmentAsync(new Assignment
        {
            ActionItemId = item.Id,
            PersonName = "Bob Jones",
            PersonEmail = "bob@corp.com",
            Task = "Send the revised figures",
            CreatedUtc = _clock.UtcNow,
        });

        var loaded = (await _repo.GetOpenAsync()).Single();
        Assert.Equal(1, loaded.OpenAssignmentCount);
        Assert.False(loaded.Assignments[0].HasBeenDrafted);

        await _repo.MarkAssignmentDraftedAsync(assignment.Id);
        await _repo.SetAssignmentDoneAsync(assignment.Id, true);

        var after = (await _repo.GetOpenAsync()).Single();
        Assert.Equal(0, after.OpenAssignmentCount);
        Assert.True(after.Assignments[0].HasBeenDrafted);
    }

    [Fact]
    public async Task Deleting_an_item_removes_its_children()
    {
        var item = await _repo.UpsertAsync(NewItem());

        await _repo.AddBlockerAsync(new BlockingTask
        {
            ActionItemId = item.Id,
            Description = "Blocked",
            CreatedUtc = _clock.UtcNow,
        });

        await _repo.DeleteAsync(item.Id);

        Assert.Empty(await _repo.GetOpenAsync());
        Assert.Null(await _repo.GetByMessageIdAsync("mid-1"));
    }

    [Fact]
    public async Task Location_can_be_refreshed_after_the_mail_moves()
    {
        await _repo.UpsertAsync(NewItem());
        await _repo.UpdateLocationAsync("mid-1", "entry-2", "store-2");

        var loaded = await _repo.GetByMessageIdAsync("mid-1");
        Assert.Equal("entry-2", loaded!.EntryId);
        Assert.Equal("store-2", loaded.StoreId);
    }

    [Fact]
    public async Task Known_assignees_are_offered_for_reuse()
    {
        var item = await _repo.UpsertAsync(NewItem());

        await _repo.AddAssignmentAsync(new Assignment
        {
            ActionItemId = item.Id,
            PersonName = "Bob Jones",
            PersonEmail = "bob@corp.com",
            Task = "One",
            CreatedUtc = _clock.UtcNow,
        });

        await _repo.AddAssignmentAsync(new Assignment
        {
            ActionItemId = item.Id,
            PersonName = "Bob Jones",
            PersonEmail = "bob@corp.com",
            Task = "Two",
            CreatedUtc = _clock.UtcNow,
        });

        var people = await _repo.GetKnownAssigneesAsync();

        Assert.Single(people);
        Assert.Equal("Bob Jones", people[0].Name);
        Assert.Equal("bob@corp.com", people[0].Email);
    }

    [Fact]
    public async Task Dates_survive_a_round_trip_through_sqlite()
    {
        var item = NewItem();
        item.ReceivedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await _repo.UpsertAsync(item);

        var loaded = await _repo.GetByMessageIdAsync("mid-1");
        Assert.Equal(item.ReceivedUtc, loaded!.ReceivedUtc);
    }

    [Fact]
    public async Task Priority_round_trips_as_an_enum()
    {
        var item = await _repo.UpsertAsync(NewItem());
        await _repo.UpdatePriorityAsync(item.Id, ActionPriority.High);

        var loaded = await _repo.GetByMessageIdAsync("mid-1");
        Assert.Equal(ActionPriority.High, loaded!.Priority);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
