using System.Globalization;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MeetingOptionsTests
{
    // Tuesday 3 March 2026, 14:00.
    private static readonly DateTimeOffset Tuesday = new(2026, 3, 3, 14, 0, 0, TimeSpan.FromHours(-5));

    public MeetingOptionsTests()
    {
        // Day names are read in the current culture.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public void Starts_as_a_one_off_busy_meeting_without_Teams()
    {
        Assert.Equal("no Teams · one time · shows as busy", new MeetingOptions().Describe(Tuesday));
    }

    [Fact]
    public void Teams_toggles_on_and_off()
    {
        var on = new MeetingOptions().ToggleTeams();
        Assert.True(on.Teams);
        Assert.False(on.ToggleTeams().Teams);
    }

    [Fact]
    public void All_day_shows_as_free_like_Outlook_and_back_to_busy_when_turned_off()
    {
        var allDay = new MeetingOptions().ToggleAllDay();
        Assert.True(allDay.AllDay);
        Assert.Equal(BusyStatus.Free, allDay.ShowAs);

        var timed = allDay.ToggleAllDay();
        Assert.False(timed.AllDay);
        Assert.Equal(BusyStatus.Busy, timed.ShowAs);
    }

    [Fact]
    public void All_day_keeps_a_show_as_the_user_chose()
    {
        var away = new MeetingOptions { ShowAs = BusyStatus.OutOfOffice }.ToggleAllDay();
        Assert.Equal(BusyStatus.OutOfOffice, away.ShowAs);
        Assert.Equal(BusyStatus.OutOfOffice, away.ToggleAllDay().ShowAs);
    }

    [Fact]
    public void Repeat_cycles_through_every_pattern_and_back_to_once()
    {
        var seen = new List<Repeat>();
        var o = new MeetingOptions();
        for (var i = 0; i < 6; i++)
        {
            o = o.NextRepeat();
            seen.Add(o.Repeat);
        }

        Assert.Equal(Repeat.Once, seen[^1]);
        Assert.Equal(Enum.GetValues<Repeat>().Order(), seen.Order());
    }

    [Fact]
    public void Show_as_cycles_through_every_status()
    {
        var seen = new HashSet<BusyStatus>();
        var o = new MeetingOptions();
        for (var i = 0; i < 5; i++)
        {
            o = o.NextShowAs();
            seen.Add(o.ShowAs);
        }

        Assert.Equal(Enum.GetValues<BusyStatus>().ToHashSet(), seen);
        Assert.Equal(BusyStatus.Busy, o.ShowAs);
    }

    [Theory]
    [InlineData(Repeat.Daily, "every day")]
    [InlineData(Repeat.Weekdays, "every weekday")]
    [InlineData(Repeat.Weekly, "every Tuesday")]
    [InlineData(Repeat.Fortnightly, "every other Tuesday")]
    [InlineData(Repeat.Monthly, "monthly on the 3rd")]
    public void Repeat_is_described_from_the_first_day(Repeat repeat, string expected)
    {
        Assert.Equal(expected, MeetingOptions.DescribeRepeat(repeat, Tuesday));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(31, "31st")]
    public void Monthly_uses_English_ordinals(int day, string expected)
    {
        var start = new DateTimeOffset(2026, 1, day, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal($"monthly on the {expected}", MeetingOptions.DescribeRepeat(Repeat.Monthly, start));
    }

    [Fact]
    public void Describes_everything_that_is_on()
    {
        var o = new MeetingOptions { Teams = true, Repeat = Repeat.Weekly }.ToggleAllDay();
        Assert.Equal("Teams meeting · all day · every Tuesday · shows as free", o.Describe(Tuesday));
    }

    [Fact]
    public void Whole_days_run_midnight_to_midnight_in_the_start_offset()
    {
        var slot = MeetingOptions.WholeDays(Tuesday);

        Assert.Equal(new DateTimeOffset(2026, 3, 3, 0, 0, 0, Tuesday.Offset), slot.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, Tuesday.Offset), slot.End);
        Assert.Equal(TimeSpan.FromDays(3), MeetingOptions.WholeDays(Tuesday, 3).Duration);
    }
}
