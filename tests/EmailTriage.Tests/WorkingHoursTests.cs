using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class WorkingHoursTests
{
    private static readonly WorkingHours Hours = new();

    // November 2026 is EST (UTC-5). Nov 10 2026 is a Tuesday.
    private static DateTimeOffset Et(int day, int hour, int minute = 0) =>
        new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData(7, 0)]
    [InlineData(12, 30)]
    [InlineData(17, 59)]
    public void Inside_hours_sends_now(int hour, int minute) =>
        Assert.Null(Hours.NextOpening(Et(10, hour, minute)));

    [Fact]
    public void Early_morning_waits_for_seven_the_same_day() =>
        Assert.Equal(Et(10, 7), Hours.NextOpening(Et(10, 5, 40)));

    [Fact]
    public void Six_pm_or_later_waits_for_seven_the_next_day()
    {
        Assert.Equal(Et(11, 7), Hours.NextOpening(Et(10, 18)));
        Assert.Equal(Et(11, 7), Hours.NextOpening(Et(10, 23, 15)));
    }

    [Fact]
    public void Friday_evening_and_the_weekend_wait_for_monday()
    {
        Assert.Equal(Et(16, 7), Hours.NextOpening(Et(13, 19)));  // Fri 7pm
        Assert.Equal(Et(16, 7), Hours.NextOpening(Et(14, 11)));  // Sat
        Assert.Equal(Et(16, 7), Hours.NextOpening(Et(15, 6)));   // Sun early
    }

    [Fact]
    public void Uses_eastern_time_whatever_the_callers_offset()
    {
        // 3pm Pacific is 6pm Eastern: closed.
        var pacific = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.FromHours(-8));
        Assert.Equal(Et(11, 7), Hours.NextOpening(pacific));
    }

    [Fact]
    public void Opening_after_a_clock_change_has_the_new_offset()
    {
        // Sat 7 Mar 2026 evening EST; Monday 9 Mar is EDT (UTC-4).
        var saturday = new DateTimeOffset(2026, 3, 7, 20, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 7, 0, 0, TimeSpan.FromHours(-4)), Hours.NextOpening(saturday));
    }
}
