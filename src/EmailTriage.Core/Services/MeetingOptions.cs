using System.Globalization;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The switches on a meeting being set up from a mail: Teams, all day,
/// repeating, and how it shows on the calendar. Each one is flipped or
/// cycled from the keyboard, so the order of the cycles is the order they
/// are most often wanted in.
/// </summary>
public sealed record MeetingOptions
{
    public bool Teams { get; init; }
    public bool AllDay { get; init; }
    public Repeat Repeat { get; init; } = Repeat.Once;
    public BusyStatus ShowAs { get; init; } = BusyStatus.Busy;

    private static readonly Repeat[] RepeatCycle =
        { Repeat.Once, Repeat.Weekly, Repeat.Fortnightly, Repeat.Monthly, Repeat.Daily, Repeat.Weekdays };

    private static readonly BusyStatus[] ShowAsCycle =
        { BusyStatus.Busy, BusyStatus.Tentative, BusyStatus.Free, BusyStatus.OutOfOffice, BusyStatus.WorkingElsewhere };

    public MeetingOptions ToggleTeams() => this with { Teams = !Teams };

    /// <summary>
    /// All day on or off. As in Outlook, a new all-day entry shows you as
    /// free, since it is usually a reminder rather than a meeting; a show-as
    /// the user already chose is kept.
    /// </summary>
    public MeetingOptions ToggleAllDay() => AllDay
        ? this with { AllDay = false, ShowAs = ShowAs == BusyStatus.Free ? BusyStatus.Busy : ShowAs }
        : this with { AllDay = true, ShowAs = ShowAs == BusyStatus.Busy ? BusyStatus.Free : ShowAs };

    public MeetingOptions NextRepeat() => this with { Repeat = Next(RepeatCycle, Repeat) };

    public MeetingOptions NextShowAs() => this with { ShowAs = Next(ShowAsCycle, ShowAs) };

    /// <summary>One line for the palette, e.g. "Teams · every Tuesday · shows as busy".</summary>
    public string Describe(DateTimeOffset? start)
    {
        var parts = new List<string> { Teams ? "Teams meeting" : "no Teams" };
        if (AllDay) parts.Add("all day");
        parts.Add(DescribeRepeat(Repeat, start));
        parts.Add($"shows as {DescribeShowAs(ShowAs)}");
        return string.Join(" · ", parts);
    }

    public static string DescribeRepeat(Repeat repeat, DateTimeOffset? start)
    {
        var day = start?.ToString("dddd", CultureInfo.CurrentCulture);
        return repeat switch
        {
            Repeat.Daily => "every day",
            Repeat.Weekdays => "every weekday",
            Repeat.Weekly => day is null ? "every week" : $"every {day}",
            Repeat.Fortnightly => day is null ? "every other week" : $"every other {day}",
            Repeat.Monthly => start is { } s ? $"monthly on the {Ordinal(s.Day)}" : "every month",
            _ => "one time",
        };
    }

    public static string DescribeShowAs(BusyStatus status) => status switch
    {
        BusyStatus.Free => "free",
        BusyStatus.Tentative => "tentative",
        BusyStatus.OutOfOffice => "out of office",
        BusyStatus.WorkingElsewhere => "working elsewhere",
        _ => "busy",
    };

    /// <summary>
    /// The whole days an all-day entry covers, from the day it starts: the
    /// midnight it begins and the midnight after it ends, as Outlook stores them.
    /// </summary>
    public static TimeSlot WholeDays(DateTimeOffset start, int days = 1)
    {
        var first = new DateTimeOffset(start.Date, start.Offset);
        return new TimeSlot(first, first.AddDays(Math.Max(1, days)));
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };

    private static T Next<T>(T[] cycle, T current) where T : struct, Enum
    {
        var i = Array.IndexOf(cycle, current);
        return cycle[(i + 1) % cycle.Length];
    }
}
