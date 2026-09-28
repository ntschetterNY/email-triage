using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>How close the next meeting is, for colouring the strip in the top bar.</summary>
public enum StripState { Clear, Upcoming, Soon, Now }

/// <summary>One meeting in the agenda list.</summary>
public sealed class AgendaRow
{
    public AgendaRow(CalendarEvent ev, DateTimeOffset now)
    {
        Event = ev;
        Day = CalendarMath.DayLabel(ev.Start.Date, now.Date);
        Time = ev.IsAllDay ? "All day" : $"{ev.Start:HH:mm}";
        Length = ev.IsAllDay ? "" : Duration(ev.End - ev.Start);
        IsPast = ev.End <= now;
        IsNow = !ev.IsAllDay && ev.Start <= now && ev.End > now;
    }

    public CalendarEvent Event { get; }

    /// <summary>The group header: "Today", "Tomorrow", "Thu 25 Sep".</summary>
    public string Day { get; }
    public string Time { get; }
    public string Length { get; }
    public string Subject => string.IsNullOrWhiteSpace(Event.Subject) ? "(no subject)" : Event.Subject;
    public string Location => Event.Location;
    public bool HasLocation => Event.Location.Length > 0;
    public bool IsPast { get; }
    public bool IsNow { get; }

    /// <summary>A short word on your answer, when it is anything but a plain yes.</summary>
    public string Tag => Event switch
    {
        { NeedsResponse: true } => "NOT ANSWERED",
        { Response: MeetingResponse.Tentative } => "TENTATIVE",
        { IsDeclined: true } => "DECLINED",
        { Busy: BusyStatus.Free, IsAllDay: false } => "FREE",
        _ => "",
    };

    public bool HasTag => Tag.Length > 0;

    /// <summary>The detail pane's heading line: "Thursday 25 September · 14:00–15:00".</summary>
    public string When => $"{Event.Start:dddd d MMMM} · {CalendarMath.TimeRange(Event.Start, Event.End, Event.IsAllDay)}";

    /// <summary>Who called it and where you stand: "Organised by Sam · you accepted · repeats".</summary>
    public string About
    {
        get
        {
            var parts = new List<string>();
            if (Event.IsOrganizer) parts.Add(Event.IsMeeting ? "Your meeting" : "Your appointment");
            else if (Event.Organizer.Length > 0) parts.Add($"Organised by {Event.Organizer}");

            if (Event.CanRespond)
            {
                parts.Add(Event.Response switch
                {
                    MeetingResponse.Accepted => "you accepted",
                    MeetingResponse.Tentative => "you said maybe",
                    MeetingResponse.Declined => "you declined",
                    _ => "not answered yet",
                });
            }

            if (Event.IsRecurring) parts.Add("repeats");
            return string.Join(" · ", parts);
        }
    }

    public bool CanRespond => Event.CanRespond;

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
        : span.Minutes == 0 ? $"{(int)span.TotalHours}h"
        : $"{(int)span.TotalHours}h{span.Minutes:00}";
}

/// <summary>
/// The Calendar tab and the "next meeting" strip in the top bar. Reads the
/// Outlook calendar a couple of weeks ahead, rereads it every few minutes and
/// after anything the app itself changes, and ticks the strip's countdown
/// from what it has in hand.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly ICalendarStore _store;
    private readonly IClock _clock;
    private readonly AppSettings _settings;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(20) };
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _detailLoad;
    private bool _loadRunning;
    private IReadOnlyList<CalendarEvent> _events = Array.Empty<CalendarEvent>();

    /// <summary>Reread this often even when nothing prompts it, to catch changes made in Outlook.</summary>
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(3);

    public ObservableCollection<AgendaRow> Rows { get; } = new();

    [ObservableProperty] private AgendaRow? _selected;
    [ObservableProperty] private CalendarEventDetail? _detail;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string _stripText = "";
    [ObservableProperty] private StripState _stripState;

    /// <summary>The meeting the strip is talking about; clicking the strip opens it.</summary>
    public CalendarEvent? StripEvent { get; private set; }

    public CalendarViewModel(ICalendarStore store, IClock clock, AppSettings settings)
    {
        _store = store;
        _clock = clock;
        _settings = settings;

        _tick.Tick += async (_, _) =>
        {
            if (_clock.Now - _loadedAt >= ReloadEvery) await RefreshQuietlyAsync().ConfigureAwait(true);
            else UpdateStrip();
        };
    }

    /// <summary>What is on your calendar, soonest first, from the last read.</summary>
    public IReadOnlyList<CalendarEvent> Events => _events;

    public void Start() => _tick.Start();

    public Task LoadAsync() => LoadCoreAsync(quiet: false);

    /// <summary>A reread nobody asked for: leaves the status line and the selection alone.</summary>
    public Task RefreshQuietlyAsync() => LoadCoreAsync(quiet: true);

    // Loads are serialised like the triage list's: one asked for mid-load
    // (after an RSVP, say) runs once the current one finishes.
    private bool _reloadPending;
    private string _shown = "";

    private async Task LoadCoreAsync(bool quiet)
    {
        if (_loadRunning)
        {
            _reloadPending = true;
            return;
        }

        _loadRunning = true;
        try
        {
            do
            {
                _reloadPending = false;
                await LoadOnceAsync(quiet).ConfigureAwait(true);
            }
            while (_reloadPending);
        }
        finally
        {
            _loadRunning = false;
        }
    }

    private async Task LoadOnceAsync(bool quiet)
    {
        if (!quiet) IsLoading = true;
        try
        {
            var now = _clock.Now;
            var from = new DateTimeOffset(now.Date, now.Offset);
            var to = from.AddDays(Math.Max(1, _settings.CalendarDaysAhead));

            _events = await _store.GetEventsAsync(from, to).ConfigureAwait(true);
            _loadedAt = now;

            var rows = _events.Select(ev => new AgendaRow(ev, now)).ToList();

            // Rebuilding the list resets its scroll and reopens the selected
            // meeting, so only do it when something on screen would change.
            var shown = string.Join('\n', rows.Select(r =>
                $"{r.Event.Key}|{r.Subject}|{r.Location}|{r.Event.End.UtcTicks}|{r.Tag}|{r.Day}|{r.IsPast}|{r.IsNow}"));

            if (shown != _shown)
            {
                _shown = shown;
                var keep = Selected?.Event.Key;

                Rows.Clear();
                foreach (var row in rows) Rows.Add(row);

                // Land where the user was, else on what is on now or next.
                Selected = Rows.FirstOrDefault(r => r.Event.Key == keep)
                           ?? Rows.FirstOrDefault(r => !r.IsPast && !r.Event.IsAllDay)
                           ?? Rows.FirstOrDefault(r => !r.IsPast)
                           ?? Rows.LastOrDefault();
            }

            UpdateStrip();

            if (!quiet)
            {
                var today = Rows.Count(r => r.Day == "Today" && !r.Event.IsDeclined);
                Status = today == 0
                    ? "Nothing on your calendar today"
                    : $"{today} on your calendar today";
            }
        }
        catch (Exception ex)
        {
            if (!quiet) Status = $"Could not read your calendar: {ex.Message}";
        }
        finally
        {
            if (!quiet) IsLoading = false;
        }
    }

    // ---- the strip ----------------------------------------------------------

    /// <summary>
    /// "Now · Standup · ends in 12 min", "Next · Design review in 25 min · 14:00",
    /// or quiet when the rest of the day is free.
    /// </summary>
    private void UpdateStrip()
    {
        var now = _clock.Now;
        var today = _events.Where(e => e.Start.Date == now.Date || e.Overlaps(now, now.AddMinutes(1))).ToList();

        var current = CalendarMath.Current(today, now).FirstOrDefault();
        var next = CalendarMath.Next(today.Where(e => e.Start.Date == now.Date), now);

        if (next is not null && next.Start - now <= TimeSpan.FromMinutes(5))
        {
            StripEvent = next;
            StripState = StripState.Soon;
            StripText = $"{Title(next)} {CalendarMath.Countdown(next.Start - now)} · {next.Start:HH:mm}{Where(next)}";
        }
        else if (current is not null)
        {
            StripEvent = current;
            StripState = StripState.Now;
            StripText = $"Now · {Title(current)} · ends {CalendarMath.Countdown(current.End - now)}"
                      + (next is null ? "" : $" · then {Title(next)} at {next.Start:HH:mm}");
        }
        else if (next is not null)
        {
            StripEvent = next;
            StripState = StripState.Upcoming;
            StripText = $"Next · {Title(next)} {CalendarMath.Countdown(next.Start - now)} · {next.Start:HH:mm}{Where(next)}";
        }
        else
        {
            StripEvent = null;
            StripState = StripState.Clear;
            StripText = _loadedAt == DateTimeOffset.MinValue ? "" : "No more meetings today";
        }

        OnPropertyChanged(nameof(StripEvent));
    }

    private static string Title(CalendarEvent e) =>
        string.IsNullOrWhiteSpace(e.Subject) ? "(no subject)" : e.Subject.Trim();

    private static string Where(CalendarEvent e) =>
        e.Location.Length == 0 || e.Location.Length > 40 ? "" : $" · {e.Location}";

    // ---- the agenda -----------------------------------------------------------

    public void Move(int delta)
    {
        if (Rows.Count == 0) return;
        var index = Selected is null ? 0 : Rows.IndexOf(Selected) + delta;
        Selected = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    public void MoveToEnd(bool last) => Selected = last ? Rows.LastOrDefault() : Rows.FirstOrDefault();

    /// <summary>Shows a meeting in the agenda - from the strip, or after scheduling one.</summary>
    public void Select(CalendarEvent ev) =>
        Selected = Rows.FirstOrDefault(r => r.Event.Key == ev.Key) ?? Selected;

    partial void OnSelectedChanged(AgendaRow? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(AgendaRow? row)
    {
        _detailLoad?.Cancel();
        Detail = null;
        if (row is null) return;

        var cts = new CancellationTokenSource();
        _detailLoad = cts;

        try
        {
            // A pause, so holding j does not open every meeting on the way past.
            await Task.Delay(120, cts.Token).ConfigureAwait(true);
            var detail = await _store.GetEventDetailAsync(row.Event).ConfigureAwait(true);
            if (!cts.IsCancellationRequested) Detail = detail;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Status = $"Could not open that meeting: {ex.Message}";
        }
    }

    public async Task OpenInOutlookAsync()
    {
        if (Selected is not { } row) return;
        try
        {
            await _store.ShowEventAsync(row.Event).ConfigureAwait(true);
            Status = $"Opened in Outlook: {row.Subject}";
        }
        catch (Exception ex)
        {
            Status = $"Could not open it in Outlook: {ex.Message}";
        }
    }

    /// <summary>Enter on a meeting: join it when it has a link, else open it in Outlook.</summary>
    public async Task ActivateAsync()
    {
        if (Selected is not { } row) return;

        var detail = Detail ?? await ReadDetailAsync(row.Event).ConfigureAwait(true);
        if (detail?.JoinUrl is { } url) Status = Join(row.Event, url);
        else await OpenInOutlookAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// A click on a meeting, in the strip or the agenda: shows it here and,
    /// when it is on now or about to start, joins it in the same click.
    /// Meetings further out only open, so browsing the agenda never dials in.
    /// </summary>
    public async Task ClickAsync(CalendarEvent ev)
    {
        Select(ev);
        if (!IsJoinable(ev)) return;

        var detail = await ReadDetailAsync(ev).ConfigureAwait(true);
        if (detail?.JoinUrl is { } url) Status = Join(ev, url);
    }

    private bool IsJoinable(CalendarEvent ev)
    {
        var now = _clock.Now;
        var lead = TimeSpan.FromMinutes(Math.Max(0, _settings.JoinLeadMinutes));
        return !ev.IsAllDay && !ev.IsDeclined && ev.End > now && ev.Start - now <= lead;
    }

    /// <summary>
    /// Joins the meeting under way or about to start, wherever you are in the
    /// app. Returns what happened, for the status line of whichever tab is showing.
    /// </summary>
    public async Task<string> JoinNowAsync()
    {
        var now = _clock.Now;
        var target = CalendarMath.Joinable(_events, now, TimeSpan.FromMinutes(Math.Max(0, _settings.JoinLeadMinutes)));

        if (target is null)
        {
            var next = CalendarMath.Next(_events, now);
            return next is null
                ? "No meeting to join"
                : $"No meeting to join yet - next is {Title(next)} {CalendarMath.Countdown(next.Start - now)}";
        }

        var detail = await ReadDetailAsync(target).ConfigureAwait(true);
        if (detail?.JoinUrl is { } url) return Join(target, url);

        await _store.ShowEventAsync(target).ConfigureAwait(true);
        return $"{Title(target)} has no Teams, Zoom, Meet or Webex link - opened it in Outlook";
    }

    private async Task<CalendarEventDetail?> ReadDetailAsync(CalendarEvent ev)
    {
        try { return await _store.GetEventDetailAsync(ev).ConfigureAwait(true); }
        catch { return null; }
    }

    private static string Join(CalendarEvent ev, string url)
    {
        try
        {
            // MeetingLinks only returns https links to known meeting hosts.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return $"Joining {Title(ev)}...";
        }
        catch (Exception ex)
        {
            return $"Could not open the meeting link: {ex.Message}";
        }
    }
}
