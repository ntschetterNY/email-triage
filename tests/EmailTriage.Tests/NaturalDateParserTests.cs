using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class NaturalDateParserTests
{
    // A Wednesday at 10:00, so "today at 9am" is unambiguously in the past.
    private static readonly DateTimeOffset Now =
        new(2026, 3, 11, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("30m", 0, 30)]
    [InlineData("2h", 2, 0)]
    [InlineData("90 minutes", 1, 30)]
    public void Parses_relative_offsets(string input, int hours, int minutes)
    {
        Assert.True(NaturalDateParser.TryParse(input, Now, out var result));
        Assert.Equal(Now.AddHours(hours).AddMinutes(minutes), result);
    }

    [Fact]
    public void Parses_day_offsets()
    {
        Assert.True(NaturalDateParser.TryParse("3d", Now, out var result));
        Assert.Equal(Now.AddDays(3), result);
    }

    [Fact]
    public void Tomorrow_defaults_to_the_configured_morning()
    {
        Assert.True(NaturalDateParser.TryParse("tomorrow", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 8, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Tomorrow_accepts_an_explicit_time()
    {
        Assert.True(NaturalDateParser.TryParse("tomorrow 9am", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Tonight_uses_the_evening_not_the_morning()
    {
        Assert.True(NaturalDateParser.TryParse("tonight", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Bare_time_that_has_passed_rolls_to_tomorrow()
    {
        // 09:00 is already behind us at 10:00.
        Assert.True(NaturalDateParser.TryParse("9am", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Bare_time_still_ahead_stays_today()
    {
        Assert.True(NaturalDateParser.TryParse("6pm", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 18, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Parses_24_hour_times()
    {
        Assert.True(NaturalDateParser.TryParse("tomorrow 14:30", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 14, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Weekday_resolves_to_the_next_such_day()
    {
        // From Wednesday, "fri" is two days out.
        Assert.True(NaturalDateParser.TryParse("fri", Now, out var result));
        Assert.Equal(DayOfWeek.Friday, result.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 3, 13, 8, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Same_weekday_resolves_to_next_week_not_today()
    {
        Assert.True(NaturalDateParser.TryParse("wed", Now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Iso_date_is_not_mistaken_for_a_time()
    {
        // The trailing "14" must not be read as 14:00 on an unrelated day.
        Assert.True(NaturalDateParser.TryParse("2026-10-14", Now, out var result));
        Assert.Equal(2026, result.Year);
        Assert.Equal(10, result.Month);
        Assert.Equal(14, result.Day);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("25pm")]
    public void Rejects_what_it_does_not_understand(string input)
        => Assert.False(NaturalDateParser.TryParse(input, Now, out _));

    [Fact]
    public void Never_returns_a_time_in_the_past()
    {
        foreach (var input in new[] { "tomorrow", "fri", "9am", "3d", "next week" })
        {
            Assert.True(NaturalDateParser.TryParse(input, Now, out var result), input);
            Assert.True(result > Now, $"{input} resolved to the past: {result}");
        }
    }
}
