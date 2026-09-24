using System.Globalization;
using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Reads what people type when putting something on the calendar: a start,
/// a length, or both - "tomorrow 2pm 1h", "fri 10-11:30am", "45m". Builds on
/// <see cref="NaturalDateParser"/> for the start, so the two boxes understand
/// the same days and times.
/// </summary>
public static partial class EventTimeParser
{
    /// <summary>
    /// True when anything was recognised. A length on its own ("1h") leaves
    /// <paramref name="start"/> null, so the caller can offer free slots of
    /// that length; a start on its own leaves <paramref name="duration"/> null.
    /// </summary>
    public static bool TryParse(
        string input, DateTimeOffset now,
        out DateTimeOffset? start, out TimeSpan? duration,
        SnoozeDayShape? shape = null)
    {
        start = null;
        duration = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = WhitespaceRegex().Replace(input.Trim().ToLowerInvariant(), " ");

        // "tomorrow at 2pm" reads the same as "tomorrow 2pm".
        text = AtRegex().Replace(text, " ").Trim();

        // "2-3pm", "14:00-15:30", "10 to 11:30am": a range gives both ends.
        var range = RangeRegex().Match(text);
        if (range.Success)
        {
            var dayPart = text[..range.Index].Trim();
            var (from, to) = RangeEnds(range.Groups["t1"].Value, range.Groups["t2"].Value);
            var startText = dayPart.Length == 0 ? from : $"{dayPart} {from}";

            if (!NaturalDateParser.TryParse(startText, now, out var s, shape)) return false;
            if (!NaturalDateParser.TryParse(to, s.AddMinutes(-1), out var e, shape)) return false;

            // Same day as the start; the end parse only borrowed "now" to anchor it.
            e = new DateTimeOffset(s.Date + e.TimeOfDay, s.Offset);
            if (e <= s) return false;

            start = s;
            duration = e - s;
            return true;
        }

        // A trailing length: "1h", "30m", "1h30", "for 90 min", "1.5 hours".
        var length = DurationRegex().Match(text);
        if (length.Success && ReadDuration(length) is { } d)
        {
            duration = d;
            text = text[..length.Index].Trim();
            if (text.Length == 0) return true;
        }

        if (!NaturalDateParser.TryParse(text, now, out var parsed, shape))
        {
            duration = null;
            return false;
        }

        start = parsed;
        return true;
    }

    private static TimeSpan? ReadDuration(Match m)
    {
        double hours = 0, minutes = 0;

        if (m.Groups["h"].Success)
            hours = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["hm"].Success)
            minutes = double.Parse(m.Groups["hm"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["m"].Success)
            minutes = double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);

        var total = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return total > TimeSpan.Zero && total <= TimeSpan.FromHours(24) ? total : null;
    }

    /// <summary>
    /// "2-3pm" means 2pm to 3pm, but "11-1pm" means 11am to 1pm: the start
    /// takes the end's am/pm unless that would put it after the end.
    /// </summary>
    private static (string From, string To) RangeEnds(string t1, string t2)
    {
        t1 = t1.Trim();
        t2 = t2.Trim();

        var meridiem = MeridiemRegex().Match(t2);
        if (!meridiem.Success || MeridiemRegex().IsMatch(t1)) return (t1, t2);

        var suffix = meridiem.Value.Trim();
        var h1 = HourOf(t1);
        var h2 = HourOf(t2);

        // Borrowing "pm" for an hour that comes later on the clock face would
        // make the start after the end, so it must be the morning.
        if (suffix.StartsWith('p') && h2 != 12 && (h1 > h2 || h1 == 12))
            return (h1 == 12 ? t1 + "pm" : t1 + "am", t2);

        return (t1 + suffix, t2);
    }

    private static int HourOf(string time)
    {
        var digits = HourRegex().Match(time);
        return digits.Success ? int.Parse(digits.Value, CultureInfo.InvariantCulture) : 0;
    }

    // Must start a word, so the "10-14" of "2026-10-14" is not taken for a range.
    [GeneratedRegex(@"(?<=^|\s)(?<t1>\d{1,2}(?::\d{2})?\s*(?:am|pm|a|p)?)\s*(?:-|–|to|until)\s*(?<t2>\d{1,2}(?::\d{2})?\s*(?:am|pm|a|p)?)$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"(?:^|\s)(?:for\s+)?(?:(?<h>\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hour|hours)\s*(?<hm>\d{1,2})?\s*(?:m|min|mins|minutes)?|(?<m>\d+)\s*(?:m|min|mins|minutes))$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"(am|pm|a|p)$")]
    private static partial Regex MeridiemRegex();

    [GeneratedRegex(@"^\d{1,2}")]
    private static partial Regex HourRegex();

    [GeneratedRegex(@"\s+at\s+")]
    private static partial Regex AtRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
