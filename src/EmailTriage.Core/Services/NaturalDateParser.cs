using System.Globalization;
using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Parses the shorthand people actually type into a snooze box - "3d",
/// "tomorrow 9am", "fri", "next tue 14:30" - into a concrete instant.
/// Deliberately narrow: it recognises a handful of unambiguous shapes and
/// returns null for anything else rather than guessing.
/// </summary>
public static partial class NaturalDateParser
{
    public static bool TryParse(
        string input, DateTimeOffset now, out DateTimeOffset result, SnoozeDayShape? shape = null)
    {
        shape ??= SnoozeDayShape.Default;
        result = default;

        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim().ToLowerInvariant();
        text = WhitespaceRegex().Replace(text, " ");

        // "45m", "2h", "3d", "1w" - a pure offset from now.
        var rel = RelativeRegex().Match(text);
        if (rel.Success)
        {
            var qty = double.Parse(rel.Groups["n"].Value, CultureInfo.InvariantCulture);
            result = rel.Groups["u"].Value switch
            {
                "m" or "min" or "mins" or "minute" or "minutes" => now.AddMinutes(qty),
                "h" or "hr" or "hrs" or "hour" or "hours" => now.AddHours(qty),
                "d" or "day" or "days" => now.AddDays(qty),
                "w" or "wk" or "week" or "weeks" => now.AddDays(qty * 7),
                "mo" or "month" or "months" => now.AddMonths((int)qty),
                _ => default,
            };
            return result != default;
        }

        // Try the whole string as a date first, so "2026-10-14" and "14 oct"
        // are not mangled by the time splitter grabbing their trailing number.
        var (day, defaultTime) = ResolveDay(text, now, shape);
        string? dayPart = text;
        TimeSpan? time = null;

        if (day is null)
        {
            // Otherwise separate a leading day word from a trailing time,
            // e.g. "tomorrow 9am".
            var (dp, timePart) = SplitDayAndTime(text);
            dayPart = dp;
            (day, defaultTime) = ResolveDay(dp, now, shape);
            time = timePart is null ? null : ResolveTime(timePart);
        }

        // A bare time ("6pm") means today, or tomorrow if that moment has passed.
        if (day is null && time is not null)
        {
            var todayAt = new DateTimeOffset(now.Date + time.Value, now.Offset);
            result = todayAt > now ? todayAt : todayAt.AddDays(1);
            return true;
        }

        if (day is null) return false;

        var chosen = time ?? defaultTime ?? shape.Morning;
        result = new DateTimeOffset(day.Value.Date + chosen, now.Offset);

        // "monday 9am" typed on a Monday at 10am means next Monday.
        if (result <= now && dayPart is not null && IsWeekday(dayPart, out _))
            result = result.AddDays(7);

        return result > now;
    }

    private static (string? Day, string? Time) SplitDayAndTime(string text)
    {
        var timeMatch = TimeRegex().Match(text);
        if (!timeMatch.Success) return (text.Length == 0 ? null : text, null);

        var day = text[..timeMatch.Index].Trim();
        return (day.Length == 0 ? null : day, timeMatch.Value.Trim());
    }

    /// <summary>
    /// Resolves a day word, and where the word itself implies a time of day
    /// ("tonight") returns that too so it is not overridden by the morning default.
    /// </summary>
    private static (DateTime? Day, TimeSpan? DefaultTime) ResolveDay(
        string? word, DateTimeOffset now, SnoozeDayShape shape)
    {
        if (word is null) return (null, null);
        word = word.Trim();

        switch (word)
        {
            case "today" or "tod": return (now.Date, null);
            case "tomorrow" or "tom" or "tmr" or "tmrw": return (now.Date.AddDays(1), null);
            case "tonight" or "this evening" or "evening": return (now.Date, shape.Evening);
            case "later": return (now.Date, null);
            case "next week" or "nextweek": return (NextDayOfWeek(now.Date, shape.WeekStart), null);
            case "weekend" or "this weekend": return (NextDayOfWeek(now.Date, DayOfWeek.Saturday), null);
            case "next month": return (now.Date.AddMonths(1), null);
        }

        bool forceNext = false;
        if (word.StartsWith("next ", StringComparison.Ordinal))
        {
            forceNext = true;
            word = word[5..].Trim();
        }

        if (IsWeekday(word, out var dow))
        {
            var d = NextDayOfWeek(now.Date, dow);
            if (forceNext && d <= now.Date.AddDays(6)) d = d.AddDays(7);
            return (d, null);
        }

        // "14 oct", "oct 14", "2026-10-14"
        foreach (var fmt in new[] { "yyyy-MM-dd", "d MMM", "MMM d", "d/M", "M/d", "d MMMM", "MMMM d" })
        {
            if (DateTime.TryParseExact(word, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                if (fmt is not "yyyy-MM-dd")
                {
                    parsed = new DateTime(now.Year, parsed.Month, parsed.Day);
                    if (parsed < now.Date) parsed = parsed.AddYears(1);
                }
                return (parsed.Date, null);
            }
        }

        return (null, null);
    }

    private static TimeSpan? ResolveTime(string text)
    {
        text = text.Trim();

        switch (text)
        {
            case "noon": return new TimeSpan(12, 0, 0);
            case "midnight": return new TimeSpan(0, 0, 0);
        }

        var m = TimeRegex().Match(text);
        if (!m.Success) return null;

        int hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minute = m.Groups["m"].Success
            ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture)
            : 0;

        var meridiem = m.Groups["ap"].Success ? m.Groups["ap"].Value : null;

        if (meridiem is not null)
        {
            if (hour is < 1 or > 12) return null;
            if (meridiem.StartsWith('p') && hour != 12) hour += 12;
            if (meridiem.StartsWith('a') && hour == 12) hour = 0;
        }
        else if (hour is < 0 or > 23)
        {
            return null;
        }

        if (minute is < 0 or > 59) return null;
        return new TimeSpan(hour, minute, 0);
    }

    private static bool IsWeekday(string word, out DayOfWeek day)
    {
        (string[] Names, DayOfWeek Day)[] table =
        {
            (new[] { "monday", "mon" }, DayOfWeek.Monday),
            (new[] { "tuesday", "tue", "tues" }, DayOfWeek.Tuesday),
            (new[] { "wednesday", "wed", "weds" }, DayOfWeek.Wednesday),
            (new[] { "thursday", "thu", "thur", "thurs" }, DayOfWeek.Thursday),
            (new[] { "friday", "fri" }, DayOfWeek.Friday),
            (new[] { "saturday", "sat" }, DayOfWeek.Saturday),
            (new[] { "sunday", "sun" }, DayOfWeek.Sunday),
        };

        foreach (var (names, d) in table)
        {
            if (names.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                day = d;
                return true;
            }
        }

        day = default;
        return false;
    }

    private static DateTime NextDayOfWeek(DateTime from, DayOfWeek target)
    {
        var d = from;
        do { d = d.AddDays(1); } while (d.DayOfWeek != target);
        return d;
    }

    [GeneratedRegex(@"^\s*(?<n>\d+(?:\.\d+)?)\s*(?<u>mo|months?|mins?|minutes?|m|hrs?|hours?|h|days?|d|wks?|weeks?|w)\s*$")]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"(?<![\d:])(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm|a|p)?\s*$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
