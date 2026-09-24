using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class EventTimeParserTests
{
    // A Wednesday at 10:00.
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_start_alone_leaves_the_length_open()
    {
        Assert.True(EventTimeParser.TryParse("tomorrow 2pm", Now, out var start, out var duration));
        Assert.Equal(At(12, 14), start);
        Assert.Null(duration);
    }

    [Theory]
    [InlineData("tomorrow 2pm 1h", 60)]
    [InlineData("tomorrow 2pm for 45m", 45)]
    [InlineData("tomorrow 2pm 1h30", 90)]
    [InlineData("tomorrow 2pm 1h 30m", 90)]
    [InlineData("tomorrow 2pm 1.5 hours", 90)]
    [InlineData("tomorrow 2pm 90 min", 90)]
    public void A_start_and_a_length(string input, int minutes)
    {
        Assert.True(EventTimeParser.TryParse(input, Now, out var start, out var duration));
        Assert.Equal(At(12, 14), start);
        Assert.Equal(TimeSpan.FromMinutes(minutes), duration);
    }

    [Theory]
    [InlineData("1h", 60)]
    [InlineData("30m", 30)]
    [InlineData("for 2 hours", 120)]
    public void A_length_alone_leaves_the_start_open(string input, int minutes)
    {
        // Unlike the snooze box, "1h" here is how long, not "an hour from now".
        Assert.True(EventTimeParser.TryParse(input, Now, out var start, out var duration));
        Assert.Null(start);
        Assert.Equal(TimeSpan.FromMinutes(minutes), duration);
    }

    [Theory]
    [InlineData("tomorrow 2-3pm", 14, 0, 15, 0)]
    [InlineData("tomorrow 14:00-15:30", 14, 0, 15, 30)]
    [InlineData("tomorrow 10 to 11:30am", 10, 0, 11, 30)]
    [InlineData("tomorrow 11-1pm", 11, 0, 13, 0)]
    [InlineData("tomorrow 9am-5pm", 9, 0, 17, 0)]
    public void A_range_gives_both_ends(string input, int h1, int m1, int h2, int m2)
    {
        Assert.True(EventTimeParser.TryParse(input, Now, out var start, out var duration));
        Assert.Equal(At(12, h1, m1), start);
        Assert.Equal(At(12, h2, m2) - At(12, h1, m1), duration);
    }

    [Fact]
    public void A_bare_range_is_today_when_still_ahead()
    {
        Assert.True(EventTimeParser.TryParse("3-4pm", Now, out var start, out var duration));
        Assert.Equal(At(11, 15), start);
        Assert.Equal(TimeSpan.FromHours(1), duration);
    }

    [Fact]
    public void A_backwards_range_is_rejected()
    {
        Assert.False(EventTimeParser.TryParse("tomorrow 15:00-14:00", Now, out _, out _));
    }

    [Fact]
    public void At_is_ignored()
    {
        Assert.True(EventTimeParser.TryParse("fri at 10am", Now, out var start, out _));
        Assert.Equal(At(13, 10), start);
    }

    [Fact]
    public void An_iso_date_is_a_date_not_a_range()
    {
        Assert.True(EventTimeParser.TryParse("2026-10-14", Now, out var start, out var duration));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), start);
        Assert.Null(duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("whenever")]
    [InlineData("tomorrow 2pm 3d")]
    public void Nonsense_is_rejected(string input)
    {
        Assert.False(EventTimeParser.TryParse(input, Now, out var start, out var duration));
        Assert.Null(start);
        Assert.Null(duration);
    }
}
