namespace EmailTriage.Core.Models;

/// <summary>How the time shows on your calendar. Values match Outlook's OlBusyStatus.</summary>
public enum BusyStatus
{
    Free = 0,
    Tentative = 1,
    Busy = 2,
    OutOfOffice = 3,
    WorkingElsewhere = 4,
}

/// <summary>Your answer to a meeting. Values match Outlook's OlResponseStatus.</summary>
public enum MeetingResponse
{
    None = 0,
    Organized = 1,
    Tentative = 2,
    Accepted = 3,
    Declined = 4,
    NotResponded = 5,
}

/// <summary>What you can say back to an invitation.</summary>
public enum InviteResponse { Accept, Tentative, Decline }

/// <summary>
/// One appointment or meeting on your calendar. Recurring meetings appear once
/// per occurrence; every occurrence shares the series' <see cref="Ref"/>, and
/// <see cref="Start"/> tells them apart.
/// </summary>
public sealed record CalendarEvent
{
    public required MailRef Ref { get; init; }
    public required string Subject { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string Location { get; init; } = "";
    public string Organizer { get; init; } = "";
    public bool IsAllDay { get; init; }
    public BusyStatus Busy { get; init; } = BusyStatus.Busy;
    public MeetingResponse Response { get; init; }
    public bool IsRecurring { get; init; }

    /// <summary>Has attendees, as opposed to an appointment only you are in.</summary>
    public bool IsMeeting { get; init; }

    /// <summary>Tells occurrences of one series apart.</summary>
    public string Key => $"{Ref.EntryId}@{Start.UtcTicks}";

    public bool IsDeclined => Response == MeetingResponse.Declined;

    public bool IsOrganizer => Response == MeetingResponse.Organized;

    /// <summary>An invitation you have not answered yet.</summary>
    public bool NeedsResponse => IsMeeting && Response == MeetingResponse.NotResponded;

    /// <summary>Someone else's meeting you can still accept or decline.</summary>
    public bool CanRespond => IsMeeting && !IsOrganizer;

    /// <summary>Takes up the time: not all day, not shown as free, not declined.</summary>
    public bool BlocksTime => !IsAllDay && Busy != BusyStatus.Free && !IsDeclined;

    /// <summary>
    /// Pencilled in: shown as tentative, or answered "maybe". A hold still
    /// shows on the calendar, but its time can be booked over.
    /// </summary>
    public bool IsHold => Busy == BusyStatus.Tentative || Response == MeetingResponse.Tentative;

    /// <summary>Takes up the time firmly: it blocks the time and is not a hold.</summary>
    public bool IsFirm => BlocksTime && !IsHold;

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && End > start;
}

/// <summary>The parts of an event that are only read when it is opened.</summary>
public sealed record CalendarEventDetail
{
    public string Body { get; init; } = "";
    public IReadOnlyList<Attendee> Attendees { get; init; } = Array.Empty<Attendee>();

    /// <summary>A Teams, Zoom, Meet or Webex link found in the location or body.</summary>
    public string? JoinUrl { get; init; }
}

public readonly record struct Attendee(string Name, string Address, bool IsOptional, MeetingResponse Response)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

/// <summary>
/// A meeting message in the Inbox (request, cancellation or response) with
/// the details of the meeting it is about.
/// </summary>
public sealed record MeetingInvite
{
    public required MailRef Message { get; init; }
    public required MailKind Kind { get; init; }
    public required string Subject { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string Location { get; init; } = "";
    public string Organizer { get; init; } = "";
    public bool IsAllDay { get; init; }
    public bool IsRecurring { get; init; }

    /// <summary>Your current answer, read from the meeting on your calendar.</summary>
    public MeetingResponse Response { get; init; }

    /// <summary>
    /// The meeting's own calendar entry. Outlook pencils a request in as soon
    /// as it arrives, so this is excluded when looking for clashes.
    /// </summary>
    public MailRef? Appointment { get; init; }

    public string? JoinUrl { get; init; }
}

/// <summary>A new calendar entry: a block of time for yourself, or a meeting to send.</summary>
public sealed record NewCalendarEvent
{
    public required string Subject { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string Body { get; init; } = "";

    /// <summary>People to invite. Empty makes it a plain appointment.</summary>
    public IReadOnlyList<string> Attendees { get; init; } = Array.Empty<string>();

    /// <summary>Mail to attach, so it is one click away from the calendar entry.</summary>
    public MailRef? AttachMail { get; init; }

    public int ReminderMinutes { get; init; } = 5;

    /// <summary>Whole days: <see cref="Start"/> and <see cref="End"/> are midnights.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>How the time shows on your calendar, and to anyone checking your availability.</summary>
    public BusyStatus ShowAs { get; init; } = BusyStatus.Busy;

    public Repeat Repeat { get; init; } = Repeat.Once;

    /// <summary>Have Outlook's Teams button add a Teams meeting before it is sent.</summary>
    public bool AddTeams { get; init; }
}

/// <summary>How often a new event happens. The pattern follows its first day.</summary>
public enum Repeat { Once, Daily, Weekdays, Weekly, Fortnightly, Monthly }

/// <summary>What became of the Teams meeting asked for when an invitation was opened in Outlook.</summary>
public enum TeamsOutcome
{
    /// <summary>Not asked for.</summary>
    None,

    /// <summary>The Teams button was pressed; Outlook fills in the join details.</summary>
    Added,

    /// <summary>No Teams button was found to press, so it has to be added by hand.</summary>
    NotFound,

    /// <summary>
    /// The Teams button was pressed but no link turned up in time, so the
    /// meeting was left open in Outlook, unsent, to check and send by hand.
    /// </summary>
    NoLinkYet,
}

/// <summary>A stretch of time, such as a free slot.</summary>
public readonly record struct TimeSlot(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Someone on a meeting being edited: an address, or a name for Outlook to look up.</summary>
public readonly record struct Invitee(string Who, bool IsOptional);

/// <summary>
/// An edit to an event already on the calendar. Every field holds the new
/// value; <see cref="Body"/> and <see cref="Invitees"/> are null when left as
/// they were, so they are not rewritten.
/// </summary>
public sealed record CalendarEventChange
{
    public required string Subject { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public bool IsAllDay { get; init; }
    public string Location { get; init; } = "";
    public BusyStatus ShowAs { get; init; } = BusyStatus.Busy;

    /// <summary>
    /// The new text, or null when unchanged. Outlook only takes plain text
    /// here, which flattens the formatting - a Teams join block included -
    /// so an untouched body is never written back.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>Everyone invited after the edit, the organizer aside; null when unchanged.</summary>
    public IReadOnlyList<Invitee>? Invitees { get; init; }

    /// <summary>
    /// Goes out to the attendees as a meeting update (or, for an appointment
    /// gaining people, as an invitation) rather than being saved quietly.
    /// </summary>
    public bool Send { get; init; }

    /// <summary>Have Outlook's Teams button add a Teams meeting before it is saved or sent.</summary>
    public bool AddTeams { get; init; }
}
