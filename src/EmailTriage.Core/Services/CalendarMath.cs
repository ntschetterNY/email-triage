using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The questions the calendar features ask of a list of events: what clashes,
/// where the gaps are, what is on now and next. Pure, so it is tested without
/// Outlook.
/// </summary>
public static class CalendarMath
{
    /// <summary>
    /// Events that take up any of [start, end). <paramref name="exclude"/>
    /// leaves out a meeting's own entry - Outlook pencils an invitation in as
    /// soon as it arrives, and it would otherwise clash with itself.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> Conflicts(
        IEnumerable<CalendarEvent> events, DateTimeOffset start, DateTimeOffset end, MailRef? exclude = null) =>
        events
            .Where(e => e.BlocksTime && e.Overlaps(start, end))
            .Where(e => exclude is not { } x || e.Ref.EntryId != x.EntryId)
            .OrderBy(e => e.Start)
            .ToList();

    /// <summary>
    /// Gaps of at least <paramref name="length"/> in the working day, soonest
    /// first, starting no earlier than the next half hour. At most one per gap,
    /// so a free afternoon is one suggestion rather than eight.
    /// </summary>
    public static IReadOnlyList<TimeSlot> FreeSlots(
        IEnumerable<CalendarEvent> events, DateTimeOffset now, TimeSpan length,
        TimeSpan dayStart, TimeSpan dayEnd, int days, int max)
    {
        var busy = events.Where(e => e.BlocksTime).OrderBy(e => e.Start).ToList();
        var slots = new List<TimeSlot>();
        var earliest = RoundUp(now, TimeSpan.FromMinutes(30));

        for (var day = 0; day < days && slots.Count < max; day++)
        {
            var date = now.Date.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            var open = new DateTimeOffset(date + dayStart, now.Offset);
            var close = new DateTimeOffset(date + dayEnd, now.Offset);
            var cursor = open > earliest ? open : earliest;

            foreach (var e in busy.Where(e => e.End > cursor && e.Start < close))
            {
                if (e.Start - cursor >= length) slots.Add(new TimeSlot(cursor, cursor + length));
                if (slots.Count >= max) break;
                if (e.End > cursor) cursor = e.End;
            }

            if (slots.Count < max && close - cursor >= length)
                slots.Add(new TimeSlot(cursor, cursor + length));
        }

        return slots;
    }

    /// <summary>Meetings under way at <paramref name="now"/>, excluding all-day and declined ones.</summary>
    public static IReadOnlyList<CalendarEvent> Current(IEnumerable<CalendarEvent> events, DateTimeOffset now) =>
        events.Where(IsTimed).Where(e => e.Start <= now && e.End > now).OrderBy(e => e.End).ToList();

    /// <summary>The next meeting to start after <paramref name="now"/>, if any.</summary>
    public static CalendarEvent? Next(IEnumerable<CalendarEvent> events, DateTimeOffset now) =>
        events.Where(IsTimed).Where(e => e.Start > now).OrderBy(e => e.Start).FirstOrDefault();

    /// <summary>
    /// The meeting a "join" key should mean: one about to start within
    /// <paramref name="lead"/> - the one you are heading to beats the one
    /// you are leaving - or else the latest one under way.
    /// </summary>
    public static CalendarEvent? Joinable(IEnumerable<CalendarEvent> events, DateTimeOffset now, TimeSpan lead)
    {
        var list = events as IReadOnlyCollection<CalendarEvent> ?? events.ToList();
        var next = Next(list, now);
        if (next is not null && next.Start - now <= lead) return next;
        return Current(list, now).OrderByDescending(e => e.Start).FirstOrDefault();
    }

    /// <summary>"now", "in 1 min", "in 25 min", "in 2 h", "in 1 h 5 min".</summary>
    public static string Countdown(TimeSpan span)
    {
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        if (minutes <= 0) return "now";
        if (minutes < 60) return $"in {minutes} min";

        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"in {h} h" : $"in {h} h {m} min";
    }

    /// <summary>"Today", "Tomorrow", then "Thu 25 Sep".</summary>
    public static string DayLabel(DateTime date, DateTime today)
    {
        var days = (date.Date - today.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => date.ToString("ddd d MMM"),
        };
    }

    /// <summary>"14:00–15:00", or "All day".</summary>
    public static string TimeRange(DateTimeOffset start, DateTimeOffset end, bool allDay = false)
    {
        if (allDay) return (end.Date - start.Date).Days > 1 ? $"All day, until {end.AddDays(-1):ddd d MMM}" : "All day";
        // Ending at midnight still reads as the same day: "23:00–00:00".
        return end.Date == start.Date || (end.TimeOfDay == TimeSpan.Zero && (end - start).TotalHours <= 24)
            ? $"{start:HH:mm}–{end:HH:mm}"
            : $"{start:HH:mm} – {end:ddd d MMM HH:mm}";
    }

    private static bool IsTimed(CalendarEvent e) => !e.IsAllDay && !e.IsDeclined;

    private static DateTimeOffset RoundUp(DateTimeOffset value, TimeSpan step)
    {
        var ticks = (value.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks;
        return new DateTimeOffset(ticks, value.Offset);
    }
}
