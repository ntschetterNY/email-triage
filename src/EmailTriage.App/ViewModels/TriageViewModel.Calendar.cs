using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>What the answer palette (y) is answering: an invitation in the Inbox or a meeting on the calendar.</summary>
public sealed record RsvpTarget(
    MailRef Item,
    string Subject,
    string When,
    string Organizer,
    MeetingResponse Current,
    bool IsCancellation = false,
    bool IsSeries = false,
    bool ArchiveAfter = false);

/// <summary>What the schedule palette (s) is putting on the calendar, and who would be invited.</summary>
public sealed record ScheduleTarget(
    string Subject,
    MailRef? Mail,
    IReadOnlyList<string> People,
    string Note,
    bool OfferUndo = true);

/// <summary>A pick in the answer palette; no response means "remove from calendar".</summary>
internal sealed record RsvpChoice(InviteResponse? Response);

/// <summary>
/// The calendar side of triage: the invitation card in the reading pane,
/// answering invitations (y), and putting mail on the calendar (s). Both
/// palettes are also opened from the board and the Calendar tab.
/// </summary>
public sealed partial class TriageViewModel
{
    /// <summary>The meeting the selected message is about, when it is a meeting message.</summary>
    [ObservableProperty] private MeetingInvite? _invite;
    [ObservableProperty] private string _inviteWhen = "";
    [ObservableProperty] private string _inviteMeta = "";
    [ObservableProperty] private string _inviteClash = "";
    [ObservableProperty] private bool _inviteHasClash;

    /// <summary>What y does for this message ("answer", "remove from calendar"), or empty.</summary>
    [ObservableProperty] private string _inviteAction = "";

    /// <summary>Raised after the app changes the calendar, so the agenda and strip reread it.</summary>
    public event EventHandler? CalendarChanged;

    private CancellationTokenSource? _inviteLoad;
    private RsvpTarget? _rsvp;
    private ScheduleTarget? _schedule;
    private IReadOnlyList<CalendarEvent> _scheduleEvents = Array.Empty<CalendarEvent>();
    private DateTimeOffset? _scheduleCheckedUntil;

    public bool HasInvite => Invite is not null;
    public bool HasInviteAction => InviteAction.Length > 0;

    partial void OnInviteChanged(MeetingInvite? value) => OnPropertyChanged(nameof(HasInvite));
    partial void OnInviteActionChanged(string value) => OnPropertyChanged(nameof(HasInviteAction));

    // ---- the invitation card ------------------------------------------------

    private async Task LoadInviteAsync(MailRowViewModel? row)
    {
        _inviteLoad?.Cancel();
        Invite = null;
        InviteAction = "";

        if (row is null || !row.Summary.Kind.IsMeeting()) return;

        var cts = new CancellationTokenSource();
        _inviteLoad = cts;

        try
        {
            var invite = await _calendar.GetInviteAsync(row.Summary.Ref).ConfigureAwait(true);
            if (cts.IsCancellationRequested || invite is null) return;

            InviteWhen = $"{invite.Start:dddd d MMMM} · {CalendarMath.TimeRange(invite.Start, invite.End, invite.IsAllDay)}"
                       + (invite.Location.Length > 0 ? $" · {invite.Location}" : "");
            InviteMeta = DescribeInvite(invite, row.Summary.DisplaySender);
            InviteClash = "";
            InviteHasClash = false;
            InviteAction = invite.Kind switch
            {
                MailKind.MeetingRequest => "accept, maybe or decline",
                MailKind.MeetingCancellation when invite.Appointment is not null => "remove it from your calendar",
                _ => "",
            };
            Invite = invite;

            // Only an open question needs the clash check.
            if (invite.Kind != MailKind.MeetingRequest || invite.IsAllDay) return;

            var events = await _calendar.GetEventsAsync(invite.Start, invite.End).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            var clash = CalendarMath.Conflicts(events, invite.Start, invite.End, invite.Appointment);
            var first = invite.IsRecurring ? " (first one)" : "";
            InviteHasClash = clash.Count > 0;
            InviteClash = clash.Count > 0
                ? $"Clashes with {DescribeClash(clash)}{first}"
                : $"You're free then{first}";
        }
        catch (Exception) when (cts.IsCancellationRequested) { }
        catch
        {
            // The card is a convenience; the message itself is still readable.
        }
    }

    private static string DescribeInvite(MeetingInvite invite, string sender)
    {
        var parts = new List<string>();

        switch (invite.Kind)
        {
            case MailKind.MeetingRequest:
                parts.Add(invite.Response switch
                {
                    MeetingResponse.Accepted => "You accepted",
                    MeetingResponse.Tentative => "You said maybe",
                    MeetingResponse.Declined => "You declined",
                    _ => "Not answered yet",
                });
                if (invite.Organizer.Length > 0) parts.Add($"from {invite.Organizer}");
                break;

            case MailKind.MeetingCancellation:
                parts.Add(invite.Appointment is null ? "Cancelled - already off your calendar" : "Cancelled - still on your calendar");
                break;

            case MailKind.MeetingAccepted: parts.Add($"{sender} accepted"); break;
            case MailKind.MeetingTentative: parts.Add($"{sender} said maybe"); break;
            case MailKind.MeetingDeclined: parts.Add($"{sender} declined"); break;
        }

        if (invite.IsRecurring) parts.Add("repeats");
        return string.Join(" · ", parts);
    }

    private static string DescribeClash(IReadOnlyList<CalendarEvent> clash)
    {
        var first = clash[0];
        var name = string.IsNullOrWhiteSpace(first.Subject) ? "(no subject)" : first.Subject.Trim();
        var more = clash.Count > 1 ? $" and {clash.Count - 1} more" : "";
        return $"{name} ({CalendarMath.TimeRange(first.Start, first.End)}){more}";
    }

    // ---- answering invitations (y) --------------------------------------------

    /// <summary>y in the triage list: answer the selected invitation, or clear up a cancellation.</summary>
    public void OpenRsvpForSelected()
    {
        if (Selected is not { } row) return;

        var kind = row.Summary.Kind;
        if (!kind.NeedsAnswer())
        {
            Status = kind.IsMeeting()
                ? "Nothing to answer - that is someone's reply to your meeting"
                : "That is not a meeting invitation";
            return;
        }

        // The card may still be loading after a quick keypress; fall back to the message.
        var invite = Invite is { } i && i.Message.EntryId == row.Summary.Ref.EntryId ? i : null;

        if (kind == MailKind.MeetingCancellation && invite is { Appointment: null })
        {
            Status = "That meeting is already off your calendar - archive the email when you're done with it";
            return;
        }

        OpenRsvpPalette(new RsvpTarget(
            row.Summary.Ref,
            row.Subject,
            invite is null ? "" : InviteWhen,
            invite?.Organizer is { Length: > 0 } organizer ? organizer : row.Summary.DisplaySender,
            invite?.Response ?? MeetingResponse.NotResponded,
            IsCancellation: kind == MailKind.MeetingCancellation,
            IsSeries: invite?.IsRecurring ?? false,
            ArchiveAfter: true));
    }

    public void OpenRsvpPalette(RsvpTarget target)
    {
        _rsvp = target;

        var title = target.IsCancellation ? "Meeting cancelled" : "Answer the invitation";
        var hint = target.IsCancellation
            ? "Enter removes it from your calendar · Esc cancel"
            : "Enter sends · type a note to go with it · ↑↓ choose · Esc cancel";
        var context = target.When.Length > 0 ? $"{target.Subject}  ·  {target.When}" : target.Subject;
        if (target.IsSeries && !target.IsCancellation) context += "  ·  every occurrence";

        Palette.Open(PaletteMode.Rsvp, title, hint, context);
        RefreshRsvpPalette();
    }

    private void RefreshRsvpPalette()
    {
        if (_rsvp is not { } t) return;

        if (t.IsCancellation)
        {
            Palette.SetEntries(new[]
            {
                new PaletteEntry("Remove from your calendar", t.ArchiveAfter ? "and archive this email" : "", new RsvpChoice(null), Array.Empty<int>()),
            });
            return;
        }

        // Typing is the note, so the choices stay put while it is written.
        var note = Palette.Query.Trim();
        var who = t.Organizer.Length > 0 ? t.Organizer : "the organizer";

        string Secondary(InviteResponse response, MeetingResponse same, string extra = "")
        {
            var parts = new List<string>();
            if (t.Current == same) parts.Add("your answer now");
            parts.Add(note.Length > 0 ? $"with your note to {who}" : $"lets {who} know");
            if (extra.Length > 0) parts.Add(extra);
            return string.Join(" · ", parts);
        }

        var keep = Palette.SelectedIndex;
        Palette.SetEntries(new[]
        {
            new PaletteEntry("Accept", Secondary(InviteResponse.Accept, MeetingResponse.Accepted), new RsvpChoice(InviteResponse.Accept), Array.Empty<int>()),
            new PaletteEntry("Maybe", Secondary(InviteResponse.Tentative, MeetingResponse.Tentative, "tentative"), new RsvpChoice(InviteResponse.Tentative), Array.Empty<int>()),
            new PaletteEntry("Decline", Secondary(InviteResponse.Decline, MeetingResponse.Declined, "takes it off your calendar"), new RsvpChoice(InviteResponse.Decline), Array.Empty<int>()),
        });

        // SetEntries goes back to the top; a note typed after picking Decline must not turn it into Accept.
        if (keep > 0) Palette.SelectedIndex = Math.Min(keep, Palette.Entries.Count - 1);
    }

    private async Task ConfirmRsvpAsync()
    {
        if (_rsvp is not { } t || Palette.Selected?.Payload is not RsvpChoice choice) return;

        var note = Palette.Query.Trim();
        Palette.Close();

        try
        {
            Status = "Answering...";
            if (choice.Response is { } response)
                await _calendar.RespondAsync(t.Item, response, note).ConfigureAwait(true);
            else
                await _calendar.RemoveCancelledAsync(t.Item).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not answer the invitation: {ex.Message}";
            return;
        }

        var message = choice.Response switch
        {
            InviteResponse.Accept => $"Accepted \"{t.Subject}\"",
            InviteResponse.Tentative => $"Said maybe to \"{t.Subject}\"",
            InviteResponse.Decline => $"Declined \"{t.Subject}\"",
            _ => $"Took \"{t.Subject}\" off your calendar",
        };
        if (choice.Response is not null && note.Length > 0) message += " with your note";

        CalendarChanged?.Invoke(this, EventArgs.Empty);

        // Answered is dealt with. Outlook sometimes deletes the request itself
        // once answered; a refresh then shows it gone either way.
        if (t.ArchiveAfter)
        {
            await ArchiveConversationOfAsync(t.Item).ConfigureAwait(true);
            await RefreshQuietlyAsync().ConfigureAwait(true);
            message += " · archived";
        }

        Status = message;
    }

    // ---- putting mail on the calendar (s) ---------------------------------------

    /// <summary>s in the triage list: block time for the selected conversation, or invite its people.</summary>
    public void OpenScheduleForSelected()
    {
        if (Selected is not { } row) return;

        var people = new List<string>();
        void Add(string address)
        {
            // Exchange senders can come back as X.500 names; only real addresses can be invited.
            if (address.Contains('@')) people.Add(address.Trim());
        }

        Add(row.Summary.SenderAddress);

        // Everyone on the thread, when the reading pane holds this conversation.
        if (OpenBody is { } body && row.Thread.Messages.Any(m => m.Ref.EntryId == body.Ref.EntryId))
        {
            Add(body.SenderAddress);
            foreach (var r in body.To.Concat(body.Cc)) Add(r.Address);
        }

        var summary = row.Summary;
        OpenSchedulePalette(new ScheduleTarget(
            ConversationGrouper.StripPrefixes(row.Subject),
            summary.Ref,
            people.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            $"From {summary.DisplaySender}, {summary.ReceivedUtc.ToLocalTime():ddd d MMM HH:mm}. The email is attached."));
    }

    public void OpenSchedulePalette(ScheduleTarget target)
    {
        _schedule = target;
        _scheduleEvents = Array.Empty<CalendarEvent>();
        _scheduleCheckedUntil = null;

        var hint = target.People.Count > 0
            ? "Enter blocks the time for you · Ctrl+Enter invites the people on it · type \"tomorrow 2pm 1h\" · Esc cancel"
            : "Enter blocks the time · type \"tomorrow 2pm 1h\", \"fri 10-11am\" or \"1h\" · Esc cancel";

        Palette.Open(PaletteMode.Schedule, "Put it on your calendar", hint, target.Subject);
        RefreshSchedulePalette();
        _ = LoadScheduleEventsAsync();
    }

    private async Task LoadScheduleEventsAsync()
    {
        var now = _clock.Now;
        var until = new DateTimeOffset(now.Date, now.Offset).AddDays(Math.Max(1, _settings.CalendarDaysAhead));

        try
        {
            var events = await _calendar.GetEventsAsync(now, until).ConfigureAwait(true);
            if (!Palette.IsOpen || Palette.Mode != PaletteMode.Schedule) return;

            _scheduleEvents = events;
            _scheduleCheckedUntil = until;
        }
        catch (Exception ex)
        {
            Status = $"Could not read your calendar, so clashes are not checked: {ex.Message}";
            _scheduleCheckedUntil = now; // stop saying "checking"
        }

        if (Palette.IsOpen && Palette.Mode == PaletteMode.Schedule) RefreshSchedulePalette();
    }

    private void RefreshSchedulePalette()
    {
        var now = _clock.Now;
        var query = Palette.Query.Trim();
        var length = TimeSpan.FromMinutes(Math.Max(5, _settings.DefaultEventMinutes));
        DateTimeOffset? start = null;

        if (query.Length > 0)
        {
            if (!EventTimeParser.TryParse(query, now, out start, out var typed, _settings.DayShape))
            {
                Palette.SetEntries(Array.Empty<PaletteEntry>());
                Palette.CreatePrompt = $"\"{query}\" is not a time I understand - try \"tomorrow 2pm\", \"fri 10-11am\" or \"1h\"";
                return;
            }
            length = typed ?? length;
        }

        var entries = new List<PaletteEntry>();

        if (start is { } s)
        {
            entries.Add(SlotEntry(new TimeSlot(s, s + length), now));
        }
        else if (_scheduleCheckedUntil is null)
        {
            entries.Add(new PaletteEntry("Reading your calendar...", "", new object(), Array.Empty<int>()));
        }
        else
        {
            // Nothing typed but a length: the next gaps that fit it.
            var shape = _settings.DayShape;
            var slots = CalendarMath.FreeSlots(
                _scheduleEvents, now, length, shape.Morning, shape.Evening,
                Math.Max(1, _settings.CalendarDaysAhead), max: 6);
            entries.AddRange(slots.Select(slot => SlotEntry(slot, now)));
        }

        Palette.SetEntries(entries);
        Palette.CreatePrompt = entries.Count == 0
            ? $"No free {(int)length.TotalMinutes} minutes in your working hours soon - type a time instead"
            : null;
    }

    private PaletteEntry SlotEntry(TimeSlot slot, DateTimeOffset now)
    {
        var primary = $"{CalendarMath.DayLabel(slot.Start.Date, now.Date)} · {CalendarMath.TimeRange(slot.Start, slot.End)}";

        string secondary;
        if (_scheduleCheckedUntil is null) secondary = "checking your calendar...";
        else if (slot.End > _scheduleCheckedUntil) secondary = "further out than the calendar was read - clashes not checked";
        else
        {
            var clash = CalendarMath.Conflicts(_scheduleEvents, slot.Start, slot.End);
            secondary = clash.Count == 0
                ? $"free · {CalendarMath.Countdown(slot.Start - now)}"
                : $"clashes with {DescribeClash(clash)}";
        }

        return new PaletteEntry(primary, secondary, slot, Array.Empty<int>());
    }

    private async Task ConfirmScheduleAsync(bool invite)
    {
        if (_schedule is not { } t || Palette.Selected?.Payload is not TimeSlot slot) return;

        if (invite && t.People.Count == 0)
        {
            // The hint line, not CreatePrompt: that would sit over the slots.
            Palette.Hint = "Nobody to invite from this one - Enter blocks the time for you · Esc cancel";
            return;
        }

        Palette.Close();

        var when = $"{CalendarMath.DayLabel(slot.Start.Date, _clock.Now.Date)} {CalendarMath.TimeRange(slot.Start, slot.End)}";
        var spec = new NewCalendarEvent
        {
            Subject = t.Subject.Length > 0 ? t.Subject : "Follow up",
            Start = slot.Start,
            End = slot.End,
            Body = t.Note,
            AttachMail = t.Mail,
            Attendees = invite ? t.People : Array.Empty<string>(),
            ReminderMinutes = invite ? 15 : Math.Max(0, _settings.BlockReminderMinutes),
        };

        try
        {
            if (invite)
            {
                // Other people get this, so it goes out from Outlook after a look, never from here.
                await _calendar.ShowNewMeetingAsync(spec).ConfigureAwait(true);
                Status = $"Invitation for {when} is open in Outlook - check it and press Send";
                return;
            }

            var created = await _calendar.CreateEventAsync(spec).ConfigureAwait(true);
            CalendarChanged?.Invoke(this, EventArgs.Empty);

            PushUndo("calendar block", async () =>
            {
                await _calendar.DeleteEventAsync(created).ConfigureAwait(true);
                CalendarChanged?.Invoke(this, EventArgs.Empty);
            });

            Status = $"On your calendar {when}: {spec.Subject}" + (t.OfferUndo ? " · z undoes" : "");
        }
        catch (Exception ex)
        {
            Status = $"Could not add it to your calendar: {ex.Message}";
        }
    }
}
