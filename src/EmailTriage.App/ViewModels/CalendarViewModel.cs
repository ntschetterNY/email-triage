using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>How close the next meeting is, for colouring the strip in the top bar.</summary>
public enum StripState { Clear, Upcoming, Soon, Now }

/// <summary>One meeting in the top bar.</summary>
public sealed partial class MeetingPill : ObservableObject
{
    public MeetingPill(CalendarEvent? ev, string text, StripState state)
    {
        Event = ev;
        _text = text;
        _state = state;
    }

    /// <summary>Null for the "No more meetings today" pill.</summary>
    public CalendarEvent? Event { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    private string _text;

    [ObservableProperty] private StripState _state;

    /// <summary>The tooltip: the whole line, in case it was cut short, and what a click does.</summary>
    public string Hint => $"{Text}\nClick for the details and a Join button";

    public void Update(string text, StripState state)
    {
        Text = text;
        State = state;
    }
}

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

/// <summary>A meeting shown in the day grid or a month cell; carries its agenda row for the detail pane.</summary>
public partial class CalendarItem : ObservableObject
{
    public CalendarItem(AgendaRow row) => Row = row;

    public AgendaRow Row { get; }
    public CalendarEvent Event => Row.Event;

    /// <summary>"10:00 OAC meeting" in a month cell; the subject alone for an all-day entry.</summary>
    public string Text => Event.IsAllDay ? Row.Subject : $"{Event.Start:HH:mm} {Row.Subject}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A timed meeting placed in a day's column of the time grid.</summary>
public sealed class CalendarBlock : CalendarItem
{
    public CalendarBlock(AgendaRow row, PlacedEvent placed, double hourHeight) : base(row)
    {
        Top = placed.Start.TotalHours * hourHeight;
        Height = Math.Max(MinHeight, (placed.End - placed.Start).TotalHours * hourHeight);
        Lane = placed.Lane;
        Lanes = placed.Lanes;
    }

    /// <summary>Short meetings still get a line of text.</summary>
    public const double MinHeight = 18;

    public double Top { get; }
    public double Height { get; }
    public int Lane { get; }
    public int Lanes { get; }

    /// <summary>"10:00–11:30 · Site trailer" under the subject.</summary>
    public string Caption => CalendarMath.TimeRange(Event.Start, Event.End) + (Row.HasLocation ? $" · {Row.Location}" : "");

    /// <summary>Time and place under the subject, when the block is tall enough to show them.</summary>
    public bool IsRoomy => Height >= 34;

    public bool IsTentative => Event.IsHold;
}

/// <summary>One day's column in the Day, Work week and Week views.</summary>
public sealed partial class CalendarDayColumn : ObservableObject
{
    public required DateTime Date { get; init; }
    public required IReadOnlyList<CalendarBlock> Blocks { get; init; }
    public required IReadOnlyList<CalendarItem> AllDay { get; init; }

    /// <summary>The shaded stretches before and after working hours, in pixels.</summary>
    public double OffBefore { get; init; }
    public double OffAfter { get; init; }

    public bool IsToday { get; init; }
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    public string Header => $"{Date:ddd d}";

    /// <summary>The red "now" line, on today's column only.</summary>
    [ObservableProperty] private double _nowTop;
}

/// <summary>One day in the Month view.</summary>
public sealed class MonthCell
{
    /// <summary>Meetings listed in a cell before the rest fold into "+N more".</summary>
    public const int Shown = 3;

    public required DateTime Date { get; init; }
    public required IReadOnlyList<CalendarItem> Entries { get; init; }
    public int Hidden { get; init; }
    public bool IsToday { get; init; }
    public bool IsOtherMonth { get; init; }

    /// <summary>"28", or "1 Oct" on the first of a month.</summary>
    public string DayText => Date.Day == 1 ? $"{Date:d MMM}" : Date.Day.ToString();
    public string More => Hidden > 0 ? $"+{Hidden} more" : "";
}

/// <summary>
/// The Calendar tab and the "next meeting" strip in the top bar. Reads the
/// Outlook calendar a couple of weeks ahead for the strip and the agenda, and
/// whatever range the day, week or month view is showing; rereads both every
/// few minutes and after anything the app itself changes, and ticks the
/// strip's countdown from what it has in hand.
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

    /// <summary>The columns of the Day, Work week or Week view.</summary>
    public ObservableCollection<CalendarDayColumn> Days { get; } = new();

    /// <summary>Six weeks of days for the Month view.</summary>
    public ObservableCollection<MonthCell> MonthCells { get; } = new();

    /// <summary>Height of an hour in the time grid, in pixels.</summary>
    public const double HourHeight = 44;

    public double GridHeight => 24 * HourHeight;

    /// <summary>Where the grid scrolls to on opening: half an hour before the working day starts.</summary>
    public double WorkdayTop => Math.Max(0, (Math.Clamp(_settings.WorkdayStartHour, 0, 23) - 0.5) * HourHeight);

    public static IReadOnlyList<string> HourLabels { get; } =
        Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();

    public static IReadOnlyList<string> WeekdayNames { get; } =
        new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeGrid), nameof(IsMonth), nameof(IsAgenda), nameof(RangeTitle))]
    private CalendarView _view;

    /// <summary>The day the view is built around; today until you move.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeTitle))]
    private DateTime _anchor;

    public bool IsTimeGrid => View is CalendarView.Day or CalendarView.WorkWeek or CalendarView.Week;
    public bool IsMonth => View == CalendarView.Month;
    public bool IsAgenda => View == CalendarView.Agenda;

    /// <summary>"28 Sep – 2 Oct 2026" above the grid.</summary>
    public string RangeTitle => CalendarLayout.Title(View, Anchor);

    /// <summary>Raised when the view or its range changes, so the window can scroll to the working day.</summary>
    public event EventHandler? RangeChanged;

    // What the day, week or month view is showing, and the meetings in it in order, for j and k.
    private IReadOnlyList<CalendarEvent> _rangeEvents = Array.Empty<CalendarEvent>();
    private (DateTime First, int Days) _range;
    private List<AgendaRow> _visibleRows = new();
    private List<CalendarItem> _items = new();
    private string _built = "";

    // Set when the user moves to a range that is still being read: once it
    // arrives, a meeting in it is picked, as it would be from the cache.
    private bool _reselectPending;

    [ObservableProperty] private AgendaRow? _selected;
    [ObservableProperty] private CalendarEventDetail? _detail;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>The top bar's meetings, one pill each; clicking one opens its card.</summary>
    public ObservableCollection<MeetingPill> Pills { get; } = new();

    /// <summary>Room for a pill's text: a lone pill can say more.</summary>
    [ObservableProperty] private double _pillMaxWidth = 560;

    public CalendarViewModel(ICalendarStore store, IClock clock, AppSettings settings)
    {
        _store = store;
        _clock = clock;
        _settings = settings;

        _view = RememberedView(settings);
        _anchor = clock.Now.Date;

        _tick.Tick += async (_, _) =>
        {
            if (_clock.Now - _loadedAt >= ReloadEvery) await RefreshQuietlyAsync().ConfigureAwait(true);
            else UpdateStrip();
            UpdateNowLine();
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
                OnPropertyChanged(nameof(AgendaSelected));

                // Land where the user was, else on what is on now or next. In
                // the grid or month the selection may be weeks away from the
                // agenda's fortnight, so it is left alone there.
                if (IsAgenda || Selected is null)
                    Selected = Rows.FirstOrDefault(r => r.Event.Key == keep)
                               ?? Rows.FirstOrDefault(r => !r.IsPast && !r.Event.IsAllDay)
                               ?? Rows.FirstOrDefault(r => !r.IsPast)
                               ?? Rows.LastOrDefault();
            }

            UpdateStrip();
            var rangeRead = await LoadRangeAsync(now, from, to).ConfigureAwait(true);

            // A range that could not be read has already said so.
            if (!quiet && rangeRead)
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

    // ---- day, week and month views ---------------------------------------------

    /// <summary>
    /// Reads what the day, week or month view shows. A range inside the
    /// fortnight already read for the agenda costs no second trip to Outlook.
    /// False when Outlook would not give it up, after saying so in the status line.
    /// </summary>
    private async Task<bool> LoadRangeAsync(DateTimeOffset now, DateTimeOffset agendaFrom, DateTimeOffset agendaTo)
    {
        if (IsAgenda) return true;

        var range = CalendarLayout.Range(View, Anchor);
        var from = new DateTimeOffset(range.First, now.Offset);
        var to = new DateTimeOffset(range.First.AddDays(range.Days), now.Offset);

        IReadOnlyList<CalendarEvent> events;
        if (from >= agendaFrom && to <= agendaTo)
        {
            events = _events.Where(e => e.Overlaps(from, to)).ToList();
        }
        else
        {
            IsLoading = true;
            try
            {
                events = await _store.GetEventsAsync(from, to).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Said out loud, whoever asked: a stalled Outlook must not pass for a free week.
                Status = $"{RangeFailed} {CalendarLayout.Title(View, Anchor)}: {ex.Message}";
                return false;
            }
            finally
            {
                IsLoading = false;
            }
        }

        // The user may have moved on while Outlook answered; the next load covers where they are now.
        if (range != CalendarLayout.Range(View, Anchor) || IsAgenda) return true;

        _rangeEvents = events;
        _range = range;
        BuildRange(now, reselect: _reselectPending);
        _reselectPending = false;

        if (Status.StartsWith(RangeFailed, StringComparison.Ordinal)) Status = "";
        return true;
    }

    private const string RangeFailed = "Could not read your calendar for";

    /// <summary>
    /// Lays out the grid or the month from what is in hand. Rebuilt only when
    /// something on screen would change, so the three-minute reread does not
    /// flicker. <paramref name="reselect"/> picks a meeting in the new range
    /// when the one selected is not in it.
    /// </summary>
    private void BuildRange(DateTimeOffset now, bool reselect)
    {
        var (first, count) = _range;
        var shown = $"{View}|{first:yyyyMMdd}|{count}|{now:yyyyMMddHH}|" + string.Join('\n', _rangeEvents.Select(e =>
            $"{e.Key}|{e.Subject}|{e.Location}|{e.End.UtcTicks}|{e.Response}|{e.Busy}"));

        if (shown != _built)
        {
            _built = shown;
            _items = new List<CalendarItem>();

            Days.Clear();
            MonthCells.Clear();

            var rows = _rangeEvents
                .GroupBy(e => e.Key)
                .ToDictionary(g => g.Key, g => new AgendaRow(g.First(), now));
            CalendarItem Item(CalendarEvent e)
            {
                var item = new CalendarItem(rows[e.Key]);
                _items.Add(item);
                return item;
            }

            if (View == CalendarView.Month)
            {
                for (var i = 0; i < count; i++)
                {
                    var date = first.AddDays(i);
                    var onDay = CalendarLayout.OnDay(_rangeEvents, date);
                    MonthCells.Add(new MonthCell
                    {
                        Date = date,
                        Entries = onDay.Take(MonthCell.Shown).Select(Item).ToList(),
                        Hidden = Math.Max(0, onDay.Count - MonthCell.Shown),
                        IsToday = date == now.Date,
                        IsOtherMonth = date.Month != Anchor.Month,
                    });
                }
            }
            else
            {
                var workStart = Math.Clamp(_settings.WorkdayStartHour, 0, 23);
                var workEnd = Math.Clamp(_settings.WorkdayEndHour, workStart + 1, 24);

                for (var i = 0; i < count; i++)
                {
                    var date = first.AddDays(i);
                    var blocks = CalendarLayout.Lanes(_rangeEvents, date, TimeSpan.FromHours(CalendarBlock.MinHeight / HourHeight))
                        .Select(p => new CalendarBlock(rows[p.Event.Key], p, HourHeight))
                        .ToList();
                    _items.AddRange(blocks);

                    Days.Add(new CalendarDayColumn
                    {
                        Date = date,
                        Blocks = blocks,
                        AllDay = CalendarLayout.AllDayOn(_rangeEvents, date).Select(Item).ToList(),
                        IsToday = date == now.Date,
                        OffBefore = workStart * HourHeight,
                        OffAfter = (24 - workEnd) * HourHeight,
                    });
                }
                OnPropertyChanged(nameof(DayCount));
            }

            // j and k walk what is on screen in time order; a meeting that
            // spans days is visited once.
            _visibleRows = rows.Values
                .OrderBy(r => r.Event.Start).ThenByDescending(r => r.Event.IsAllDay)
                .ToList();

            UpdateNowLine();
        }

        var key = Selected?.Event.Key;
        if (reselect && (key is null || _visibleRows.All(r => r.Event.Key != key)))
        {
            var timed = _visibleRows.Where(r => !r.Event.IsAllDay).ToList();
            Selected = timed.FirstOrDefault(r => r.Event.End > now) ?? timed.FirstOrDefault() ?? _visibleRows.FirstOrDefault();
        }
        else SyncSelection();
    }

    /// <summary>How many columns the time grid has: 1, 5 or 7.</summary>
    public int DayCount => Math.Max(1, Days.Count);

    /// <summary>1-5: switch view, keeping the day you are on. Remembered for next time.</summary>
    public void SetView(CalendarView view)
    {
        if (view == View) return;

        // Coming back from the agenda, land on the meeting that was picked there.
        if (IsAgenda && Selected is { } row) Anchor = row.Event.Start.Date;

        View = view;
        Remember(view);
        ShowRange(reselect: false);
    }

    /// <summary>Left and Right: the previous or next day, week or month.</summary>
    public void Step(int delta)
    {
        if (IsAgenda) return;
        Anchor = CalendarLayout.Step(View, Anchor, delta);
        ShowRange(reselect: true);
    }

    /// <summary>Home, or the Today button.</summary>
    public void GoToToday()
    {
        if (IsAgenda)
        {
            MoveToEnd(false);
            return;
        }
        Anchor = _clock.Now.Date;
        ShowRange(reselect: true);
    }

    /// <summary>A click on a month day, or Enter in the month: that day in the Day view.</summary>
    public void ShowDay(DateTime date)
    {
        Anchor = date.Date;
        View = CalendarView.Day;
        ShowRange(reselect: true);
    }

    /// <summary>Enter in the month view opens the selected meeting's day.</summary>
    public void OpenSelectedDay() => ShowDay(Selected?.Event.Start.Date ?? Anchor);

    /// <summary>
    /// Shows the new range straight away from what is in hand, then reads it
    /// from Outlook. A range outside what is cached shows empty until then.
    /// </summary>
    private void ShowRange(bool reselect)
    {
        _reselectPending = reselect;
        if (!IsAgenda)
        {
            var now = _clock.Now;
            _range = CalendarLayout.Range(View, Anchor);
            var from = new DateTimeOffset(_range.First, now.Offset);
            var to = from.AddDays(_range.Days);
            _rangeEvents = _events.Where(e => e.Overlaps(from, to)).ToList();
            BuildRange(now, reselect);
        }
        else SyncSelection();

        RangeChanged?.Invoke(this, EventArgs.Empty);
        _ = LoadCoreAsync(quiet: true);
    }

    /// <summary>Where the selected meeting sits in the grid, for scrolling it into view.</summary>
    public CalendarBlock? SelectedBlock =>
        Selected is { } row ? _items.OfType<CalendarBlock>().FirstOrDefault(b => b.Event.Key == row.Event.Key) : null;

    private void SyncSelection()
    {
        var key = Selected?.Event.Key;
        foreach (var item in _items) item.IsSelected = item.Event.Key == key;
    }

    private void UpdateNowLine()
    {
        var now = _clock.Now;
        foreach (var day in Days)
            if (day.IsToday) day.NowTop = (now.DateTime - now.Date).TotalHours * HourHeight;
    }

    // The last view picked, kept beside settings.json so it survives a restart.
    private static string ViewFile => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "calendar-view.txt");

    private static CalendarView RememberedView(AppSettings settings)
    {
        try
        {
            if (File.Exists(ViewFile) && Enum.TryParse<CalendarView>(File.ReadAllText(ViewFile).Trim(), true, out var last))
                return last;
        }
        catch { /* fall back to the setting */ }

        return Enum.TryParse<CalendarView>(settings.CalendarView, true, out var view) ? view : CalendarView.WorkWeek;
    }

    private static void Remember(CalendarView view)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ViewFile)!);
            File.WriteAllText(ViewFile, view.ToString());
        }
        catch { /* a view not remembered is no reason to fail */ }
    }

    // ---- the strip ----------------------------------------------------------

    /// <summary>A meeting starting within this long gets a pill of its own beside the one on now.</summary>
    private static readonly TimeSpan PillWindow = TimeSpan.FromMinutes(60);

    private const int MaxPills = 4;

    /// <summary>
    /// One pill per meeting on now or coming up shortly: "Now · Standup · ends
    /// in 12 min", "Design review in 25 min · 14:00". The next meeting alone
    /// when none is that close, or a quiet "No more meetings today".
    /// </summary>
    private void UpdateStrip()
    {
        var now = _clock.Now;
        var today = _events.Where(e => e.Start.Date == now.Date || e.Overlaps(now, now.AddMinutes(1)));
        var upcoming = CalendarMath.Upcoming(today, now, PillWindow, MaxPills);

        var pills = upcoming.Select((e, i) => PillFor(e, now, first: i == 0)).ToList();
        if (pills.Count == 0 && _loadedAt != DateTimeOffset.MinValue)
            pills.Add(new MeetingPill(null, "No more meetings today", StripState.Clear));

        // Update in place when the same meetings are showing, so the tick
        // does not rebuild the pills (and drop a hover) every 20 seconds.
        if (pills.Select(p => p.Event?.Key).SequenceEqual(Pills.Select(p => p.Event?.Key)))
        {
            for (var i = 0; i < pills.Count; i++) Pills[i].Update(pills[i].Text, pills[i].State);
        }
        else
        {
            Pills.Clear();
            foreach (var pill in pills) Pills.Add(pill);
        }

        PillMaxWidth = Pills.Count <= 1 ? 560 : 260;
    }

    private static MeetingPill PillFor(CalendarEvent e, DateTimeOffset now, bool first)
    {
        if (e.Start <= now)
            return new MeetingPill(e, $"Now · {Title(e)} · ends {CalendarMath.Countdown(e.End - now)}", StripState.Now);

        var soon = e.Start - now <= TimeSpan.FromMinutes(5);
        var text = $"{Title(e)} {CalendarMath.Countdown(e.Start - now)} · {e.Start:HH:mm}{Where(e)}";
        return new MeetingPill(e, soon || !first ? text : $"Next · {text}", soon ? StripState.Soon : StripState.Upcoming);
    }

    private static string Title(CalendarEvent e) =>
        string.IsNullOrWhiteSpace(e.Subject) ? "(no subject)" : e.Subject.Trim();

    private static string Where(CalendarEvent e) =>
        e.Location.Length == 0 || e.Location.Length > 40 ? "" : $" · {e.Location}";

    // ---- the agenda -----------------------------------------------------------

    /// <summary>What j and k walk: the agenda, or the meetings the grid or month is showing.</summary>
    private IReadOnlyList<AgendaRow> Walkable => IsAgenda ? Rows : _visibleRows;

    public void Move(int delta)
    {
        var rows = Walkable;
        if (rows.Count == 0) return;

        var key = Selected?.Event.Key;
        var at = key is null ? -1 : rows.ToList().FindIndex(r => r.Event.Key == key);
        var index = at < 0 ? 0 : at + delta;
        Selected = rows[Math.Clamp(index, 0, rows.Count - 1)];
    }

    public void MoveToEnd(bool last) => Selected = last ? Walkable.LastOrDefault() : Walkable.FirstOrDefault();

    /// <summary>
    /// Shows a meeting - from the strip, or after scheduling one. The grid
    /// and month move to the meeting's day if it is off screen.
    /// </summary>
    public void Select(CalendarEvent ev)
    {
        if (IsAgenda)
        {
            Selected = Rows.FirstOrDefault(r => r.Event.Key == ev.Key) ?? Selected;
            return;
        }

        // Anything that shows in the range counts as on screen - an all-day
        // entry that began last week, a meeting that ran past midnight.
        var (first, days) = CalendarLayout.Range(View, Anchor);
        var offset = _clock.Now.Offset;
        if (!ev.Overlaps(new DateTimeOffset(first, offset), new DateTimeOffset(first.AddDays(days), offset)))
        {
            Anchor = ev.Start.Date;
            Selected = new AgendaRow(ev, _clock.Now);
            ShowRange(reselect: false);
            return;
        }

        Selected = _visibleRows.FirstOrDefault(r => r.Event.Key == ev.Key)
                   ?? Rows.FirstOrDefault(r => r.Event.Key == ev.Key)
                   ?? new AgendaRow(ev, _clock.Now);
    }

    /// <summary>
    /// The agenda list's selection: the selected meeting, when the list has
    /// it. The list pushes back null when it loses the item (on a rebuild, or
    /// for a meeting picked weeks away in the month), which never clears the
    /// selection itself.
    /// </summary>
    public AgendaRow? AgendaSelected
    {
        get => Selected is { } row ? Rows.FirstOrDefault(r => r.Event.Key == row.Event.Key) : null;
        set
        {
            if (value is not null && !ReferenceEquals(value, Selected)) Selected = value;
        }
    }

    partial void OnSelectedChanged(AgendaRow? value)
    {
        SyncSelection();
        OnPropertyChanged(nameof(AgendaSelected));
        _ = LoadDetailAsync(value);
    }

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
        Status = await OpenInOutlookAsync(row.Event).ConfigureAwait(true);
    }

    /// <summary>Opens a meeting in Outlook; returns what happened, for the status line.</summary>
    public async Task<string> OpenInOutlookAsync(CalendarEvent ev)
    {
        try
        {
            await _store.ShowEventAsync(ev).ConfigureAwait(true);
            return $"Opened in Outlook: {Title(ev)}";
        }
        catch (Exception ex)
        {
            return $"Could not open it in Outlook: {ex.Message}";
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
    /// A click on a meeting, in the agenda or the grid: shows it here and,
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
    /// The card a pill opens: the meeting's details, read in the background,
    /// with its join link. Nothing is joined until the card's Join is pressed.
    /// </summary>
    public MeetingCardViewModel OpenCard(CalendarEvent ev)
    {
        var card = new MeetingCardViewModel(new AgendaRow(ev, _clock.Now), _clock.Now);
        _ = LoadCardAsync(card);
        return card;
    }

    private async Task LoadCardAsync(MeetingCardViewModel card)
    {
        card.Detail = await ReadDetailAsync(card.Row.Event).ConfigureAwait(true);
        card.IsLoading = false;
    }

    /// <summary>Join from a meeting's card; returns what happened, for the status line.</summary>
    public static string JoinFromCard(MeetingCardViewModel card) =>
        card.Detail?.JoinUrl is { } url ? Join(card.Row.Event, url) : "That meeting has no link to join";

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
