using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MeetingLinksTests
{
    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=%7b%22Tid%22%7d")]
    [InlineData("https://teams.microsoft.com/meet/286012961380218?p=dCUuvYNuRCwXdXTFnO")]
    [InlineData("https://acme.zoom.us/j/1234567890?pwd=abc")]
    [InlineData("https://meet.google.com/abc-defg-hij")]
    [InlineData("https://acme.webex.com/meet/jdoe")]
    public void Finds_known_meeting_links(string url)
    {
        var body = $"Join the meeting:\r\n<{url}>\r\nMeeting ID: 123";
        Assert.Equal(url, MeetingLinks.Find(body));
    }

    [Fact]
    public void Skips_ordinary_links_before_the_join_link()
    {
        var body = "Agenda: https://sharepoint.example.com/doc.docx\n"
                 + "Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_x/0 .";
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/19%3ameeting_x/0", MeetingLinks.Find(body));
    }

    [Fact]
    public void The_short_join_link_wins_over_the_system_reference_below_it()
    {
        // How a current Teams invitation body reads.
        var body = "Join: https://teams.microsoft.com/meet/286012961380218?p=dCUuvYNuRCwXdXTFnO\n"
                 + "System reference <https://teams.microsoft.com/l/meetup-join/19%3ameeting_x/0>";
        Assert.Equal("https://teams.microsoft.com/meet/286012961380218?p=dCUuvYNuRCwXdXTFnO", MeetingLinks.Find(body));
    }

    [Fact]
    public void Unwraps_safe_links()
    {
        var real = "https://acme.zoom.us/j/555?pwd=x";
        var wrapped = "https://nam02.safelinks.protection.outlook.com/?url="
                    + Uri.EscapeDataString(real) + "&data=05%7C01&reserved=0";

        Assert.Equal(real, MeetingLinks.Find("Click " + wrapped));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Conference room 4B")]
    [InlineData("http://acme.zoom.us/j/123")]           // not https
    [InlineData("https://teams.microsoft.com/_#/calendar")] // Teams, but not a meeting
    [InlineData("https://zoom.us.evil.example/j/123")]
    public void Nothing_to_join(string? text)
    {
        Assert.Null(MeetingLinks.Find(text));
    }
}

public class MailKindTests
{
    [Theory]
    [InlineData("IPM.Note", MailKind.Mail)]
    [InlineData("IPM.Note.SMIME.MultipartSigned", MailKind.Mail)]
    [InlineData("IPM.Schedule.Meeting.Request", MailKind.MeetingRequest)]
    [InlineData("IPM.Schedule.Meeting.Canceled", MailKind.MeetingCancellation)]
    [InlineData("IPM.Schedule.Meeting.Resp.Pos", MailKind.MeetingAccepted)]
    [InlineData("IPM.Schedule.Meeting.Resp.Tent", MailKind.MeetingTentative)]
    [InlineData("IPM.Schedule.Meeting.Resp.Neg", MailKind.MeetingDeclined)]
    public void Maps_message_classes(string messageClass, MailKind expected)
    {
        Assert.Equal(expected, MailKinds.FromMessageClass(messageClass));
    }

    [Theory]
    [InlineData("REPORT.IPM.Note.NDR")]
    [InlineData("IPM.Task")]
    [InlineData("IPM.Schedule.Meeting.Notification.Forward")]
    [InlineData("")]
    public void Leaves_out_what_the_list_does_not_show(string messageClass)
    {
        Assert.Null(MailKinds.FromMessageClass(messageClass));
    }
}
