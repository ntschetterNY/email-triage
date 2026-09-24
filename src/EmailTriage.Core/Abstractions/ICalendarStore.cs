using EmailTriage.Core.Models;

namespace EmailTriage.Core.Abstractions;

/// <summary>
/// The calendar of the default Outlook profile. Implemented by the same store
/// as <see cref="IMailStore"/>, so it shares its single COM thread.
/// </summary>
public interface ICalendarStore
{
    /// <summary>Everything overlapping [from, to), recurring occurrences expanded, by start time.</summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Body, attendees and join link: the parts too slow to read for a whole list.</summary>
    Task<CalendarEventDetail> GetEventDetailAsync(CalendarEvent ev, CancellationToken ct = default);

    /// <summary>The meeting a request, cancellation or response is about; null for ordinary mail.</summary>
    Task<MeetingInvite?> GetInviteAsync(MailRef mail, CancellationToken ct = default);

    /// <summary>
    /// Answers an invitation - given either the request in the Inbox or the
    /// meeting on the calendar - and sends the answer, with the note if there
    /// is one, to the organizer. A recurring meeting is answered for the series.
    /// </summary>
    Task RespondAsync(MailRef item, InviteResponse response, string note, CancellationToken ct = default);

    /// <summary>Takes a cancelled meeting off the calendar, given its cancellation notice.</summary>
    Task RemoveCancelledAsync(MailRef cancellation, CancellationToken ct = default);

    /// <summary>Saves an appointment straight to the calendar and returns it, for undo.</summary>
    Task<MailRef> CreateEventAsync(NewCalendarEvent spec, CancellationToken ct = default);

    /// <summary>
    /// Opens a meeting invitation in Outlook for the user to check and send.
    /// Never sent from here: an invitation goes to other people.
    /// </summary>
    Task ShowNewMeetingAsync(NewCalendarEvent spec, CancellationToken ct = default);

    Task DeleteEventAsync(MailRef ev, CancellationToken ct = default);

    /// <summary>Opens the event in Outlook - the single occurrence, for a recurring meeting.</summary>
    Task ShowEventAsync(CalendarEvent ev, CancellationToken ct = default);
}
