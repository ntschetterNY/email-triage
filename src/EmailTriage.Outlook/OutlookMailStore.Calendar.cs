using System.Globalization;
using System.Runtime.Versioning;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore : ICalendarStore
{
    // olFolderCalendar = 9, olAppointmentItem = 1 (for CreateItem)
    private const int FolderCalendar = 9;
    private const int NewAppointmentItem = 1;

    // OlMeetingStatus: olNonMeeting = 0, olMeeting = 1
    private const int OlNonMeeting = 0;
    private const int OlMeeting = 1;

    // OlMeetingResponse, for AppointmentItem.Respond
    private const int RespondTentative = 2;
    private const int RespondAccepted = 3;
    private const int RespondDeclined = 4;

    // OlMeetingRecipientType: olRequired = 1, olOptional = 2, olResource = 3
    private const int AttendeeRequired = 1;
    private const int AttendeeOptional = 2;
    private const int AttendeeResource = 3;

    /// <summary>olEmbeddeditem: attach an Outlook item itself rather than a file.</summary>
    private const int AttachEmbeddedItem = 5;

    /// <summary>A runaway recurrence can expand forever; nobody reads past this.</summary>
    private const int EventScanLimit = 500;

    // Where a meeting message keeps its meeting's times when it has no calendar
    // entry to ask (a cancellation for something already removed). PSETID_Appointment.
    private const string PropMeetingStart = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/820D0040";
    private const string PropMeetingEnd = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/820E0040";
    private const string PropMeetingLocation = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/8208001F";

    /// <summary>Set on meetings the Teams add-in created; the body usually has the link too.</summary>
    private const string PropTeamsUrl = "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/SkypeTeamsMeetingUrl";

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        _sta.InvokeAsync<IReadOnlyList<CalendarEvent>>(() =>
        {
            EnsureConnected();
            var events = new List<CalendarEvent>();

            dynamic? folder = null, items = null, found = null;
            try
            {
                folder = _session!.GetDefaultFolder(FolderCalendar);
                var storeId = ComUtil.Str(() => folder!.StoreID);
                items = folder!.Items;

                // Outlook only expands recurring meetings into occurrences on a
                // collection sorted by start, with this set before Restrict.
                items!.Sort("[Start]");
                items.IncludeRecurrences = true;
                found = items.Restrict($"[Start] < '{JetDate(to)}' AND [End] > '{JetDate(from)}'");

                // Count is meaningless once recurrences are included; walk instead.
                dynamic? item = found!.GetFirst();
                while (item is not null && events.Count < EventScanLimit)
                {
                    try
                    {
                        if (ReadEvent((object)item, storeId) is { } ev) events.Add(ev);
                    }
                    finally
                    {
                        var previous = item;
                        item = found.GetNext();
                        ComUtil.Release(previous);
                    }
                }
            }
            finally { ComUtil.ReleaseAll(found, items, folder); }

            return events.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
        }, ct);

    /// <summary>Jet filters take local wall-clock time, in the same form as HasReplySinceAsync uses.</summary>
    private static string JetDate(DateTimeOffset when) =>
        when.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);

    private static CalendarEvent? ReadEvent(object itemObj, string storeId)
    {
        dynamic item = itemObj;
        if (ComUtil.Int(() => item.Class) != ComUtil.OlAppointment) return null;

        return new CalendarEvent
        {
            Ref = new MailRef(ComUtil.Str(() => item.EntryID), storeId),
            Subject = ComUtil.Str(() => item.Subject),
            Start = ComUtil.Date(() => item.Start),
            End = ComUtil.Date(() => item.End),
            Location = ComUtil.Str(() => item.Location),
            Organizer = ComUtil.Str(() => item.Organizer),
            IsAllDay = ComUtil.Bool(() => item.AllDayEvent),
            Busy = (BusyStatus)ComUtil.Int(() => item.BusyStatus, (int)BusyStatus.Busy),
            Response = (MeetingResponse)ComUtil.Int(() => item.ResponseStatus),
            IsRecurring = ComUtil.Bool(() => item.IsRecurring),
            IsMeeting = ComUtil.Int(() => item.MeetingStatus) != OlNonMeeting,
        };
    }

    public Task<CalendarEventDetail> GetEventDetailAsync(CalendarEvent ev, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null;
            try
            {
                item = OpenEvent(ev);
                var body = ComUtil.Str(() => item!.Body);
                var location = ComUtil.Str(() => item!.Location);

                // The organizer is in the list with no response of their own; mark them by name.
                var organizer = ComUtil.Str(() => item!.Organizer);
                var attendees = ReadAttendees((object)item!)
                    .Select(a => organizer.Length > 0 && a.Response == MeetingResponse.None
                                 && string.Equals(a.Name, organizer, StringComparison.OrdinalIgnoreCase)
                        ? a with { Response = MeetingResponse.Organized }
                        : a)
                    .ToList();

                return new CalendarEventDetail
                {
                    Body = body.Trim(),
                    Attendees = attendees,
                    JoinUrl = MeetingLinks.Find(location + "\n" + body)
                              ?? MeetingLinks.Find(ComUtil.MapiString((object)item!, PropTeamsUrl)),
                };
            }
            finally { ComUtil.Release(item); }
        }, ct);

    /// <summary>
    /// The item for an event: the occurrence itself for a recurring meeting,
    /// so opening it shows that day rather than the series. Falls back to the
    /// series when the occurrence cannot be found (one that was moved).
    /// </summary>
    private dynamic OpenEvent(CalendarEvent ev)
    {
        var item = GetItem(ev.Ref);
        if (!ev.IsRecurring) return item;

        dynamic? pattern = null;
        try
        {
            pattern = item.GetRecurrencePattern();
            var occurrence = pattern!.GetOccurrence(ev.Start.LocalDateTime);
            if (occurrence is null) return item;

            ComUtil.Release(item);
            return occurrence;
        }
        catch { return item; }
        finally { ComUtil.Release(pattern); }
    }

    private static IReadOnlyList<Attendee> ReadAttendees(object itemObj)
    {
        dynamic item = itemObj;
        var attendees = new List<Attendee>();

        dynamic? recipients = null;
        try
        {
            recipients = item.Recipients;
            int count = ComUtil.Int(() => recipients!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? r = null;
                try
                {
                    r = recipients![i];
                    var type = ComUtil.Int(() => r!.Type, AttendeeRequired);
                    if (type == AttendeeResource) continue; // rooms and equipment

                    var address = ComUtil.MapiString((object)r!, ComUtil.PropRecipientSmtpAddress);
                    if (string.IsNullOrWhiteSpace(address)) address = ComUtil.Str(() => r!.Address);

                    attendees.Add(new Attendee(
                        ComUtil.Str(() => r!.Name),
                        address,
                        type == AttendeeOptional,
                        (MeetingResponse)ComUtil.Int(() => r!.MeetingResponseStatus)));
                }
                finally { ComUtil.Release(r); }
            }
        }
        catch { }
        finally { ComUtil.Release(recipients); }

        return attendees;
    }

    public Task<MeetingInvite?> GetInviteAsync(MailRef mail, CancellationToken ct = default) =>
        _sta.InvokeAsync<MeetingInvite?>(() =>
        {
            EnsureConnected();

            dynamic? item = null, appt = null;
            try
            {
                item = GetItem(mail);
                var kind = MailKinds.FromMessageClass(ComUtil.Str(() => item!.MessageClass));
                if (kind is not { } k || !k.IsMeeting()) return null;

                // Without adding it: asking must not put a cancelled meeting back.
                appt = ComUtil.Try<object?>(() => item!.GetAssociatedAppointment(false));
                var body = ComUtil.Str(() => item!.Body);

                if (appt is null)
                {
                    // Nothing on the calendar - usually a cancellation already
                    // dealt with. The message still carries the meeting's times.
                    var start = UtcProperty((object)item!, PropMeetingStart);
                    if (start is null) return null;
                    var location = ComUtil.MapiString((object)item!, PropMeetingLocation);

                    return new MeetingInvite
                    {
                        Message = mail,
                        Kind = k,
                        Subject = ComUtil.Str(() => item!.Subject),
                        Start = start.Value,
                        End = UtcProperty((object)item!, PropMeetingEnd) ?? start.Value,
                        Location = location,
                        JoinUrl = MeetingLinks.Find(location + "\n" + body),
                    };
                }

                var where = ComUtil.Str(() => appt!.Location);
                return new MeetingInvite
                {
                    Message = mail,
                    Kind = k,
                    Subject = ComUtil.Str(() => appt!.Subject),
                    Start = ComUtil.Date(() => appt!.Start),
                    End = ComUtil.Date(() => appt!.End),
                    Location = where,
                    Organizer = ComUtil.Str(() => appt!.Organizer),
                    IsAllDay = ComUtil.Bool(() => appt!.AllDayEvent),
                    IsRecurring = ComUtil.Bool(() => appt!.IsRecurring),
                    Response = (MeetingResponse)ComUtil.Int(() => appt!.ResponseStatus),
                    Appointment = new MailRef(ComUtil.Str(() => appt!.EntryID), ComUtil.Str(() => appt!.Parent.StoreID)),
                    JoinUrl = MeetingLinks.Find(where + "\n" + body),
                };
            }
            finally { ComUtil.ReleaseAll(appt, item); }
        }, ct);

    /// <summary>PropertyAccessor hands date properties back in UTC, unlike the object model.</summary>
    private static DateTimeOffset? UtcProperty(object item, string dasl)
    {
        object? accessor = null;
        try
        {
            accessor = ((dynamic)item).PropertyAccessor;
            var value = ((dynamic)accessor!).GetProperty(dasl);
            if (value is not DateTime dt || dt.Year < 1900) return null;
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToLocalTime();
        }
        catch { return null; }
        finally { ComUtil.Release(accessor); }
    }

    public Task RespondAsync(MailRef item, InviteResponse response, string note, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? source = null, appt = null, reply = null;
            try
            {
                source = GetItem(item);
                var itemClass = ComUtil.Int(() => source!.Class);

                if (itemClass == ComUtil.OlAppointment)
                {
                    appt = source;
                    source = null; // one object; released once, as appt
                }
                else if (itemClass == ComUtil.OlMeetingRequest)
                {
                    // True: put it on the calendar if Outlook has not already.
                    appt = source!.GetAssociatedAppointment(true);
                }
                else
                {
                    throw new InvalidOperationException("Only a meeting invitation can be accepted or declined.");
                }

                if (appt is null)
                    throw new InvalidOperationException("Outlook could not find this meeting on your calendar.");

                if ((MeetingResponse)ComUtil.Int(() => appt!.ResponseStatus) == MeetingResponse.Organized)
                    throw new InvalidOperationException("This is your own meeting - there is nobody to answer.");

                var code = response switch
                {
                    InviteResponse.Accept => RespondAccepted,
                    InviteResponse.Tentative => RespondTentative,
                    _ => RespondDeclined,
                };

                // No dialog: Respond hands back the answer as an unsent message.
                reply = appt!.Respond(code, true, false);

                if (ComUtil.Bool(() => appt!.ResponseRequested, true) && reply is not null)
                {
                    if (!string.IsNullOrWhiteSpace(note)) reply.Body = note.Trim();
                    reply.Send();
                }
                else
                {
                    // The organizer asked for no answer: record it, send nothing.
                    if (reply is not null) ComUtil.Try<object?>(() => { reply!.Close(1 /* olDiscard */); return null; });
                    ComUtil.Try<object?>(() => { appt!.Save(); return null; });
                }

                // A declined meeting leaves the calendar, as it does in Outlook.
                // Outlook may already have removed it, which makes this throw.
                if (response == InviteResponse.Decline)
                    ComUtil.Try<object?>(() => { appt!.Delete(); return null; });
            }
            finally { ComUtil.ReleaseAll(reply, appt, source); }
        }, ct);

    public Task RemoveCancelledAsync(MailRef cancellation, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null, appt = null;
            try
            {
                item = GetItem(cancellation);
                if (ComUtil.Int(() => item!.Class) != ComUtil.OlMeetingCancellation)
                    throw new InvalidOperationException("That is not a meeting cancellation.");

                appt = ComUtil.Try<object?>(() => item!.GetAssociatedAppointment(false));
                appt?.Delete(); // already gone is fine: it is off the calendar either way
            }
            finally { ComUtil.ReleaseAll(appt, item); }
        }, ct);

    public Task<MailRef> CreateEventAsync(NewCalendarEvent spec, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? appt = null;
            try
            {
                appt = _app!.CreateItem(NewAppointmentItem);
                Fill((object)appt!, spec);
                appt!.Save();

                return new MailRef(ComUtil.Str(() => appt!.EntryID), ComUtil.Str(() => appt!.Parent.StoreID));
            }
            finally { ComUtil.Release(appt); }
        }, ct);

    public Task ShowNewMeetingAsync(NewCalendarEvent spec, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();
            var me = MyAddresses();

            dynamic? appt = null, recipients = null;
            try
            {
                appt = _app!.CreateItem(NewAppointmentItem);
                Fill((object)appt!, spec);
                appt!.MeetingStatus = OlMeeting;

                recipients = appt.Recipients;
                foreach (var address in spec.Attendees.Where(a => !string.IsNullOrWhiteSpace(a) && !me.Contains(a)))
                {
                    dynamic? r = null;
                    try
                    {
                        r = recipients!.Add(address);
                        r!.Type = AttendeeRequired;
                    }
                    finally { ComUtil.Release(r); }
                }
                ComUtil.Try<object?>(() => recipients!.ResolveAll());

                // Shown, never sent, and not saved: closing it without
                // sending leaves nothing behind on the calendar.
                appt.Display(false);
            }
            finally { ComUtil.ReleaseAll(recipients, appt); }
        }, ct);

    private void Fill(object apptObj, NewCalendarEvent spec)
    {
        dynamic appt = apptObj;

        appt.Subject = spec.Subject;
        appt.Start = spec.Start.LocalDateTime;
        appt.End = spec.End.LocalDateTime;
        appt.BusyStatus = (int)BusyStatus.Busy;
        if (spec.Body.Length > 0) appt.Body = spec.Body;

        appt.ReminderSet = spec.ReminderMinutes > 0;
        if (spec.ReminderMinutes > 0) appt.ReminderMinutesBeforeStart = spec.ReminderMinutes;

        if (spec.AttachMail is not { IsEmpty: false } mail) return;

        // A nicety: the entry still stands if the mail has moved and cannot be attached.
        dynamic? source = null, attachments = null;
        try
        {
            source = GetItem(mail);
            attachments = appt.Attachments;
            attachments!.Add(source, AttachEmbeddedItem);
        }
        catch { }
        finally { ComUtil.ReleaseAll(attachments, source); }
    }

    public Task DeleteEventAsync(MailRef ev, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();
            dynamic? item = null;
            try
            {
                item = GetItem(ev);
                if (ComUtil.Int(() => item!.Class) != ComUtil.OlAppointment)
                    throw new InvalidOperationException("That is not a calendar entry.");
                item!.Delete();
            }
            finally { ComUtil.Release(item); }
        }, ct);

    public Task ShowEventAsync(CalendarEvent ev, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();
            dynamic? item = null;
            try
            {
                item = OpenEvent(ev);
                item.Display(false);
            }
            finally { ComUtil.Release(item); }
        }, ct);
}
