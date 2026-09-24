using System.Net;
using System.Text;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>One open wait: a blocker or hand-off on an action item, and who it is on.</summary>
public sealed record WaitRow(
    string Person,
    string Kind,
    ActionItem Item,
    string What,
    DateTimeOffset SinceUtc,
    DateTimeOffset? DueUtc,
    bool IsOverdue)
{
    public int DaysWaiting(DateTimeOffset now) => Math.Max(0, (int)(now - SinceUtc).TotalDays);

    public string DueDisplay => DueUtc is { } d ? d.ToLocalTime().ToString("ddd d MMM") : "";
}

/// <summary>Everything waiting on one person.</summary>
public sealed record PersonWaits(string Person, IReadOnlyList<WaitRow> Rows)
{
    public int Count => Rows.Count;
    public int OverdueCount => Rows.Count(r => r.IsOverdue);
    public DateTimeOffset OldestSinceUtc => Rows.Min(r => r.SinceUtc);
}

public enum WaitingSort
{
    Person,
    MostItems,
    LongestWait,
    MostOverdue,
}

/// <summary>
/// The "who is holding what up" report behind the board's By person view,
/// its CSV export and the emailed version.
/// </summary>
public static class WaitingReport
{
    public const string NoOneNamed = "(no one named)";

    /// <summary>Every open blocker and hand-off on open items, one row each.</summary>
    public static IReadOnlyList<WaitRow> Rows(IEnumerable<ActionItem> items)
    {
        var rows = new List<WaitRow>();
        foreach (var item in items.Where(i => !i.IsComplete))
        {
            foreach (var b in item.Blockers.Where(b => !b.IsResolved))
            {
                rows.Add(new WaitRow(
                    b.WaitingOn.Trim().Length > 0 ? b.WaitingOn.Trim() : NoOneNamed,
                    "Blocked by", item, b.Description, b.CreatedUtc, b.DueUtc, b.IsOverdue));
            }

            foreach (var a in item.Assignments.Where(a => !a.IsDone))
            {
                rows.Add(new WaitRow(
                    a.PersonName.Trim().Length > 0 ? a.PersonName.Trim() : NoOneNamed,
                    "Assigned", item, a.Task, a.CreatedUtc, a.DueUtc, a.IsOverdue));
            }
        }
        return rows;
    }

    /// <summary>Rows grouped by person (names matched case-insensitively), sorted as asked.</summary>
    public static IReadOnlyList<PersonWaits> ByPerson(IEnumerable<WaitRow> rows, WaitingSort sort)
    {
        var groups = rows
            .GroupBy(r => r.Person, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PersonWaits(
                g.First().Person,
                g.OrderByDescending(r => r.IsOverdue).ThenBy(r => r.SinceUtc).ToList()));

        // The catch-all group always goes last: it is a to-do, not a person.
        return (sort switch
            {
                WaitingSort.MostItems => groups.OrderByDescending(g => g.Count).ThenBy(g => g.Person, StringComparer.OrdinalIgnoreCase),
                WaitingSort.LongestWait => groups.OrderBy(g => g.OldestSinceUtc),
                WaitingSort.MostOverdue => groups.OrderByDescending(g => g.OverdueCount).ThenByDescending(g => g.Count),
                _ => groups.OrderBy(g => g.Person, StringComparer.OrdinalIgnoreCase),
            })
            .OrderBy(g => g.Person == NoOneNamed)
            .ToList();
    }

    /// <summary>Opens straight in Excel: one row per wait, dates as plain text.</summary>
    public static string ToCsv(IEnumerable<PersonWaits> groups, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Person,Type,Task,Waiting for,Days waiting,Due,Overdue,From,Received");

        foreach (var g in groups)
        foreach (var r in g.Rows)
        {
            sb.AppendLine(string.Join(",",
                Csv(g.Person), Csv(r.Kind), Csv(r.Item.Subject), Csv(r.What),
                r.DaysWaiting(now).ToString(),
                Csv(r.DueUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? ""),
                r.IsOverdue ? "Yes" : "",
                Csv(r.Item.SenderName),
                Csv(r.Item.ReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd"))));
        }

        return sb.ToString();
    }

    /// <summary>The same report as an HTML email body.</summary>
    public static string ToHtml(IEnumerable<PersonWaits> groups, DateTimeOffset now)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        const string cell = "style=\"padding:4px 10px;border-bottom:1px solid #ddd;text-align:left\"";

        var sb = new StringBuilder("<div style=\"font-family:Calibri,sans-serif;font-size:11pt\">");
        sb.Append($"<p>Open items waiting on others, as of {now.ToLocalTime():dddd d MMM yyyy}.</p>");

        foreach (var g in groups)
        {
            sb.Append($"<h3 style=\"margin:16px 0 4px\">{E(g.Person)} &mdash; {g.Count} item{(g.Count == 1 ? "" : "s")}");
            if (g.OverdueCount > 0) sb.Append($" <span style=\"color:#c0392b\">({g.OverdueCount} overdue)</span>");
            sb.Append("</h3><table style=\"border-collapse:collapse\">");
            sb.Append($"<tr><th {cell}>Type</th><th {cell}>Task</th><th {cell}>Waiting for</th><th {cell}>Days</th><th {cell}>Due</th></tr>");

            foreach (var r in g.Rows)
            {
                var due = r.IsOverdue ? $"<span style=\"color:#c0392b\">{E(r.DueDisplay)}</span>" : E(r.DueDisplay);
                sb.Append($"<tr><td {cell}>{E(r.Kind)}</td><td {cell}>{E(r.Item.Subject)}</td><td {cell}>{E(r.What)}</td>" +
                          $"<td {cell}>{r.DaysWaiting(now)}</td><td {cell}>{due}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Everyone ever tagged on a blocker or hand-off, for reusing the same names.</summary>
    public static IReadOnlyList<string> KnownPeople(IEnumerable<ActionItem> items) =>
        items.SelectMany(i => i.Blockers.Select(b => b.WaitingOn).Concat(i.Assignments.Select(a => a.PersonName)))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
