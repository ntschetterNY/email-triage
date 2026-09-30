using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class ActionCaptureTests
{
    // Tuesday 29 Sep 2026, 10:00, an hour ahead of UTC.
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.FromHours(1));

    private static readonly Recipient Sam = new("Sam Lee", "sam@rimkus.com");

    [Theory]
    [InlineData("RE: Level 3 RFI", "Level 3 RFI")]
    [InlineData("Re: FW: Fwd: Pay app 7", "Pay app 7")]
    [InlineData("  AW: Zeichnungen ", "Zeichnungen")]
    [InlineData("Reply needed", "Reply needed")]
    [InlineData("", "")]
    public void Cleans_reply_and_forward_prefixes_off_a_subject(string subject, string expected)
    {
        Assert.Equal(expected, ActionCapture.CleanSubject(subject));
    }

    [Fact]
    public void Naming_someone_lands_the_card_in_waiting()
    {
        var request = new CaptureRequest { Who = Sam };

        Assert.Equal(ActionStage.Waiting, ActionCapture.ResultingStage(request, ActionStage.ToDo));
        Assert.Equal(ActionStage.Waiting, ActionCapture.ResultingStage(request, ActionStage.Doing));
        Assert.Equal(ActionStage.Waiting, ActionCapture.ResultingStage(request, ActionStage.Done));
    }

    [Fact]
    public void Naming_nobody_keeps_the_stage_but_reopens_a_done_card()
    {
        var request = new CaptureRequest();

        Assert.Equal(ActionStage.ToDo, ActionCapture.ResultingStage(request, ActionStage.ToDo));
        Assert.Equal(ActionStage.Doing, ActionCapture.ResultingStage(request, ActionStage.Doing));
        Assert.Equal(ActionStage.Waiting, ActionCapture.ResultingStage(request, ActionStage.Waiting));
        Assert.Equal(ActionStage.ToDo, ActionCapture.ResultingStage(request, ActionStage.Done));
    }

    [Fact]
    public void A_blank_name_is_not_a_wait()
    {
        Assert.False(new CaptureRequest { Who = new Recipient("  ", "") }.HasWait);
        Assert.True(new CaptureRequest { Who = Sam }.HasWait);
    }

    [Fact]
    public void Default_follow_up_is_a_few_days_before_the_due_date()
    {
        var due = Now.AddDays(7); // Tue 6 Oct

        var chase = ActionCapture.DefaultFollowUp(due, Now, beforeDueDays: 2, afterDays: 5);

        Assert.Equal(new DateTime(2026, 10, 4), chase!.Value.ToOffset(Now.Offset).Date);
    }

    [Fact]
    public void Default_follow_up_falls_back_to_the_due_day_when_the_gap_is_too_short()
    {
        var due = Now.AddDays(1);

        var chase = ActionCapture.DefaultFollowUp(due, Now, beforeDueDays: 2, afterDays: 5);

        Assert.Equal(due, chase);
    }

    [Fact]
    public void Default_follow_up_without_a_due_date_is_the_usual_interval_from_now()
    {
        var chase = ActionCapture.DefaultFollowUp(null, Now, beforeDueDays: 2, afterDays: 5);

        Assert.Equal(Now.AddDays(5).ToUniversalTime(), chase);
    }

    [Fact]
    public void Default_follow_up_for_something_due_today_uses_the_usual_interval()
    {
        var chase = ActionCapture.DefaultFollowUp(Now.AddHours(3), Now, beforeDueDays: 2, afterDays: 5);

        Assert.Equal(Now.AddDays(5).ToUniversalTime(), chase);
    }

    [Fact]
    public void Default_follow_up_is_off_when_the_interval_is_zero()
    {
        Assert.Null(ActionCapture.DefaultFollowUp(null, Now, beforeDueDays: 2, afterDays: 0));
    }

    [Fact]
    public void Describes_where_the_card_lands_and_when()
    {
        var request = new CaptureRequest
        {
            Who = Sam,
            DueUtc = Now.AddDays(3),
            FollowUpUtc = Now.AddDays(1),
            Priority = ActionPriority.High,
        };

        var line = ActionCapture.Describe(request, ActionStage.ToDo, Now);

        Assert.Equal("Lands in Waiting on Sam Lee · due Fri 2 Oct · follow up Wed 30 Sep · high priority", line);
    }

    [Fact]
    public void Describes_a_plain_flag_as_to_do()
    {
        Assert.Equal("Lands in To do", ActionCapture.Describe(new CaptureRequest(), ActionStage.ToDo, Now));
    }

    [Fact]
    public void Describes_a_blocker_as_such()
    {
        var line = ActionCapture.Describe(new CaptureRequest { Who = Sam, IsBlocker = true }, ActionStage.Doing, Now);

        Assert.StartsWith("Lands in Waiting on Sam Lee (blocker)", line);
    }
}

public class ActionCaptureRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"triage-test-{Guid.NewGuid():N}.db");

    private readonly Database _db;
    private readonly FakeClock _clock = new();
    private readonly ActionItemRepository _repo;

    public ActionCaptureRepositoryTests()
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
        Subject = "RE: Contract review",
        SenderName = "Alice",
        SenderAddress = "alice@corp.com",
        ReceivedUtc = _clock.UtcNow,
    };

    private static readonly Recipient Sam = new("Sam Lee", "sam@rimkus.com");

    [Fact]
    public async Task Captures_a_new_item_with_a_hand_off_in_waiting()
    {
        var due = _clock.UtcNow.AddDays(5);
        var chase = _clock.UtcNow.AddDays(3);

        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest
        {
            Title = "Get the redlines back",
            Who = Sam,
            DueUtc = due,
            FollowUpUtc = chase,
            Priority = ActionPriority.High,
            Notes = "Legal wants it before the board meeting",
        });

        Assert.True(item.Id > 0);
        Assert.Equal("Get the redlines back", item.Title);
        Assert.Equal("Get the redlines back", item.DisplayTitle);
        Assert.Equal(ActionStage.Waiting, item.Stage);
        Assert.Equal(ActionPriority.High, item.Priority);
        Assert.Equal(due, item.DueUtc);
        Assert.Equal("Legal wants it before the board meeting", item.Notes);

        var handoff = Assert.Single(item.Assignments);
        Assert.Equal("Sam Lee", handoff.PersonName);
        Assert.Equal("sam@rimkus.com", handoff.PersonEmail);
        Assert.Equal("Get the redlines back", handoff.Task);
        Assert.Equal(chase, handoff.DueUtc);
        Assert.False(handoff.IsDone);
        Assert.Empty(item.Blockers);
    }

    [Fact]
    public async Task A_blocker_is_filed_as_a_blocking_task_with_the_subject_when_no_title_was_typed()
    {
        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = Sam, IsBlocker = true });

        Assert.Equal(ActionStage.Waiting, item.Stage);
        var blocker = Assert.Single(item.Blockers);
        Assert.Equal("Sam Lee", blocker.WaitingOn);
        Assert.Equal("RE: Contract review", blocker.Description);
        Assert.Empty(item.Assignments);
        Assert.Equal("", item.Title);
        Assert.Equal("RE: Contract review", item.DisplayTitle);
    }

    [Fact]
    public async Task An_empty_request_is_a_plain_flag()
    {
        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest());

        Assert.Equal(ActionStage.ToDo, item.Stage);
        Assert.Null(item.DueUtc);
        Assert.Empty(item.Blockers);
        Assert.Empty(item.Assignments);
        Assert.Single(await _repo.GetOpenAsync());
    }

    [Fact]
    public async Task Capturing_again_adds_to_the_card_without_wiping_what_was_there()
    {
        var first = await _repo.CaptureAsync(NewItem(), new CaptureRequest
        {
            Title = "Chase the redlines",
            Notes = "Background here",
            DueUtc = _clock.UtcNow.AddDays(2),
        });

        var again = await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = Sam });

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Chase the redlines", again.Title);
        Assert.Equal("Background here", again.Notes);
        Assert.Equal(first.DueUtc, again.DueUtc);
        Assert.Equal(ActionStage.Waiting, again.Stage);
        Assert.Single(again.Assignments);
        Assert.Single(await _repo.GetOpenAsync());
    }

    [Fact]
    public async Task Capturing_a_done_card_reopens_it()
    {
        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest());
        await _repo.SetCompletedAsync(item.Id, true);

        var reopened = await _repo.CaptureAsync(NewItem(), new CaptureRequest());

        Assert.False(reopened.IsComplete);
        Assert.Equal(ActionStage.ToDo, reopened.Stage);
    }

    [Fact]
    public async Task Restoring_a_snapshot_undoes_a_later_capture()
    {
        var before = await _repo.CaptureAsync(NewItem(), new CaptureRequest
        {
            Title = "Original title",
            Priority = ActionPriority.Low,
        });

        await _repo.CaptureAsync(NewItem(), new CaptureRequest
        {
            Title = "Changed title",
            Who = Sam,
            DueUtc = _clock.UtcNow.AddDays(1),
            Priority = ActionPriority.High,
            Notes = "new notes",
        });

        await _repo.RestoreAsync(before);

        var restored = await _repo.GetByMessageIdAsync("mid-1");
        Assert.NotNull(restored);
        Assert.Equal("Original title", restored!.Title);
        Assert.Equal(ActionPriority.Low, restored.Priority);
        Assert.Null(restored.DueUtc);
        Assert.Equal("", restored.Notes);
        Assert.Equal(ActionStage.ToDo, restored.Stage);
        Assert.Empty(restored.Assignments);
    }

    [Fact]
    public async Task Marking_done_closes_its_open_waits()
    {
        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = Sam });
        await _repo.AddBlockerAsync(new BlockingTask { ActionItemId = item.Id, Description = "Drawings", WaitingOn = "Dina" });

        await _repo.SetCompletedAsync(item.Id, true);

        var done = (await _repo.GetCompletedAsync(10)).Single();
        Assert.True(done.IsComplete);
        Assert.False(done.IsWaiting);
        Assert.All(done.Assignments, a => Assert.True(a.IsDone));
        Assert.All(done.Blockers, b => Assert.True(b.IsResolved));
    }

    [Fact]
    public async Task Restoring_a_snapshot_reopens_the_waits_that_were_open()
    {
        await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = Sam });
        var before = (await _repo.GetByMessageIdAsync("mid-1"))!;

        await _repo.SetCompletedAsync(before.Id, true);
        await _repo.RestoreAsync(before);

        var restored = (await _repo.GetByMessageIdAsync("mid-1"))!;
        Assert.False(restored.IsComplete);
        Assert.Equal(ActionStage.Waiting, restored.Stage);
        Assert.True(restored.IsWaiting);
        Assert.False(Assert.Single(restored.Assignments).IsDone);
        Assert.Single(await _repo.GetOpenAsync());
    }

    [Fact]
    public async Task Restoring_keeps_the_waits_the_snapshot_already_had()
    {
        var item = await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = Sam });
        var snapshot = (await _repo.GetByMessageIdAsync("mid-1"))!;

        await _repo.CaptureAsync(NewItem(), new CaptureRequest { Who = new Recipient("Dina", "dina@x.com"), IsBlocker = true });

        await _repo.RestoreAsync(snapshot);

        var restored = (await _repo.GetByMessageIdAsync("mid-1"))!;
        Assert.Equal(item.Assignments.Single().Id, Assert.Single(restored.Assignments).Id);
        Assert.Empty(restored.Blockers);
        Assert.Equal(ActionStage.Waiting, restored.Stage);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
