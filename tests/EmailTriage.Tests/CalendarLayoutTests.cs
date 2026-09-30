using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class CalendarLayoutTests
{
    // Wednesday 30 September 2026.
    private static readonly DateTime Wednesday = new(2026, 9, 30);

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-4));

    private static CalendarEvent Event(string subject, DateTimeOffset start, DateTimeOffset end, bool allDay = false) => new()
    {
        Ref = new MailRef(subject, "store"),
        Subject = subject,
        Start = start,
        End = end,
        IsAllDay = allDay,
    };

    // ---- ranges ------------------------------------------------------------

    [Fact]
    public void Each_view_covers_the_days_around_its_anchor()
    {
        Assert.Equal((Wednesday, 1), CalendarLayout.Range(CalendarView.Day, Wednesday));
        Assert.Equal((new DateTime(2026, 9, 28), 5), CalendarLayout.Range(CalendarView.WorkWeek, Wednesday));
        Assert.Equal((new DateTime(2026, 9, 28), 7), CalendarLayout.Range(CalendarView.Week, Wednesday));

        // September 2026 starts on a Tuesday: six whole weeks from Monday 31 August.
        Assert.Equal((new DateTime(2026, 8, 31), 42), CalendarLayout.Range(CalendarView.Month, Wednesday));
    }

    [Fact]
    public void A_week_starts_on_Monday_even_from_a_Sunday()
    {
        var sunday = new DateTime(2026, 10, 4);
        Assert.Equal(new DateTime(2026, 9, 28), CalendarLayout.Range(CalendarView.Week, sunday).First);
        Assert.Equal(new DateTime(2026, 9, 28), CalendarLayout.Monday(new DateTime(2026, 9, 28)));
    }

    [Fact]
    public void The_work_week_seen_from_the_weekend_is_the_one_coming_up()
    {
        var saturday = new DateTime(2026, 10, 3);
        Assert.Equal((new DateTime(2026, 10, 5), 5), CalendarLayout.Range(CalendarView.WorkWeek, saturday));
        Assert.Equal((new DateTime(2026, 9, 28), 7), CalendarLayout.Range(CalendarView.Week, saturday));
    }

    [Fact]
    public void Stepping_moves_by_the_view_s_own_unit()
    {
        Assert.Equal(new DateTime(2026, 10, 1), CalendarLayout.Step(CalendarView.Day, Wednesday, 1));
        Assert.Equal(new DateTime(2026, 9, 23), CalendarLayout.Step(CalendarView.WorkWeek, Wednesday, -1));
        Assert.Equal(new DateTime(2026, 10, 7), CalendarLayout.Step(CalendarView.Week, Wednesday, 1));
        Assert.Equal(new DateTime(2026, 10, 30), CalendarLayout.Step(CalendarView.Month, Wednesday, 1));
    }

    [Fact]
    public void Titles_name_the_span_shown()
    {
        Assert.Equal("Wednesday 30 September 2026", CalendarLayout.Title(CalendarView.Day, Wednesday));
        Assert.Equal("28 Sep – 2 Oct 2026", CalendarLayout.Title(CalendarView.WorkWeek, Wednesday));
        Assert.Equal("5–11 Oct 2026", CalendarLayout.Title(CalendarView.Week, new DateTime(2026, 10, 7)));
        Assert.Equal("28 Dec 2026 – 1 Jan 2027", CalendarLayout.Title(CalendarView.WorkWeek, new DateTime(2026, 12, 30)));
        Assert.Equal("September 2026", CalendarLayout.Title(CalendarView.Month, Wednesday));
    }

    // ---- lanes -------------------------------------------------------------

    [Fact]
    public void A_meeting_on_its_own_takes_the_whole_column()
    {
        var placed = CalendarLayout.Lanes(new[] { Event("OAC", At(9, 30, 10), At(9, 30, 11, 30)) }, Wednesday);

        var only = Assert.Single(placed);
        Assert.Equal((0, 1), (only.Lane, only.Lanes));
        Assert.Equal(TimeSpan.FromHours(10), only.Start);
        Assert.Equal(TimeSpan.FromHours(11.5), only.End);
    }

    [Fact]
    public void Overlapping_meetings_sit_side_by_side_and_back_to_back_ones_do_not()
    {
        var events = new[]
        {
            Event("Walk", At(9, 30, 10, 30), At(9, 30, 13)),
            Event("OAC", At(9, 30, 10), At(9, 30, 11, 30)),
            Event("Call", At(9, 30, 11, 30), At(9, 30, 12)),   // fits under OAC, beside Walk
            Event("Huddle", At(9, 30, 14), At(9, 30, 14, 30)), // a cluster of its own
        };

        var placed = CalendarLayout.Lanes(events, Wednesday).ToDictionary(p => p.Event.Subject);

        Assert.Equal((0, 2), (placed["OAC"].Lane, placed["OAC"].Lanes));
        Assert.Equal((1, 2), (placed["Walk"].Lane, placed["Walk"].Lanes));
        Assert.Equal((0, 2), (placed["Call"].Lane, placed["Call"].Lanes));
        Assert.Equal((0, 1), (placed["Huddle"].Lane, placed["Huddle"].Lanes));
    }

    [Fact]
    public void A_short_meeting_drawn_taller_than_it_lasts_does_not_share_a_lane_with_the_next()
    {
        var events = new[]
        {
            Event("Quick check", At(9, 30, 9), At(9, 30, 9, 15)),
            Event("Next", At(9, 30, 9, 15), At(9, 30, 10)),
        };

        Assert.All(CalendarLayout.Lanes(events, Wednesday), p => Assert.Equal(1, p.Lanes));

        var drawn = CalendarLayout.Lanes(events, Wednesday, minLength: TimeSpan.FromMinutes(25)).ToDictionary(p => p.Event.Subject);
        Assert.Equal((0, 2), (drawn["Quick check"].Lane, drawn["Quick check"].Lanes));
        Assert.Equal((1, 2), (drawn["Next"].Lane, drawn["Next"].Lanes));
        Assert.Equal(TimeSpan.FromHours(9.25), drawn["Quick check"].End); // its real end is kept
    }

    [Fact]
    public void A_meeting_across_midnight_is_clipped_to_each_day()
    {
        var late = Event("Pour", At(9, 30, 22), At(10, 1, 2));

        var first = Assert.Single(CalendarLayout.Lanes(new[] { late }, Wednesday));
        Assert.Equal(TimeSpan.FromHours(22), first.Start);
        Assert.Equal(TimeSpan.FromHours(24), first.End);

        var second = Assert.Single(CalendarLayout.Lanes(new[] { late }, Wednesday.AddDays(1)));
        Assert.Equal(TimeSpan.Zero, second.Start);
        Assert.Equal(TimeSpan.FromHours(2), second.End);
    }

    [Fact]
    public void All_day_events_stay_out_of_the_grid_and_end_the_day_before_their_end_date()
    {
        var pour = Event("Pour: L3 slab", At(9, 30, 0), At(10, 1, 0), allDay: true);

        Assert.Empty(CalendarLayout.Lanes(new[] { pour }, Wednesday));
        Assert.Single(CalendarLayout.AllDayOn(new[] { pour }, Wednesday));
        Assert.Empty(CalendarLayout.AllDayOn(new[] { pour }, Wednesday.AddDays(1)));
    }

    [Fact]
    public void A_month_cell_lists_all_day_events_first_then_by_time()
    {
        var events = new[]
        {
            Event("Afternoon", At(9, 30, 14), At(9, 30, 15)),
            Event("Morning", At(9, 30, 8), At(9, 30, 9)),
            Event("Pour", At(9, 30, 0), At(10, 1, 0), allDay: true),
            Event("Tomorrow", At(10, 1, 8), At(10, 1, 9)),
        };

        Assert.Equal(new[] { "Pour", "Morning", "Afternoon" },
                     CalendarLayout.OnDay(events, Wednesday).Select(e => e.Subject));
    }
}
