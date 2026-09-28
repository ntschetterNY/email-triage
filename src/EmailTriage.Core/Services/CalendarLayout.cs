using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>The ways the Calendar tab can show your calendar, on keys 1-5.</summary>
public enum CalendarView { Day, WorkWeek, Week, Month, Agenda }

/// <summary>A timed event placed in one day's column: where it sits and which lane it shares.</summary>
/// <param name="Start">From the start of the day, clipped to it.</param>
/// <param name="End">From the start of the day, clipped to it (at most 24 h).</param>
/// <param name="Lane">Which of <paramref name="Lanes"/> side-by-side slots it takes.</param>
/// <param name="Lanes">How many events overlap in its cluster, so how wide each lane is.</param>
public sealed record PlacedEvent(CalendarEvent Event, TimeSpan Start, TimeSpan End, int Lane, int Lanes);

/// <summary>
/// The layout maths behind the day, week and month views: which days a view
/// covers, how it steps, and how overlapping meetings share a column. Pure, so
/// it is tested without WPF or Outlook. Weeks start on Monday.
/// </summary>
public static class CalendarLayout
{
    /// <summary>
    /// The first day a view shows and how many days it covers. A month is six
    /// whole weeks from the Monday on or before the 1st, so every month has
    /// the same shape.
    /// </summary>
    public static (DateTime First, int Days) Range(CalendarView view, DateTime anchor)
    {
        var day = anchor.Date;
        return view switch
        {
            // Seen from the weekend, the work week is the one coming up, not the one just gone.
            CalendarView.WorkWeek => (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? Monday(day).AddDays(7)
                : Monday(day), 5),
            CalendarView.Week => (Monday(day), 7),
            CalendarView.Month => (Monday(new DateTime(day.Year, day.Month, 1)), 42),
            _ => (day, 1),
        };
    }

    /// <summary>The anchor one day, week or month on (or back, for a negative step).</summary>
    public static DateTime Step(CalendarView view, DateTime anchor, int delta) => view switch
    {
        CalendarView.WorkWeek or CalendarView.Week => anchor.Date.AddDays(7 * delta),
        CalendarView.Month => anchor.Date.AddMonths(delta),
        _ => anchor.Date.AddDays(delta),
    };

    /// <summary>"Monday 28 September 2026", "28 Sep – 2 Oct 2026", "September 2026".</summary>
    public static string Title(CalendarView view, DateTime anchor)
    {
        var (first, days) = Range(view, anchor);
        var last = first.AddDays(days - 1);

        return view switch
        {
            CalendarView.Day => $"{first:dddd d MMMM yyyy}",
            CalendarView.Month => $"{anchor:MMMM yyyy}",
            CalendarView.Agenda => "Today and the days ahead",
            _ when first.Year != last.Year => $"{first:d MMM yyyy} – {last:d MMM yyyy}",
            _ when first.Month != last.Month => $"{first:d MMM} – {last:d MMM yyyy}",
            _ => $"{first.Day}–{last:d MMM yyyy}",
        };
    }

    public static DateTime Monday(DateTime date)
    {
        var back = ((int)date.DayOfWeek + 6) % 7; // Monday 0 ... Sunday 6
        return date.Date.AddDays(-back);
    }

    /// <summary>
    /// The timed events on <paramref name="day"/>, placed side by side where
    /// they overlap. Events that chain into each other form one cluster and
    /// share its lane count, so a column never has blocks of mixed widths
    /// that overlap. All-day events are left to <see cref="AllDayOn"/>.
    /// <paramref name="minLength"/> is how long the shortest block looks on
    /// screen, so a 15-minute meeting drawn taller than that never runs over
    /// the one after it.
    /// </summary>
    public static IReadOnlyList<PlacedEvent> Lanes(
        IEnumerable<CalendarEvent> events, DateTime day, TimeSpan minLength = default)
    {
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);

        var items = events
            .Where(e => !e.IsAllDay && e.Start.DateTime < dayEnd && e.End.DateTime > dayStart)
            .Select(e => (Event: e,
                          Start: Max(e.Start.DateTime, dayStart) - dayStart,
                          End: Min(e.End.DateTime, dayEnd) - dayStart))
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End)
            .ToList();

        var placed = new List<PlacedEvent>();
        var cluster = new List<(CalendarEvent Event, TimeSpan Start, TimeSpan End, int Lane)>();
        var laneEnds = new List<TimeSpan>();
        var clusterEnd = TimeSpan.MinValue;

        void Flush()
        {
            placed.AddRange(cluster.Select(c => new PlacedEvent(c.Event, c.Start, c.End, c.Lane, laneEnds.Count)));
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var item in items)
        {
            // Zero-length and short events still take up the room they are drawn in.
            var shortest = minLength > TimeSpan.Zero ? minLength : TimeSpan.FromMinutes(1);
            var end = Max(item.End, item.Start + shortest);

            if (cluster.Count > 0 && item.Start >= clusterEnd) Flush();

            var lane = laneEnds.FindIndex(e => e <= item.Start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            else laneEnds[lane] = end;

            cluster.Add((item.Event, item.Start, item.End, lane));
            clusterEnd = cluster.Count == 1 ? end : Max(clusterEnd, end);
        }

        Flush();
        return placed;
    }

    /// <summary>All-day events that include <paramref name="day"/> (their end date is exclusive, as in Outlook).</summary>
    public static IReadOnlyList<CalendarEvent> AllDayOn(IEnumerable<CalendarEvent> events, DateTime day) =>
        events.Where(e => e.IsAllDay && e.Start.Date <= day.Date && e.End.DateTime > day.Date)
              .OrderBy(e => e.Subject, StringComparer.CurrentCultureIgnoreCase)
              .ToList();

    /// <summary>Everything on <paramref name="day"/> for a month cell: all-day first, then by start.</summary>
    public static IReadOnlyList<CalendarEvent> OnDay(IEnumerable<CalendarEvent> events, DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        return events
            .Where(e => e.IsAllDay
                ? e.Start.Date <= start && e.End.DateTime > start
                : e.Start.DateTime < end && e.End.DateTime > start)
            .OrderByDescending(e => e.IsAllDay)
            .ThenBy(e => e.Start)
            .ToList();
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
