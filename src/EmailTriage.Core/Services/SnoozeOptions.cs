namespace EmailTriage.Core.Services;

public sealed record SnoozeOption(string Label, string Hint, DateTimeOffset When);

/// <summary>Times of day the presets anchor to. Adjustable per user.</summary>
public sealed record SnoozeDayShape
{
    public TimeSpan Morning { get; init; } = new(8, 0, 0);
    public TimeSpan Afternoon { get; init; } = new(13, 0, 0);
    public TimeSpan Evening { get; init; } = new(18, 0, 0);

    /// <summary>First day of the working week, used by "next week".</summary>
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

    public static readonly SnoozeDayShape Default = new();
}

public static class SnoozePresets
{
    /// <summary>
    /// The standing list shown when the snooze palette opens, filtered to
    /// entries that are still in the future.
    /// </summary>
    public static IReadOnlyList<SnoozeOption> For(DateTimeOffset now, SnoozeDayShape? shape = null)
    {
        shape ??= SnoozeDayShape.Default;
        var today = now.Date;

        var candidates = new List<SnoozeOption>
        {
            new("In 1 hour", Fmt(now.AddHours(1)), now.AddHours(1)),
            new("In 3 hours", Fmt(now.AddHours(3)), now.AddHours(3)),
            new("This evening", Fmt(At(now, today, shape.Evening)), At(now, today, shape.Evening)),
            new("Tomorrow morning", Fmt(At(now, today.AddDays(1), shape.Morning)), At(now, today.AddDays(1), shape.Morning)),
            new("Tomorrow afternoon", Fmt(At(now, today.AddDays(1), shape.Afternoon)), At(now, today.AddDays(1), shape.Afternoon)),
            new("This weekend", Fmt(NextWeekend(now, shape)), NextWeekend(now, shape)),
            new("Next week", Fmt(NextWeek(now, shape)), NextWeek(now, shape)),
            new("In two weeks", Fmt(At(now, today.AddDays(14), shape.Morning)), At(now, today.AddDays(14), shape.Morning)),
            new("Next month", Fmt(At(now, today.AddMonths(1), shape.Morning)), At(now, today.AddMonths(1), shape.Morning)),
        };

        return candidates.Where(c => c.When > now.AddMinutes(1)).ToList();
    }

    private static DateTimeOffset At(DateTimeOffset now, DateTime day, TimeSpan time) =>
        new(day.Date + time, now.Offset);

    private static DateTimeOffset NextWeekend(DateTimeOffset now, SnoozeDayShape shape)
    {
        var d = now.Date;
        do { d = d.AddDays(1); } while (d.DayOfWeek != DayOfWeek.Saturday);
        return At(now, d, shape.Morning);
    }

    private static DateTimeOffset NextWeek(DateTimeOffset now, SnoozeDayShape shape)
    {
        var d = now.Date;
        do { d = d.AddDays(1); } while (d.DayOfWeek != shape.WeekStart);
        return At(now, d, shape.Morning);
    }

    private static string Fmt(DateTimeOffset when)
    {
        var now = DateTimeOffset.Now;
        var days = (when.Date - now.Date).Days;
        return days switch
        {
            0 => when.ToString("h:mm tt").ToLowerInvariant(),
            1 => "tomorrow " + when.ToString("h:mm tt").ToLowerInvariant(),
            < 7 => when.ToString("ddd h:mm tt").ToLowerInvariant(),
            _ => when.ToString("ddd d MMM, h:mm tt").ToLowerInvariant(),
        };
    }
}
