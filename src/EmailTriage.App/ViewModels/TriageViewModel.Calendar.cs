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

/// <summary>
/// What the schedule palette is putting on the calendar, and who would be
/// invited: time for yourself (s), or, as a meeting, a reply with an
/// invitation to everyone on the mail (Shift+S).
/// </summary>
public sealed record ScheduleTarget(
    string Subject,
    MailRef? Mail,
    IReadOnlyList<string> People,
    string Note,
    bool OfferUndo = true,
    bool AsMeeting = false,
    bool TitleFromQuery = false);

/// <summary>A switch on the meeting palette, each on its own Ctrl key.</summary>
public enum MeetingSwitch { Teams, AllDay, Repeat, ShowAs }

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

    /// <summary>The title typed with the time, for a new entry made on the Calendar tab.</summary>
    private string _scheduleTitle = "";
    private IReadOnlyList<CalendarEvent> _scheduleEvents = Array.Empty<CalendarEvent>();
    private DateTimeOffset? _scheduleCheckedUntil;
    private MeetingOptions _meeting = new();

    /// <summary>How many days ahead an all-day meeting offers when no day is typed.</summary>
    private const int AllDayChoices = 7;

    /// <summary>
    /// Most of the mail's text quoted into the invitation, as Outlook's own
    /// Reply with Meeting does; a long thread is cut here, not dropped.
    /// </summary>
    private const int MeetingQuoteLimit = 4000;

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

            var overlap = CalendarMath.Conflicts(events, invite.Start, invite.End, invite.Appointment);
            var clash = overlap.Where(e => !e.IsHold).ToList();
            var holds = overlap.Where(e => e.IsHold).ToList();
            var first = invite.IsRecurring ? " (first one)" : "";
            InviteHasClash = clash.Count > 0;
            InviteClash = clash.Count > 0 ? $"Clashes with {DescribeClash(clash)}{first}"
                : holds.Count > 0 ? $"Free apart from a HOLD: {DescribeClash(holds)}{first}"
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

        var summary = row.Summary;
        OpenSchedulePalette(new ScheduleTarget(
            ConversationGrouper.StripPrefixes(row.Subject),
            summary.Ref,
            PeopleOnSelected(row),
            $"From {summary.DisplaySender}, {summary.ReceivedUtc.ToLocalTime():ddd d MMM HH:mm}. The email is attached."));
    }

    /// <summary>
    /// Shift+S in the triage list: answer the mail with a meeting invitation
    /// to everyone on it, as Outlook's Reply with Meeting does.
    /// </summary>
    public void OpenReplyWithMeetingForSelected()
    {
        if (Selected is not { } row) return;

        var people = PeopleOnSelected(row);
        if (people.Count == 0)
        {
            Status = "Nobody on this mail can be invited - s blocks the time for you instead";
            return;
        }

        // The mail's own text, when the reading pane holds it, so the
        // invitation carries the context; otherwise the mail is attached.
        var summary = row.Summary;
        var body = OpenBody is { } b && row.Thread.Messages.Any(m => m.Ref.EntryId == b.Ref.EntryId) ? b : null;
        var quote = body?.PlainText.Trim() ?? "";
        if (quote.Length > MeetingQuoteLimit) quote = quote[..MeetingQuoteLimit].TrimEnd() + "\n[...]";

        var note = quote.Length > 0
            ? $"\n\n-----\nFrom: {body!.SenderName}\nSent: {body.ReceivedDisplay}\nSubject: {body.Subject}\n\n{quote}"
            : $"From {summary.DisplaySender}, {summary.ReceivedUtc.ToLocalTime():ddd d MMM HH:mm}. The email is attached.";

        OpenSchedulePalette(new ScheduleTarget(
            ConversationGrouper.StripPrefixes(row.Subject),
            quote.Length > 0 ? null : summary.Ref,
            people,
            note,
            AsMeeting: true));
    }

    /// <summary>The sender, and everyone on the thread when the reading pane holds it: real addresses only.</summary>
    private List<string> PeopleOnSelected(MailRowViewModel row)
    {
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

        return people.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void OpenSchedulePalette(ScheduleTarget target)
    {
        _schedule = target;
        _scheduleTitle = "";
        _scheduleEvents = Array.Empty<CalendarEvent>();
        _scheduleCheckedUntil = null;
        _meeting = new MeetingOptions { Teams = target.AsMeeting && _settings.TeamsByDefault };

        if (target.AsMeeting)
        {
            var who = target.People.Count == 1 ? target.People[0] : $"{target.People.Count} people";
            Palette.Open(
                PaletteMode.Schedule,
                "Reply with a meeting",
                "Enter opens it in Outlook to send · Ctrl+T Teams · Ctrl+D all day · Ctrl+R repeat · Ctrl+B show as · Esc cancel",
                $"{target.Subject}  ·  with {who}");
        }
        else if (target.TitleFromQuery)
        {
            Palette.Open(
                PaletteMode.Schedule,
                "New calendar entry",
                "Type a title and a time - \"Site walk tomorrow 2pm 1h\", \"fri 10-11am budget review\" or \"Focus 2h\" · Enter adds it · Esc cancel",
                "");
        }
        else
        {
            var hint = target.People.Count > 0
                ? "Enter blocks the time for you · Ctrl+Enter invites the people on it · type \"tomorrow 2pm 1h\" · Esc cancel"
                : "Enter blocks the time · type \"tomorrow 2pm 1h\", \"fri 10-11am\" or \"1h\" · Esc cancel";

            Palette.Open(PaletteMode.Schedule, "Put it on your calendar", hint, target.Subject);
        }

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

        if (_schedule is { TitleFromQuery: true })
        {
            // Title and time in one box; no time yet just means "offer free slots".
            EventTimeParser.TryParseWithTitle(query, now, out _scheduleTitle, out start, out var typed, _settings.DayShape);
            length = typed ?? length;
            Palette.ContextLine = _scheduleTitle.Length > 0 ? _scheduleTitle : "(type a title)";
        }
        else if (query.Length > 0)
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

        if (IsMeetingAllDay)
        {
            // Whole days: the typed one, or the week ahead.
            var first = start ?? now;
            var count = start is null ? AllDayChoices : 1;
            for (var i = 0; i < count; i++)
                entries.Add(DayEntry(MeetingOptions.WholeDays(first.AddDays(i)), now));
        }
        else if (start is { } s)
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

        var keep = Palette.SelectedIndex;
        Palette.SetEntries(entries);
        Palette.CreatePrompt = entries.Count == 0
            ? $"No free {(int)length.TotalMinutes} minutes in your working hours soon - type a time instead"
            : null;

        // Flipping a switch rebuilds the list; the day picked stays picked.
        if (keep > 0 && keep < entries.Count) Palette.SelectedIndex = keep;
        UpdateMeetingOptionsLine();
    }

    private bool IsMeetingAllDay => _schedule is { AsMeeting: true } && _meeting.AllDay;

    /// <summary>The switches, described from the day picked ("every Tuesday").</summary>
    public void UpdateMeetingOptionsLine()
    {
        if (_schedule is not { AsMeeting: true } || Palette.Mode != PaletteMode.Schedule) return;

        var start = (Palette.Selected?.Payload as TimeSlot?)?.Start;
        Palette.OptionsLine = _meeting.Describe(start);
    }

    /// <summary>Ctrl+T, Ctrl+D, Ctrl+R or Ctrl+B in the meeting palette. False when it is not the meeting palette.</summary>
    public bool ToggleMeetingSwitch(MeetingSwitch which)
    {
        if (!Palette.IsOpen || Palette.Mode != PaletteMode.Schedule || _schedule is not { AsMeeting: true }) return false;

        _meeting = which switch
        {
            MeetingSwitch.Teams => _meeting.ToggleTeams(),
            MeetingSwitch.AllDay => _meeting.ToggleAllDay(),
            MeetingSwitch.Repeat => _meeting.NextRepeat(),
            _ => _meeting.NextShowAs(),
        };

        // All day swaps the list between times and days; the rest only reword it.
        if (which == MeetingSwitch.AllDay) RefreshSchedulePalette();
        else UpdateMeetingOptionsLine();
        return true;
    }

    private PaletteEntry DayEntry(TimeSlot day, DateTimeOffset now)
    {
        var primary = $"{CalendarMath.DayLabel(day.Start.Date, now.Date)} · all day";

        string secondary;
        if (_scheduleCheckedUntil is null) secondary = "checking your calendar...";
        else if (day.End > _scheduleCheckedUntil) secondary = "further out than the calendar was read";
        else
        {
            var busy = CalendarMath.Conflicts(_scheduleEvents, day.Start, day.End);
            secondary = busy.Count == 0 ? "nothing else that day" : $"also that day: {DescribeClash(busy)}";
        }

        return new PaletteEntry(primary, secondary, day, Array.Empty<int>());
    }

    private PaletteEntry SlotEntry(TimeSlot slot, DateTimeOffset now)
    {
        var primary = $"{CalendarMath.DayLabel(slot.Start.Date, now.Date)} · {CalendarMath.TimeRange(slot.Start, slot.End)}";

        string secondary;
        if (_scheduleCheckedUntil is null) secondary = "checking your calendar...";
        else if (slot.End > _scheduleCheckedUntil) secondary = "further out than the calendar was read - clashes not checked";
        else
        {
            var overlap = CalendarMath.Conflicts(_scheduleEvents, slot.Start, slot.End);
            var clash = overlap.Where(e => !e.IsHold).ToList();
            var holds = overlap.Where(e => e.IsHold).ToList();
            secondary = clash.Count > 0 ? $"clashes with {DescribeClash(clash)}"
                : holds.Count > 0 ? $"over a HOLD: {DescribeClash(holds)} · {CalendarMath.Countdown(slot.Start - now)}"
                : $"free · {CalendarMath.Countdown(slot.Start - now)}";
        }

        return new PaletteEntry(primary, secondary, slot, Array.Empty<int>());
    }

    private async Task ConfirmScheduleAsync(bool invite)
    {
        if (_schedule is not { } t || Palette.Selected?.Payload is not TimeSlot slot) return;

        if (t.AsMeeting)
        {
            await ConfirmReplyWithMeetingAsync(t, slot).ConfigureAwait(true);
            return;
        }

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
            Subject = t.TitleFromQuery ? (_scheduleTitle.Length > 0 ? _scheduleTitle : "Busy")
                    : t.Subject.Length > 0 ? t.Subject : "Follow up",
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

    private async Task ConfirmReplyWithMeetingAsync(ScheduleTarget t, TimeSlot slot)
    {
        var options = _meeting;
        Palette.Close();

        var when = options.AllDay
            ? $"{CalendarMath.DayLabel(slot.Start.Date, _clock.Now.Date)} all day"
            : $"{CalendarMath.DayLabel(slot.Start.Date, _clock.Now.Date)} {CalendarMath.TimeRange(slot.Start, slot.End)}";

        var spec = new NewCalendarEvent
        {
            Subject = t.Subject.Length > 0 ? t.Subject : "Meeting",
            Start = slot.Start,
            End = slot.End,
            Body = t.Note,
            AttachMail = t.Mail,
            Attendees = t.People,
            IsAllDay = options.AllDay,
            ShowAs = options.ShowAs,
            Repeat = options.Repeat,
            AddTeams = options.Teams,

            // Outlook's own defaults: the evening before for a whole day, else a quarter hour.
            ReminderMinutes = options.AllDay ? 18 * 60 : 15,
        };

        try
        {
            // Other people get this, so it goes out from Outlook after a look, never from here.
            Status = options.Teams ? "Opening the invitation in Outlook and adding Teams..." : "Opening the invitation in Outlook...";
            var teams = await _calendar.ShowNewMeetingAsync(spec).ConfigureAwait(true);

            var repeat = options.Repeat == Repeat.Once ? "" : $", {MeetingOptions.DescribeRepeat(options.Repeat, slot.Start)}";
            Status = teams switch
            {
                TeamsOutcome.Added => $"Invitation for {when}{repeat} is open in Outlook with Teams - check it and press Send",
                TeamsOutcome.NotFound => $"Invitation for {when}{repeat} is open in Outlook, but the Teams button was not found - press Teams Meeting there, then Send",
                _ => $"Invitation for {when}{repeat} is open in Outlook - check it and press Send",
            };
        }
        catch (Exception ex)
        {
            Status = $"Could not open the invitation: {ex.Message}";
        }
    }
}
