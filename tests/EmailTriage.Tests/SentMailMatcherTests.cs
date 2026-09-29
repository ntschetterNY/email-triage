using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class SentMailMatcherTests
{
    private static readonly DateTimeOffset Sent = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static MailSummary Mail(string id, string subject, string to, DateTimeOffset when) => new()
    {
        Ref = new MailRef($"entry-{id}", "store"),
        InternetMessageId = $"<{id}@corp.com>",
        Subject = subject,
        SenderName = "Me",
        SenderAddress = "me@corp.com",
        ReceivedUtc = when,
        IsUnread = false,
        HasAttachments = false,
        DisplayTo = to,
        IsSent = true,
    };

    [Fact]
    public void Placeholders_AreRecognised()
    {
        var id = SentMailMatcher.NewPlaceholder();
        Assert.True(SentMailMatcher.IsPlaceholder(id));
        Assert.NotEqual(id, SentMailMatcher.NewPlaceholder());
        Assert.False(SentMailMatcher.IsPlaceholder("<abc@corp.com>"));
    }

    [Fact]
    public void Find_PicksTheCopySentToThemAfterTheSendTime()
    {
        var sent = new[]
        {
            Mail("old", "Revised SOV", "Sam Ortiz", Sent.AddDays(-2)),       // an earlier send, same subject
            Mail("other", "Revised SOV", "Dana Reyes", Sent.AddSeconds(5)),  // same subject, someone else
            Mail("hit", "Revised SOV", "Sam Ortiz; Dana Reyes", Sent.AddSeconds(20)),
            Mail("later", "Revised SOV", "Sam Ortiz", Sent.AddMinutes(30)),
        };

        Assert.Equal("<hit@corp.com>", SentMailMatcher.Find(sent, " revised sov ", "Sam Ortiz", "sam@corp.com", Sent)?.InternetMessageId);
    }

    [Fact]
    public void Find_MatchesOnAddressWhenTheLineHasNoName()
    {
        var sent = new[] { Mail("hit", "Revised SOV", "sam@corp.com", Sent.AddSeconds(3)) };
        Assert.NotNull(SentMailMatcher.Find(sent, "Revised SOV", "Sam Ortiz", "sam@corp.com", Sent));
    }

    [Fact]
    public void Find_AllowsForALittleClockDrift()
    {
        var sent = new[] { Mail("hit", "Revised SOV", "Sam Ortiz", Sent.AddSeconds(-40)) };
        Assert.NotNull(SentMailMatcher.Find(sent, "Revised SOV", "Sam Ortiz", "", Sent));
    }

    [Fact]
    public void Find_ReturnsNullUntilTheCopyArrives()
    {
        var sent = new[] { Mail("old", "Revised SOV", "Sam Ortiz", Sent.AddHours(-1)) };
        Assert.Null(SentMailMatcher.Find(sent, "Revised SOV", "Sam Ortiz", "", Sent));
    }
}
