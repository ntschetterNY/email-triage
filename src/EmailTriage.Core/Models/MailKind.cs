namespace EmailTriage.Core.Models;

/// <summary>What an Inbox item is: ordinary mail, or one of Outlook's meeting messages.</summary>
public enum MailKind
{
    Mail,
    MeetingRequest,
    MeetingCancellation,
    MeetingAccepted,
    MeetingTentative,
    MeetingDeclined,
}

public static class MailKinds
{
    /// <summary>
    /// The kind for an Outlook message class, or null for items the triage
    /// list does not show (reports, tasks, forwarded-meeting notifications).
    /// Classes carry suffixes such as "IPM.Note.SMIME", so prefixes are compared.
    /// </summary>
    public static MailKind? FromMessageClass(string messageClass)
    {
        static bool Is(string value, string prefix) =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        if (Is(messageClass, "IPM.Note")) return MailKind.Mail;
        if (Is(messageClass, "IPM.Schedule.Meeting.Request")) return MailKind.MeetingRequest;
        if (Is(messageClass, "IPM.Schedule.Meeting.Canceled")) return MailKind.MeetingCancellation;
        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Pos")) return MailKind.MeetingAccepted;
        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Tent")) return MailKind.MeetingTentative;
        if (Is(messageClass, "IPM.Schedule.Meeting.Resp.Neg")) return MailKind.MeetingDeclined;
        return null;
    }

    public static bool IsMeeting(this MailKind kind) => kind != MailKind.Mail;

    /// <summary>Requests and cancellations ask something of you; responses are only news.</summary>
    public static bool NeedsAnswer(this MailKind kind) =>
        kind is MailKind.MeetingRequest or MailKind.MeetingCancellation;
}
