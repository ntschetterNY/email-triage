using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FollowUpPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ActionItem Item(long id = 1) => new()
    {
        Id = id,
        InternetMessageId = $"<{id}@test>",
        Subject = "DEP letter",
        SenderName = "Dana Reyes",
        SenderAddress = "dana@example.com",
        Stage = ActionStage.Waiting,
    };

    private static BlockingTask Blocker(int daysAgo, string who = "Dana Reyes", string what = "signed DEP letter") =>
        new() { Description = what, WaitingOn = who, CreatedUtc = Now.AddDays(-daysAgo) };

    private static Assignment Handoff(int daysAgo, DateTimeOffset? notified = null) => new()
    {
        PersonName = "Sam Ortiz",
        Task = "send the revised SOV",
        CreatedUtc = Now.AddDays(-daysAgo),
        NotifiedUtc = notified,
    };

    [Fact]
    public void FindDue_FlagsAWaitOlderThanTheThreshold()
    {
        var stale = Item(1); stale.Blockers.Add(Blocker(daysAgo: 6));
        var fresh = Item(2); fresh.Blockers.Add(Blocker(daysAgo: 2));

        var due = FollowUpPlanner.FindDue(new[] { stale, fresh }, afterDays: 5, Now);

        var only = Assert.Single(due);
        Assert.Equal(1, only.Item.Id);
        Assert.Equal("Dana Reyes", only.Who);
        Assert.Equal(6, only.DaysWaiting);
    }

    [Fact]
    public void FindDue_AFollowUpRestartsTheClock()
    {
        var item = Item();
        item.Blockers.Add(Blocker(daysAgo: 10));
        item.LastFollowUpUtc = Now.AddDays(-2); // chased two days ago

        Assert.Empty(FollowUpPlanner.FindDue(new[] { item }, afterDays: 5, Now));

        item.LastFollowUpUtc = Now.AddDays(-7); // the chase itself has gone stale
        var again = Assert.Single(FollowUpPlanner.FindDue(new[] { item }, afterDays: 5, Now));
        Assert.Equal(10, again.DaysWaiting); // total wait, not time since the nudge
    }

    [Fact]
    public void FindDue_IgnoresResolvedDoneAndCompleted()
    {
        var resolved = Item(1);
        resolved.Blockers.Add(new BlockingTask
        {
            Description = "x", CreatedUtc = Now.AddDays(-20), ResolvedUtc = Now.AddDays(-1),
        });

        var completed = Item(2);
        completed.Blockers.Add(Blocker(daysAgo: 20));
        completed.CompletedUtc = Now;

        Assert.Empty(FollowUpPlanner.FindDue(new[] { resolved, completed }, afterDays: 5, Now));
    }

    [Fact]
    public void FindDue_ZeroDaysTurnsItOff()
    {
        var item = Item();
        item.Blockers.Add(Blocker(daysAgo: 30));

        Assert.Empty(FollowUpPlanner.FindDue(new[] { item }, afterDays: 0, Now));
    }

    [Fact]
    public void FindDue_LongestWaitComesFirst()
    {
        var older = Item(1); older.Blockers.Add(Blocker(daysAgo: 12));
        var newer = Item(2); newer.Blockers.Add(Blocker(daysAgo: 7));

        var due = FollowUpPlanner.FindDue(new[] { newer, older }, afterDays: 5, Now);

        Assert.Equal(new long[] { 1, 2 }, due.Select(d => d.Item.Id));
    }

    [Fact]
    public void Describe_AHandoffClockStartsWhenTheyWereTold()
    {
        var item = Item();
        item.Assignments.Add(Handoff(daysAgo: 10, notified: Now.AddDays(-3)));

        var due = FollowUpPlanner.Describe(item, Now);

        Assert.NotNull(due);
        Assert.Equal("Sam Ortiz", due!.Who);
        Assert.Equal(3, due.DaysWaiting);
    }

    [Fact]
    public void Describe_NothingOpenIsNull()
    {
        Assert.Null(FollowUpPlanner.Describe(Item(), Now));
    }

    [Fact]
    public void BuildInstructions_SaysWhoWhatAndHowLong()
    {
        var item = Item();
        item.Blockers.Add(Blocker(daysAgo: 9));

        var brief = FollowUpPlanner.BuildInstructions(FollowUpPlanner.Describe(item, Now)!);

        Assert.Contains("Dana Reyes", brief);
        Assert.Contains("signed DEP letter", brief);
        Assert.Contains("9 days", brief);
        Assert.Contains("follow-up", brief);
    }
}

public class WritingStyleTests
{
    [Fact]
    public void ExtractOwnText_CutsAtOutlookQuoteMarkers()
    {
        var reply = "Thanks - approved.\n\nFrom: Dana Reyes <dana@example.com>\nSent: Monday\nTo: Me\n\nPlease approve.";
        Assert.Equal("Thanks - approved.", WritingStyleService.ExtractOwnText(reply));

        var divider = "Will do by Friday.\n________________________________\nFrom: someone";
        Assert.Equal("Will do by Friday.", WritingStyleService.ExtractOwnText(divider));

        var original = "See attached.\n-----Original Message-----\nold text";
        Assert.Equal("See attached.", WritingStyleService.ExtractOwnText(original));

        var gmail = "Sounds good.\nOn Mon, 3 Jun 2026 at 09:12, Dana wrote:\n> earlier";
        Assert.Equal("Sounds good.", WritingStyleService.ExtractOwnText(gmail));
    }

    [Fact]
    public void ExtractOwnText_PlainMailComesBackWhole()
    {
        var text = "Team,\n\nSchedule attached. Flag anything by Friday.\n\nThanks";
        Assert.Equal(text, WritingStyleService.ExtractOwnText(text));
    }

    [Fact]
    public void BuildPrompt_CarriesTheSamplesAndAsksForBullets()
    {
        var prompt = WritingStyleService.BuildPrompt(new[] { "Sample one text.", "Sample two text." });

        Assert.Contains("Sample one text.", prompt);
        Assert.Contains("--- Sample 2", prompt);
        Assert.Contains("style guide", prompt);
        Assert.Contains("greeting", prompt);
    }
}
