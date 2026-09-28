using System.Text;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>What counts as time the user can offer to meet.</summary>
public sealed record AvailabilityRules
{
    public TimeSpan DayStart { get; init; } = TimeSpan.FromHours(9);
    public TimeSpan DayEnd { get; init; } = TimeSpan.FromHours(17);

    /// <summary>Kept clear either side of an existing meeting.</summary>
    public TimeSpan Buffer { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gaps shorter than this are not worth offering.</summary>
    public TimeSpan MinWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How many working days ahead to look, starting with the next one.</summary>
    public int WorkingDays { get; init; } = 10;

    /// <summary>
    /// Lunch, kept free where possible: its free time is listed apart, for
    /// when nothing else fits. Null, or an empty range, treats it like any
    /// other time.
    /// </summary>
    public TimeSpan? LunchStart { get; init; }
    public TimeSpan? LunchEnd { get; init; }

    /// <summary>Times the draft should offer, when the email is about meeting.</summary>
    public int SlotCount { get; init; } = 3;

    public bool HasLunch => LunchStart is { } s && LunchEnd is { } e && e > s;
}

/// <summary>One working day's free time.</summary>
/// <param name="Windows">Free time outside lunch, soonest first.</param>
/// <param name="Lunch">Free time over lunch, offered only when nothing else fits.</param>
/// <param name="IsAway">An all-day out-of-office or busy entry takes the whole day.</param>
public sealed record DayAvailability(
    DateTime Date, IReadOnlyList<TimeSlot> Windows, IReadOnlyList<TimeSlot> Lunch, bool IsAway = false)
{
    public bool IsFullyBooked => Windows.Count == 0 && Lunch.Count == 0;
}

/// <summary>
/// Works out when the user is free to meet, from their own calendar, and puts
/// it into words for the draft prompt. Pure, so it is tested without Outlook.
/// </summary>
public static class Availability
{
    /// <summary>
    /// Free windows in working hours on the next <see cref="AvailabilityRules.WorkingDays"/>
    /// weekdays, starting tomorrow - never later today. Busy, tentative and
    /// out-of-office time all count as taken, padded by the buffer; free,
    /// declined and "working elsewhere" entries do not.
    /// </summary>
    /// <param name="zone">The user's time zone, for days that change clocks. Null keeps <paramref name="now"/>'s offset.</param>
    public static IReadOnlyList<DayAvailability> FreeWindows(
        IEnumerable<CalendarEvent> events, DateTimeOffset now, AvailabilityRules rules, TimeZoneInfo? zone = null)
    {
        DateTime Local(DateTimeOffset t) => zone is null ? t.ToOffset(now.Offset).DateTime : TimeZoneInfo.ConvertTime(t, zone).DateTime;
        DateTimeOffset Offset(DateTime t) => new(t, zone is null ? now.Offset : zone.GetUtcOffset(t));
        TimeSlot Slot((DateTime Start, DateTime End) s) => new(Offset(s.Start), Offset(s.End));

        var list = events.ToList();

        var taken = list
            .Where(e => e.BlocksTime && e.Busy != BusyStatus.WorkingElsewhere)
            .Select(e => (Start: Local(e.Start) - rules.Buffer, End: Local(e.End) + rules.Buffer))
            .OrderBy(e => e.Start)
            .ToList();

        // Leave, or a day blocked out whole, is not a day to offer.
        var awayDays = list
            .Where(e => e.IsAllDay && !e.IsDeclined && e.Busy is BusyStatus.OutOfOffice or BusyStatus.Busy)
            .ToList();

        var today = Local(now).Date;
        var days = new List<DayAvailability>();

        for (var date = today.AddDays(1); days.Count < rules.WorkingDays; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            if (awayDays.Any(e => e.Start.Date <= date && e.End.DateTime > date))
            {
                days.Add(new DayAvailability(date, Array.Empty<TimeSlot>(), Array.Empty<TimeSlot>(), IsAway: true));
                continue;
            }

            var free = Subtract(new[] { (date + rules.DayStart, date + rules.DayEnd) }, taken);

            var lunch = new List<(DateTime Start, DateTime End)>();
            if (rules.HasLunch)
            {
                var lunchStart = date + rules.LunchStart!.Value;
                var lunchEnd = date + rules.LunchEnd!.Value;
                lunch = free
                    .Select(f => (Start: Max(f.Start, lunchStart), End: Min(f.End, lunchEnd)))
                    .Where(f => f.End > f.Start)
                    .ToList();
                free = Subtract(free, new[] { (lunchStart, lunchEnd) });
            }

            var windows = free.Where(f => f.End - f.Start >= rules.MinWindow).ToList();

            // A short stretch of lunch is still worth listing when it extends
            // a window next to it; alone, it has to be long enough to meet in.
            var lunchFree = lunch
                .Where(l => l.End - l.Start >= rules.MinWindow || windows.Any(w => w.End == l.Start || w.Start == l.End))
                .ToList();

            days.Add(new DayAvailability(date, windows.Select(Slot).ToList(), lunchFree.Select(Slot).ToList()));
        }

        return days;
    }

    /// <summary>
    /// The "your availability" section of the draft prompt. It tells Claude to
    /// offer times only when the email is about meeting, and only from these
    /// windows.
    /// </summary>
    /// <param name="zoneName">"Eastern Daylight Time (UTC-04:00)", say.</param>
    public static string Describe(
        IReadOnlyList<DayAvailability> days, DateTimeOffset now, AvailabilityRules rules, string zoneName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The user's calendar availability, in {zoneName}. It is now {now:dddd d MMMM yyyy, HH:mm}.");

        var through = days.Count > 0 ? $" through {days[^1].Date:ddd d MMM}" : "";
        sb.AppendLine($"Free windows in their working hours ({Clock(rules.DayStart)}-{Clock(rules.DayEnd)}), " +
                      $"after existing meetings plus a {(int)rules.Buffer.TotalMinutes}-minute buffer, " +
                      $"from the next working day{through}:");

        foreach (var day in days)
        {
            var line = day switch
            {
                { IsAway: true } => "away (out of office)",
                { Windows.Count: 0, Lunch.Count: 0 } => "fully booked",
                { Windows.Count: 0 } => $"only over lunch: {Spans(day.Lunch)}",
                _ => Spans(day.Windows) + (day.Lunch.Count > 0 ? $" (lunch free too: {Spans(day.Lunch)})" : ""),
            };
            sb.AppendLine($"  {day.Date:ddd d MMM}: {line}");
        }

        sb.AppendLine();
        sb.AppendLine($"If the email involves arranging a meeting or call, offer {Math.Max(1, rules.SlotCount)} specific times " +
                      "the user is free: each fully inside one window above, on different days where possible, and as long as " +
                      "the conversation or the notes call for (30 minutes when nothing says).");
        if (rules.HasLunch)
            sb.AppendLine($"The user keeps lunch ({Clock(rules.LunchStart!.Value)}-{Clock(rules.LunchEnd!.Value)}) free: " +
                          "only offer a time that runs into lunch when nothing else fits.");
        sb.AppendLine("Write each one with its day, date and time zone, e.g. \"Tue 29 Sep, 2:00-3:00pm ET\". " +
                      "If the other side already proposed times, say which of those fit instead of offering new ones. " +
                      "Never offer a time outside these windows. If the email is not about arranging a meeting, ignore this section.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>The prompt's stand-in when the calendar could not be read: no guessing at times.</summary>
    public const string Unreadable =
        "The user's calendar could not be read just now. If the email involves arranging a meeting, " +
        "do not propose specific times; ask the other side which times suit them instead.";

    /// <summary>"07:00", and "24:00" for the end of the day rather than "00:00".</summary>
    private static string Clock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    private static string Spans(IEnumerable<TimeSlot> slots) =>
        string.Join(", ", slots.Select(s => $"{s.Start:HH:mm}-{s.End:HH:mm}"));

    /// <summary>What is left of <paramref name="spans"/> after taking out <paramref name="cuts"/>.</summary>
    private static List<(DateTime Start, DateTime End)> Subtract(
        IEnumerable<(DateTime Start, DateTime End)> spans, IEnumerable<(DateTime Start, DateTime End)> cuts)
    {
        var result = spans.ToList();
        foreach (var cut in cuts)
        {
            var next = new List<(DateTime Start, DateTime End)>();
            foreach (var span in result)
            {
                if (cut.End <= span.Start || cut.Start >= span.End)
                {
                    next.Add(span);
                    continue;
                }
                if (cut.Start > span.Start) next.Add((span.Start, cut.Start));
                if (cut.End < span.End) next.Add((cut.End, span.End));
            }
            result = next;
        }
        return result;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
