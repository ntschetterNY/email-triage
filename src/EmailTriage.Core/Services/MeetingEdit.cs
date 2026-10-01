using System.Globalization;
using System.Text.RegularExpressions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>What the meeting card's edit form holds: everything as typed.</summary>
public sealed record MeetingForm
{
    public string Subject { get; init; } = "";

    /// <summary>"Fri 2 Oct 2026", or anything <see cref="MeetingEdit.TryParseDate"/> reads.</summary>
    public string Date { get; init; } = "";

    /// <summary>"09:00", "9am", "2:15pm".</summary>
    public string StartTime { get; init; } = "";
    public string EndTime { get; init; } = "";
    public bool IsAllDay { get; init; }
    public string Location { get; init; } = "";
    public BusyStatus ShowAs { get; init; } = BusyStatus.Busy;

    /// <summary>A recipient line: "Jane Smith &lt;jane@x.com&gt;; bob@y.com; Al".</summary>
    public string Required { get; init; } = "";
    public string Optional { get; init; } = "";
    public string Body { get; init; } = "";
    public bool AddTeams { get; init; }
}

/// <summary>
/// Editing a meeting from its card: fills the form from the meeting, reads
/// it back, and works out what has to change in Outlook and whether the
/// attendees are told.
/// </summary>
public static partial class MeetingEdit
{
    private const string DateFormat = "ddd d MMM yyyy";

    /// <summary>
    /// Yours to change: an appointment, or a meeting you organised. Someone
    /// else's meeting can only be answered - its changes come from them.
    /// </summary>
    public static bool CanEdit(CalendarEvent ev) => !ev.IsMeeting || ev.IsOrganizer;

    public static MeetingForm FormFor(CalendarEvent ev, CalendarEventDetail? detail)
    {
        // The organizer is on the list too, but is not someone you invite.
        var people = (detail?.Attendees ?? Array.Empty<Attendee>())
            .Where(a => a.Response != MeetingResponse.Organized)
            .ToList();

        return new MeetingForm
        {
            Subject = ev.Subject,
            Date = FormatDate(ev.Start.Date),
            StartTime = ev.IsAllDay ? "09:00" : FormatTime(ev.Start.TimeOfDay),
            EndTime = ev.IsAllDay ? "09:30" : FormatTime(ev.End.TimeOfDay),
            IsAllDay = ev.IsAllDay,
            Location = ev.Location,
            ShowAs = ev.Busy,
            Required = Line(people.Where(a => !a.IsOptional)),
            Optional = Line(people.Where(a => a.IsOptional)),
            Body = detail?.Body ?? "",
        };
    }

    private static string Line(IEnumerable<Attendee> people) =>
        RecipientLine.Format(people.Select(a => new Recipient(a.Name, a.Address)));

    /// <summary>
    /// The change to make, or the reason the form cannot be saved yet.
    /// <paramref name="original"/> is the form as it was first filled, so
    /// only what was touched is rewritten.
    /// </summary>
    public static string? TryBuild(
        MeetingForm form, MeetingForm original, CalendarEvent ev, DateTimeOffset now, out CalendarEventChange? change)
    {
        change = null;

        var subject = form.Subject.Trim();
        if (subject.Length == 0) return "Give it a title";

        if (!TryParseDate(form.Date, now, out var day)) return $"Could not read the date \"{form.Date.Trim()}\"";

        DateTimeOffset start, end;
        if (form.IsAllDay)
        {
            // Keep a multi-day event's length; a meeting turned all-day takes the one day.
            var days = ev.IsAllDay ? Math.Max(1, (int)Math.Round((ev.End - ev.Start).TotalDays)) : 1;
            start = Local(day);
            end = Local(day.AddDays(days));
        }
        else
        {
            if (!TryParseTime(form.StartTime, out var from)) return $"Could not read the start time \"{form.StartTime.Trim()}\"";
            if (!TryParseTime(form.EndTime, out var to)) return $"Could not read the end time \"{form.EndTime.Trim()}\"";
            if (to <= from) return "It ends before it starts";
            start = Local(day + from);
            end = Local(day + to);
        }

        var required = RecipientLine.Parse(form.Required);
        var optional = RecipientLine.Parse(form.Optional);
        var invitees = required.Select(w => new Invitee(w, false))
            .Concat(optional.Select(w => new Invitee(w, true)))
            .DistinctBy(i => i.Who, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var peopleChanged = !SameLine(form.Required, original.Required) || !SameLine(form.Optional, original.Optional);
        if (ev.IsMeeting && invitees.Count == 0)
            return "A meeting needs someone on it - to call it off, cancel it in Outlook";

        change = new CalendarEventChange
        {
            Subject = subject,
            Start = start,
            End = end,
            IsAllDay = form.IsAllDay,
            Location = form.Location.Trim(),
            ShowAs = form.ShowAs,
            Body = form.Body.Trim() == original.Body.Trim() ? null : form.Body.Trim(),
            Invitees = peopleChanged ? invitees : null,
            Send = invitees.Count > 0,
            AddTeams = form.AddTeams,
        };
        return null;
    }

    /// <summary>Whether two recipient lines name the same people, whatever the order or spacing.</summary>
    private static bool SameLine(string a, string b) =>
        RecipientLine.Parse(a).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(RecipientLine.Parse(b).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>What the save button says: whether, and how, the attendees hear of it.</summary>
    public static string SaveLabel(MeetingForm form, CalendarEvent ev)
    {
        var anyone = RecipientLine.Parse(form.Required).Count + RecipientLine.Parse(form.Optional).Count > 0;
        if (!anyone) return "Save";
        return ev.IsMeeting ? "Send update" : "Send invitation";
    }

    private static DateTimeOffset Local(DateTime wallClock) =>
        new(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(wallClock));

    public static string FormatDate(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    /// <summary>
    /// A day as typed: "Fri 2 Oct 2026" (as the form shows it), "2 Oct",
    /// "2026-10-02", or the words the snooze box knows - "tomorrow", "fri".
    /// </summary>
    public static bool TryParseDate(string text, DateTimeOffset now, out DateTime date)
    {
        date = default;
        text = text.Trim();
        if (text.Length == 0) return false;

        string[] formats = { DateFormat, "dddd d MMMM yyyy", "d MMM yyyy", "d MMMM yyyy", "ddd d MMM", "d MMM", "d MMMM", "yyyy-MM-dd" };
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
        {
            if (DateTime.TryParseExact(text, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            {
                // A day with no year is the next one to come.
                if (!HasYear(text) && exact.Date < now.Date) exact = exact.AddYears(1);
                date = exact.Date;
                return true;
            }
        }

        if (NaturalDateParser.TryParse(text, now, out var natural))
        {
            date = natural.Date;
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var loose))
        {
            date = loose.Date;
            return true;
        }

        return false;
    }

    private static bool HasYear(string text) => YearRegex().IsMatch(text);

    /// <summary>A time of day: "9", "9:30", "0930", "14.00", "9am", "2:15 pm", "noon".</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        text = text.Trim().ToLowerInvariant();
        if (text == "noon") { time = TimeSpan.FromHours(12); return true; }
        if (text == "midnight") { time = TimeSpan.Zero; return true; }

        var m = TimeRegex().Match(text);
        if (!m.Success) return false;

        int hour, minute = 0;
        var digits = m.Groups["h"].Value;
        if (m.Groups["m"].Success)
        {
            hour = int.Parse(digits, CultureInfo.InvariantCulture);
            minute = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        }
        else if (digits.Length >= 3)
        {
            // "930", "1430"
            hour = int.Parse(digits[..^2], CultureInfo.InvariantCulture);
            minute = int.Parse(digits[^2..], CultureInfo.InvariantCulture);
        }
        else
        {
            hour = int.Parse(digits, CultureInfo.InvariantCulture);
        }

        var half = m.Groups["ap"].Value;
        if (half.Length > 0)
        {
            if (hour is < 1 or > 12) return false;
            if (half.StartsWith('p') && hour != 12) hour += 12;
            if (half.StartsWith('a') && hour == 12) hour = 0;
        }

        if (hour > 23 || minute > 59) return false;
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    [GeneratedRegex(@"^(?<h>\d{1,4})(?:[:.](?<m>\d{2}))?\s*(?<ap>a\.?m\.?|p\.?m\.?|a|p)?$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex YearRegex();
}
