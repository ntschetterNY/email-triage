using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MeetingEditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(bool meeting = true, MeetingResponse response = MeetingResponse.Organized) => new()
    {
        Ref = new MailRef("e1", "s1"),
        Subject = "Design review",
        Start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 2, 9, 0, 0))),
        End = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 2, 9, 30, 0))),
        Location = "Room 4",
        IsMeeting = meeting,
        Response = response,
    };

    private static readonly CalendarEventDetail Detail = new()
    {
        Body = "Agenda: drawings",
        Attendees = new[]
        {
            new Attendee("Me Myself", "me@x.com", false, MeetingResponse.Organized),
            new Attendee("Jane Smith", "jane@x.com", false, MeetingResponse.Accepted),
            new Attendee("Bob", "bob@y.com", true, MeetingResponse.None),
        },
    };

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("9:30", 9, 30)]
    [InlineData("09:30", 9, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("1430", 14, 30)]
    [InlineData("14.00", 14, 0)]
    [InlineData("9am", 9, 0)]
    [InlineData("2:15 pm", 14, 15)]
    [InlineData("12pm", 12, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("noon", 12, 0)]
    public void Reads_times(string text, int hour, int minute)
    {
        Assert.True(MeetingEdit.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hour, minute, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("9:75")]
    [InlineData("13pm")]
    [InlineData("soon")]
    public void Refuses_what_is_not_a_time(string text) => Assert.False(MeetingEdit.TryParseTime(text, out _));

    [Theory]
    [InlineData("Fri 2 Oct 2026", 2026, 10, 2)]
    [InlineData("2 Oct 2026", 2026, 10, 2)]
    [InlineData("2026-10-02", 2026, 10, 2)]
    [InlineData("2 Oct", 2026, 10, 2)]
    [InlineData("3 Sep", 2027, 9, 3)] // already past this year
    public void Reads_dates(string text, int year, int month, int day)
    {
        Assert.True(MeetingEdit.TryParseDate(text, Now, out var date));
        Assert.Equal(new DateTime(year, month, day), date);
    }

    [Fact]
    public void Reads_the_date_it_writes()
    {
        var shown = MeetingEdit.FormatDate(new DateTime(2026, 12, 24));
        Assert.True(MeetingEdit.TryParseDate(shown, Now, out var date));
        Assert.Equal(new DateTime(2026, 12, 24), date);
    }

    [Fact]
    public void Only_your_own_can_be_edited()
    {
        Assert.True(MeetingEdit.CanEdit(Event()));
        Assert.True(MeetingEdit.CanEdit(Event(meeting: false, response: MeetingResponse.None)));
        Assert.False(MeetingEdit.CanEdit(Event(response: MeetingResponse.Accepted)));
    }

    [Fact]
    public void The_form_leaves_the_organizer_out()
    {
        var form = MeetingEdit.FormFor(Event(), Detail);

        Assert.Equal("Design review", form.Subject);
        Assert.Equal("09:00", form.StartTime);
        Assert.Equal("09:30", form.EndTime);
        Assert.Equal("Jane Smith <jane@x.com>; ", form.Required);
        Assert.Equal("Bob <bob@y.com>; ", form.Optional);
        Assert.DoesNotContain("me@x.com", form.Required);
    }

    [Fact]
    public void An_untouched_form_rewrites_neither_notes_nor_people()
    {
        var ev = Event();
        var form = MeetingEdit.FormFor(ev, Detail);

        Assert.Null(MeetingEdit.TryBuild(form, form, ev, Now, out var change));
        Assert.NotNull(change);
        Assert.Null(change!.Body);
        Assert.Null(change.Invitees);
        Assert.Equal(ev.Start, change.Start);
        Assert.Equal(ev.End, change.End);
        Assert.True(change.Send); // a meeting's attendees hear of any change
    }

    [Fact]
    public void Adding_someone_lists_everyone()
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);
        var form = original with { Required = original.Required + "al@z.com; ", Subject = "  Design review 2 " };

        Assert.Null(MeetingEdit.TryBuild(form, original, ev, Now, out var change));
        Assert.Equal("Design review 2", change!.Subject);
        Assert.Equal(
            new[] { new Invitee("jane@x.com", false), new Invitee("al@z.com", false), new Invitee("bob@y.com", true) },
            change.Invitees);
    }

    [Fact]
    public void Moves_the_time()
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);
        var form = original with { Date = "5 Oct 2026", StartTime = "2pm", EndTime = "3:15pm" };

        Assert.Null(MeetingEdit.TryBuild(form, original, ev, Now, out var change));
        Assert.Equal(new DateTime(2026, 10, 5, 14, 0, 0), change!.Start.DateTime);
        Assert.Equal(new DateTime(2026, 10, 5, 15, 15, 0), change.End.DateTime);
    }

    [Fact]
    public void All_day_runs_midnight_to_midnight()
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);

        Assert.Null(MeetingEdit.TryBuild(original with { IsAllDay = true }, original, ev, Now, out var change));
        Assert.Equal(new DateTime(2026, 10, 2), change!.Start.DateTime);
        Assert.Equal(new DateTime(2026, 10, 3), change.End.DateTime);
    }

    [Theory]
    [InlineData("", "09:00", "09:30", "title")]
    [InlineData("x", "10:00", "09:30", "ends before")]
    [InlineData("x", "later", "09:30", "start time")]
    public void Says_what_is_wrong(string subject, string start, string end, string expected)
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);
        var form = original with { Subject = subject, StartTime = start, EndTime = end };

        var problem = MeetingEdit.TryBuild(form, original, ev, Now, out var change);
        Assert.Null(change);
        Assert.Contains(expected, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_meeting_cannot_be_left_with_nobody()
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);
        var problem = MeetingEdit.TryBuild(original with { Required = "", Optional = "" }, original, ev, Now, out _);
        Assert.Contains("cancel it in Outlook", problem);
    }

    [Fact]
    public void An_appointment_saves_quietly_until_someone_is_invited()
    {
        var ev = Event(meeting: false, response: MeetingResponse.None);
        var original = MeetingEdit.FormFor(ev, null);

        Assert.Equal("Save", MeetingEdit.SaveLabel(original, ev));
        Assert.Null(MeetingEdit.TryBuild(original with { Location = "Site" }, original, ev, Now, out var quiet));
        Assert.False(quiet!.Send);

        var invited = original with { Required = "jane@x.com" };
        Assert.Equal("Send invitation", MeetingEdit.SaveLabel(invited, ev));
        Assert.Null(MeetingEdit.TryBuild(invited, original, ev, Now, out var sent));
        Assert.True(sent!.Send);
    }

    [Fact]
    public void Changed_notes_are_written()
    {
        var ev = Event();
        var original = MeetingEdit.FormFor(ev, Detail);
        Assert.Null(MeetingEdit.TryBuild(original with { Body = "New agenda\n" }, original, ev, Now, out var change));
        Assert.Equal("New agenda", change!.Body);
        Assert.Equal("Send update", MeetingEdit.SaveLabel(original, ev));
    }
}
