using EmailTriage.Core.Data;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class ActionWorkflowTests
{
    private static ActionItem Item(long id, ActionStage stage = ActionStage.Doing) => new()
    {
        Id = id,
        InternetMessageId = $"<{id}@x>",
        Subject = $"Item {id}",
        SenderName = "Sender",
        SenderAddress = "s@x.com",
        Stage = stage,
    };

    [Theory]
    [InlineData(ActionStage.ToDo, ActionStage.Waiting)]
    [InlineData(ActionStage.Doing, ActionStage.Waiting)]
    public void Adding_a_wait_moves_the_item_to_waiting(ActionStage from, ActionStage expected)
    {
        Assert.Equal(expected, ActionWorkflow.AfterWaitAdded(Item(1, from)));
    }

    [Theory]
    [InlineData(ActionStage.Waiting)]
    [InlineData(ActionStage.Done)]
    public void Adding_a_wait_leaves_waiting_and_done_alone(ActionStage from)
    {
        Assert.Null(ActionWorkflow.AfterWaitAdded(Item(1, from)));
    }

    [Fact]
    public void Clearing_the_last_wait_brings_it_back_to_doing()
    {
        var item = Item(1, ActionStage.Waiting);
        item.Blockers.Add(new BlockingTask { Description = "Drawings", ResolvedUtc = DateTimeOffset.UtcNow });

        Assert.Equal(ActionStage.Doing, ActionWorkflow.AfterWaitCleared(item));
    }

    [Fact]
    public void Clearing_one_of_several_waits_keeps_it_waiting()
    {
        var item = Item(1, ActionStage.Waiting);
        item.Blockers.Add(new BlockingTask { Description = "Drawings", ResolvedUtc = DateTimeOffset.UtcNow });
        item.Assignments.Add(new Assignment { PersonName = "Dina", Task = "Price it" });

        Assert.Null(ActionWorkflow.AfterWaitCleared(item));
    }

    [Theory]
    [InlineData(ActionStage.ToDo, 1, ActionStage.Doing)]
    [InlineData(ActionStage.Waiting, 1, ActionStage.Done)]
    [InlineData(ActionStage.Done, 1, ActionStage.Done)]
    [InlineData(ActionStage.ToDo, -1, ActionStage.ToDo)]
    [InlineData(ActionStage.Done, -1, ActionStage.Waiting)]
    public void Steps_stop_at_the_ends(ActionStage from, int delta, ActionStage expected)
    {
        Assert.Equal(expected, ActionWorkflow.Step(from, delta));
    }

    [Fact]
    public void Waiting_on_counts_items_per_person_overdue_first()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1);

        var a = Item(1);
        a.Assignments.Add(new Assignment { PersonName = "Bobby", Task = "x" });
        a.Blockers.Add(new BlockingTask { Description = "y", WaitingOn = "Dina" });

        var b = Item(2);
        b.Assignments.Add(new Assignment { PersonName = "bobby ", Task = "z" });
        b.Assignments.Add(new Assignment { PersonName = "Bobby", Task = "again" }); // same item counts once

        var c = Item(3);
        c.Blockers.Add(new BlockingTask { Description = "late", WaitingOn = "Tomas", DueUtc = past });

        var done = Item(4);
        done.CompletedUtc = DateTimeOffset.UtcNow;
        done.Assignments.Add(new Assignment { PersonName = "Ignored", Task = "x" });

        var summary = ActionWorkflow.WaitingOn(new[] { a, b, c, done });

        Assert.Equal(new[] { "Tomas", "Bobby", "Dina" }, summary.Select(s => s.Person));
        Assert.True(summary[0].AnyOverdue);
        Assert.Equal(2, summary[1].Count);
    }

    [Fact]
    public void Waiting_line_names_who_it_is_on()
    {
        var item = Item(1);
        item.Blockers.Add(new BlockingTask { Description = "Shop drawings", WaitingOn = "Dina Brown" });
        item.Assignments.Add(new Assignment { PersonName = "Bobby K", Task = "Price" });
        item.Assignments.Add(new Assignment { PersonName = "Done Person", Task = "x", DoneUtc = DateTimeOffset.UtcNow });

        Assert.Equal("Blocked by Dina Brown  ·  With Bobby K", item.WaitingLine);
        Assert.True(ActionWorkflow.Involves(item, "bobby k"));
        Assert.False(ActionWorkflow.Involves(item, "Done Person"));
    }

    [Fact]
    public async Task Stage_and_completion_stay_in_step_in_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"actions-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            db.Migrate();
            var repo = new ActionItemRepository(db, new FakeClock());

            var item = await repo.UpsertAsync(Item(0, ActionStage.ToDo));

            await repo.UpdateStageAsync(item.Id, ActionStage.Done);
            var done = (await repo.GetCompletedAsync(10)).Single();
            Assert.Equal(ActionStage.Done, done.Stage);
            Assert.True(done.IsComplete);

            await repo.UpdateStageAsync(item.Id, ActionStage.Waiting);
            var reopened = (await repo.GetOpenAsync()).Single();
            Assert.Equal(ActionStage.Waiting, reopened.Stage);

            var due = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);
            await repo.UpdateDueAsync(item.Id, due);
            Assert.Equal(due, (await repo.GetOpenAsync()).Single().DueUtc);

            await repo.SetCompletedAsync(item.Id, true);
            Assert.Equal(ActionStage.Done, (await repo.GetCompletedAsync(10)).Single().Stage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }
}
