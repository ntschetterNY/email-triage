using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class CalendarMathTests
{
    // Wednesday 11 March 2026, 10:00.
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    private static CalendarEvent Event(
        string subject, DateTimeOffset start, DateTimeOffset end,
        BusyStatus busy = BusyStatus.Busy, MeetingResponse response = MeetingResponse.Accepted,
        bool allDay = false, string id = "") => new()
        {
            Ref = new MailRef(id.Length > 0 ? id : subject, "store"),
            Subject = subject,
            Start = start,
            End = end,
            Busy = busy,
            Response = response,
            IsAllDay = allDay,
            IsMeeting = true,
        };

    // ---- conflicts ---------------------------------------------------------

    [Fact]
    public void Overlapping_meetings_conflict()
    {
        var events = new[]
        {
            Event("Standup", At(11, 9), At(11, 9, 15)),
            Event("Design review", At(11, 14, 30), At(11, 15, 30)),
        };

        var clash = CalendarMath.Conflicts(events, At(11, 14), At(11, 15));

        Assert.Equal("Design review", Assert.Single(clash).Subject);
    }

    [Fact]
    public void Back_to_back_is_not_a_conflict()
    {
        var events = new[] { Event("Before", At(11, 13), At(11, 14)), Event("After", At(11, 15), At(11, 16)) };
        Assert.Empty(CalendarMath.Conflicts(events, At(11, 14), At(11, 15)));
    }

    [Fact]
    public void Free_declined_and_all_day_entries_never_conflict()
    {
        var events = new[]
        {
            Event("Lunch (free)", At(11, 14), At(11, 15), busy: BusyStatus.Free),
            Event("Declined", At(11, 14), At(11, 15), response: MeetingResponse.Declined),
            Event("Offsite", At(11, 0), At(12, 0), allDay: true),
        };

        Assert.Empty(CalendarMath.Conflicts(events, At(11, 14), At(11, 15)));
    }

    [Fact]
    public void An_invitation_does_not_clash_with_its_own_pencilled_in_entry()
    {
        var own = Event("The invite", At(11, 14), At(11, 15), busy: BusyStatus.Tentative,
                        response: MeetingResponse.NotResponded, id: "appt-1");

        Assert.Empty(CalendarMath.Conflicts(new[] { own }, At(11, 14), At(11, 15), exclude: own.Ref));
        Assert.Single(CalendarMath.Conflicts(new[] { own }, At(11, 14), At(11, 15)));
    }

    // ---- free slots --------------------------------------------------------

    private static IReadOnlyList<TimeSlot> Slots(IEnumerable<CalendarEvent> events, DateTimeOffset now, int minutes, int max = 5) =>
        CalendarMath.FreeSlots(events, now, TimeSpan.FromMinutes(minutes),
            TimeSpan.FromHours(9), TimeSpan.FromHours(17), days: 7, max: max);

    [Fact]
    public void First_free_slot_starts_at_the_next_half_hour()
    {
        var slots = Slots(Array.Empty<CalendarEvent>(), At(11, 10, 7), 30);
        Assert.Equal(At(11, 10, 30), slots[0].Start);
        Assert.Equal(At(11, 11), slots[0].End);
    }

    [Fact]
    public void Slots_fit_between_meetings_one_per_gap()
    {
        var events = new[]
        {
            Event("A", At(11, 10), At(11, 11)),
            Event("B", At(11, 11, 30), At(11, 12)),   // 30 min gap before: too short for an hour
            Event("C", At(11, 13), At(11, 17)),
        };

        var slots = Slots(events, Now, 60, max: 2);

        Assert.Equal(new TimeSlot(At(11, 12), At(11, 13)), slots[0]);
        Assert.Equal(new TimeSlot(At(12, 9), At(12, 10)), slots[1]);  // next morning
    }

    [Fact]
    public void Weekends_are_skipped()
    {
        // Friday 13 March at 16:50: nothing fits today, and the weekend is not offered.
        var slots = Slots(Array.Empty<CalendarEvent>(), At(13, 16, 50), 60, max: 1);
        Assert.Equal(At(16, 9), Assert.Single(slots).Start);
    }

    [Fact]
    public void Overlapping_meetings_do_not_open_a_false_gap()
    {
        var events = new[]
        {
            Event("Long", At(11, 10), At(11, 16)),
            Event("Inside", At(11, 11), At(11, 12)),
        };

        var slots = Slots(events, Now, 60, max: 1);
        Assert.Equal(At(11, 16), Assert.Single(slots).Start);
    }

    // ---- now and next ------------------------------------------------------

    [Fact]
    public void Current_and_next_skip_all_day_and_declined()
    {
        var events = new[]
        {
            Event("Offsite", At(11, 0), At(12, 0), allDay: true),
            Event("Standup", At(11, 9, 45), At(11, 10, 15)),
            Event("Declined", At(11, 10, 30), At(11, 11), response: MeetingResponse.Declined),
            Event("1:1", At(11, 11), At(11, 11, 30)),
        };

        Assert.Equal("Standup", Assert.Single(CalendarMath.Current(events, Now)).Subject);
        Assert.Equal("1:1", CalendarMath.Next(events, Now)?.Subject);
    }

    [Fact]
    public void Join_prefers_the_meeting_about_to_start_over_the_one_ending()
    {
        var events = new[]
        {
            Event("Ending", At(11, 9), At(11, 10, 5)),
            Event("Starting", At(11, 10, 5), At(11, 11)),
        };

        Assert.Equal("Starting", CalendarMath.Joinable(events, Now, TimeSpan.FromMinutes(10))?.Subject);
        Assert.Equal("Ending", CalendarMath.Joinable(events, Now, TimeSpan.FromMinutes(2))?.Subject);
    }

    [Theory]
    [InlineData(-5, "now")]
    [InlineData(0, "now")]
    [InlineData(0.5, "in 1 min")]
    [InlineData(25, "in 25 min")]
    [InlineData(60, "in 1 h")]
    [InlineData(65, "in 1 h 5 min")]
    public void Countdown_reads_naturally(double minutes, string expected)
    {
        Assert.Equal(expected, CalendarMath.Countdown(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Day_labels()
    {
        var today = new DateTime(2026, 3, 11);
        Assert.Equal("Today", CalendarMath.DayLabel(today, today));
        Assert.Equal("Tomorrow", CalendarMath.DayLabel(today.AddDays(1), today));
        Assert.Equal("Fri 13 Mar", CalendarMath.DayLabel(today.AddDays(2), today));
    }

    [Fact]
    public void Time_ranges()
    {
        Assert.Equal("14:00–15:30", CalendarMath.TimeRange(At(11, 14), At(11, 15, 30)));
        Assert.Equal("23:00–00:00", CalendarMath.TimeRange(At(11, 23), At(12, 0)));
        Assert.Equal("All day", CalendarMath.TimeRange(At(11, 0), At(12, 0), allDay: true));
    }
}
