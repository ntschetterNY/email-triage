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
}

/// <summary>A stretch of time, such as a free slot.</summary>
public readonly record struct TimeSlot(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}
