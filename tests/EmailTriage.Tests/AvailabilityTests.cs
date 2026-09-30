using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class AvailabilityTests
{
    private static readonly TimeSpan Et = TimeSpan.FromHours(-4);

    // Monday 28 September 2026, 09:35.
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 35, 0, Et);

    private static readonly AvailabilityRules Rules = new()
    {
        DayStart = TimeSpan.FromHours(7),
        DayEnd = TimeSpan.FromHours(16),
        MinWindow = TimeSpan.FromMinutes(30),
        WorkingDays = 5,
        LunchStart = TimeSpan.FromHours(12),
        LunchEnd = TimeSpan.FromHours(13),
        SlotCount = 3,
    };

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, day < 15 ? 10 : 9, day, hour, minute, 0, Et);

    private static CalendarEvent Event(
        DateTimeOffset start, DateTimeOffset end, BusyStatus busy = BusyStatus.Busy,
        MeetingResponse response = MeetingResponse.Accepted, bool allDay = false) => new()
        {
            Ref = new MailRef($"{start:O}", "store"),
            Subject = "Meeting",
            Start = start,
            End = end,
            Busy = busy,
            Response = response,
            IsAllDay = allDay,
            IsMeeting = true,
        };

    private static string Spans(IEnumerable<TimeSlot> slots) =>
        string.Join(", ", slots.Select(s => $"{s.Start:HH:mm}-{s.End:HH:mm}"));

    [Fact]
    public void Starts_tomorrow_and_counts_only_working_days()
    {
        var days = Availability.FreeWindows(Array.Empty<CalendarEvent>(), Now, Rules);

        // Tue 29 Sep to Fri 2 Oct, then Mon 5 Oct: the weekend is skipped.
        Assert.Equal(new[] { 29, 30, 1, 2, 5 }, days.Select(d => d.Date.Day));
    }

    [Fact]
    public void An_empty_day_is_free_either_side_of_lunch()
    {
        var tuesday = Availability.FreeWindows(Array.Empty<CalendarEvent>(), Now, Rules)[0];

        Assert.Equal("07:00-12:00, 13:00-16:00", Spans(tuesday.Windows));
        Assert.Equal("12:00-13:00", Spans(tuesday.Lunch));
        Assert.All(tuesday.Windows, w => Assert.Equal(Et, w.Start.Offset));
    }

    [Fact]
    public void Meetings_take_exactly_their_own_time()
    {
        var events = new[]
        {
            Event(At(29, 7, 30), At(29, 8)),        // huddle
            Event(At(29, 10, 30), At(29, 13)),      // site walk, through lunch
        };

        var tuesday = Availability.FreeWindows(events, Now, Rules)[0];

        // 07:00-07:30 is just long enough; no gap is kept either side of a meeting.
        Assert.Equal("07:00-07:30, 08:00-10:30, 13:00-16:00", Spans(tuesday.Windows));
        Assert.Empty(tuesday.Lunch);
    }

    [Fact]
    public void Free_declined_and_working_elsewhere_do_not_take_time()
    {
        var events = new[]
        {
            Event(At(29, 10), At(29, 11), busy: BusyStatus.Free),
            Event(At(29, 13), At(29, 14), response: MeetingResponse.Declined),
            Event(At(29, 14), At(29, 15), busy: BusyStatus.WorkingElsewhere),
        };

        var tuesday = Availability.FreeWindows(events, Now, Rules)[0];

        Assert.Equal("07:00-12:00, 13:00-16:00", Spans(tuesday.Windows));
        Assert.Empty(tuesday.Holds);
    }

    [Fact]
    public void Tentative_time_stays_bookable_and_is_listed_as_a_hold()
    {
        var events = new[]
        {
            Event(At(29, 8), At(29, 9), busy: BusyStatus.Tentative),
            Event(At(29, 8, 30), At(29, 9, 30), response: MeetingResponse.Tentative),   // said maybe; overlaps the first
            Event(At(29, 12), At(29, 12, 30), busy: BusyStatus.Tentative),              // over lunch
            Event(At(29, 14), At(29, 15), busy: BusyStatus.Tentative),
            Event(At(29, 14, 30), At(29, 16)),                                           // a firm meeting wins over the hold
        };

        var tuesday = Availability.FreeWindows(events, Now, Rules)[0];

        Assert.Equal("07:00-12:00, 13:00-14:30", Spans(tuesday.Windows));
        Assert.Equal("12:00-13:00", Spans(tuesday.Lunch));
        Assert.Equal("08:00-09:30, 12:00-12:30, 14:00-14:30", Spans(tuesday.Holds));
    }

    [Fact]
    public void Out_of_office_all_day_takes_the_day_but_a_free_all_day_note_does_not()
    {
        var events = new[]
        {
            Event(At(30, 0), At(1, 0), busy: BusyStatus.OutOfOffice, allDay: true),
            Event(At(1, 0), At(2, 0), busy: BusyStatus.Free, allDay: true),
        };

        var days = Availability.FreeWindows(events, Now, Rules);

        Assert.True(days[1].IsAway);
        Assert.True(days[1].IsFullyBooked);
        Assert.False(days[2].IsAway);
        Assert.Equal("07:00-12:00, 13:00-16:00", Spans(days[2].Windows));
    }

    [Fact]
    public void A_short_bit_of_lunch_is_kept_only_when_it_extends_a_window()
    {
        var events = new[]
        {
            // Wed: lunch free from 12:45, running on into a free afternoon.
            Event(At(30, 7), At(30, 12, 45)),
            // Thu: lunch free 12:30-12:45 only, between two meetings.
            Event(At(1, 7), At(1, 12, 30)),
            Event(At(1, 12, 45), At(1, 16)),
        };

        var days = Availability.FreeWindows(events, Now, Rules);

        Assert.Equal("12:45-13:00", Spans(days[1].Lunch));
        Assert.Equal("13:00-16:00", Spans(days[1].Windows));
        Assert.True(days[2].IsFullyBooked);
    }

    [Fact]
    public void Without_lunch_rules_midday_is_ordinary_time()
    {
        var days = Availability.FreeWindows(Array.Empty<CalendarEvent>(), Now,
            Rules with { LunchStart = null, LunchEnd = null });

        Assert.Equal("07:00-16:00", Spans(days[0].Windows));
        Assert.Empty(days[0].Lunch);
    }

    [Fact]
    public void Describe_lists_each_day_and_the_rules_for_offering_times()
    {
        var events = new[]
        {
            Event(At(29, 7), At(29, 16)),
            Event(At(30, 7), At(30, 12, 15)),
            Event(At(30, 12, 45), At(30, 16)),
            Event(At(2, 0), At(3, 0), busy: BusyStatus.OutOfOffice, allDay: true),
        };

        var text = Availability.Describe(
            Availability.FreeWindows(events, Now, Rules), Now, Rules, "Eastern Daylight Time (UTC-04:00)");

        Assert.Contains("in Eastern Daylight Time (UTC-04:00). It is now Monday 28 September 2026, 09:35.", text);
        Assert.Contains("working hours (07:00-16:00)", text);
        Assert.Contains("back to back allowed", text);
        Assert.DoesNotContain("buffer", text);
        Assert.DoesNotContain("HOLD", text);
        Assert.Contains("through Mon 5 Oct", text);
        Assert.Contains("Tue 29 Sep: fully booked", text);
        Assert.Contains("Wed 30 Sep: only over lunch: 12:15-12:45", text);
        Assert.Contains("Thu 1 Oct: 07:00-12:00, 13:00-16:00 (lunch free too: 12:00-13:00)", text);
        Assert.Contains("Fri 2 Oct: away (out of office)", text);
        Assert.Contains("offer 3 specific times", text);
        Assert.Contains("keeps lunch (12:00-13:00) free", text);
        Assert.Contains("Never offer a time outside these windows", text);
    }

    [Fact]
    public void Describe_marks_hold_times()
    {
        var events = new[] { Event(At(29, 9), At(29, 10), busy: BusyStatus.Tentative) };

        var text = Availability.Describe(Availability.FreeWindows(events, Now, Rules), Now, Rules, "ET");

        Assert.Contains("Tue 29 Sep: 07:00-12:00, 13:00-16:00 (lunch free too: 12:00-13:00); HOLD 09:00-10:00", text);
        Assert.Contains("HOLD times are pencilled in (tentative) and can be booked over", text);
    }

    [Fact]
    public void A_working_day_to_midnight_reads_24_00()
    {
        var late = Rules with { DayEnd = TimeSpan.FromHours(24) };
        var text = Availability.Describe(Availability.FreeWindows(Array.Empty<CalendarEvent>(), Now, late), Now, late, "ET");

        Assert.Contains("working hours (07:00-24:00)", text);
    }

    [Fact]
    public void The_draft_prompt_carries_the_availability_only_when_given()
    {
        DraftContext Context(string availability) => new()
        {
            Subject = "RE: Level 3 punch list walkthrough",
            Kind = "reply to everyone on the conversation below",
            Availability = availability,
        };

        Assert.DoesNotContain("availability", AiDraftService.BuildPrompt(Context("")));
        Assert.Contains(Availability.Unreadable, AiDraftService.BuildPrompt(Context(Availability.Unreadable)));
        Assert.Contains("Free windows", AiDraftService.BuildPrompt(Context(
            Availability.Describe(Availability.FreeWindows(Array.Empty<CalendarEvent>(), Now, Rules), Now, Rules, "ET"))));
    }
}
