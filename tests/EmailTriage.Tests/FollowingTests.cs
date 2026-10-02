using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class FollowingTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 11, 14, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(BusyStatus busy, MeetingResponse response, bool meeting = true) => new()
    {
        Ref = new MailRef("id", "store"),
        Subject = "Design review",
        Start = Start,
        End = Start.AddHours(1),
        Busy = busy,
        Response = response,
        IsMeeting = meeting,
    };

    [Fact]
    public void Follow_sends_your_note_or_a_standard_one()
    {
        Assert.Equal(Following.DefaultNote, Following.Note(""));
        Assert.Equal(Following.DefaultNote, Following.Note("   "));
        Assert.Equal("Send me the slides", Following.Note("  Send me the slides "));
    }

    [Fact]
    public void A_maybe_shown_as_free_is_followed()
    {
        var followed = Event(BusyStatus.Free, MeetingResponse.Tentative);

        Assert.True(followed.IsFollowing);
        Assert.False(followed.IsHold);
        Assert.False(followed.BlocksTime);
        Assert.Empty(CalendarMath.Conflicts(new[] { followed }, Start, Start.AddHours(1)));
    }

    [Fact]
    public void A_plain_maybe_is_still_a_hold_not_followed()
    {
        var maybe = Event(BusyStatus.Tentative, MeetingResponse.Tentative);

        Assert.False(maybe.IsFollowing);
        Assert.True(maybe.IsHold);
    }

    [Theory]
    [InlineData(BusyStatus.Free, MeetingResponse.Accepted)]
    [InlineData(BusyStatus.Busy, MeetingResponse.Tentative)]
    [InlineData(BusyStatus.Free, MeetingResponse.Organized)]
    public void Only_a_maybe_shown_as_free_counts(BusyStatus busy, MeetingResponse response) =>
        Assert.False(Event(busy, response).IsFollowing);

    [Fact]
    public void An_invite_reads_back_as_followed_from_its_calendar_entry()
    {
        var invite = new MeetingInvite
        {
            Message = new MailRef("m", "store"),
            Kind = MailKind.MeetingRequest,
            Subject = "Design review",
            Start = Start,
            End = Start.AddHours(1),
            Response = MeetingResponse.Tentative,
            Busy = BusyStatus.Free,
        };

        Assert.True(invite.IsFollowing);
        Assert.False((invite with { Busy = BusyStatus.Tentative }).IsFollowing);
    }
}
