using System.Text.RegularExpressions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The rules behind the capture popup: where a card lands given what was
/// typed, when to chase if no day was given, and the one-line preview that
/// says so before Enter is pressed. Pure functions, so they are tested here
/// and the view model only formats.
/// </summary>
public static partial class ActionCapture
{
    /// <summary>"RE: FW: Level 3 RFI" becomes "Level 3 RFI", so the title box starts clean.</summary>
    public static string CleanSubject(string subject)
    {
        var text = subject.Trim();
        while (true)
        {
            var m = PrefixRegex().Match(text);
            if (!m.Success) break;
            text = text[m.Length..].Trim();
        }
        return text;
    }

    /// <summary>
    /// The column the card goes to. A person named puts it in Waiting; nothing
    /// named keeps it where it is, except that a finished card reopens in To do.
    /// </summary>
    public static ActionStage ResultingStage(CaptureRequest request, ActionStage current) =>
        request.HasWait ? ActionStage.Waiting
        : current == ActionStage.Done ? ActionStage.ToDo
        : current;

    /// <summary>
    /// When to chase if the user did not say: a few days before the due date
    /// when that is still ahead, the due day itself when the gap is too short,
    /// and otherwise the usual interval from now. Null when chasing is off.
    /// </summary>
    public static DateTimeOffset? DefaultFollowUp(
        DateTimeOffset? dueUtc, DateTimeOffset now, int beforeDueDays, int afterDays)
    {
        var today = now.Date;

        if (dueUtc is { } due)
        {
            var dueLocal = due.ToOffset(now.Offset);
            var before = dueLocal.AddDays(-Math.Max(0, beforeDueDays));
            if (beforeDueDays > 0 && before.Date > today) return before.ToUniversalTime();
            if (dueLocal.Date > today) return due;
        }

        return afterDays > 0 ? now.AddDays(afterDays).ToUniversalTime() : null;
    }

    /// <summary>
    /// The preview line under the form, e.g. "Lands in Waiting on Sam Lee ·
    /// due Fri 2 Oct · follow up Wed 30 Sep".
    /// </summary>
    public static string Describe(CaptureRequest request, ActionStage current, DateTimeOffset now)
    {
        var stage = ResultingStage(request, current);
        var where = stage switch
        {
            ActionStage.Waiting when request.HasWait =>
                $"Lands in Waiting on {request.Who!.Value.Display}" + (request.IsBlocker ? " (blocker)" : ""),
            ActionStage.Waiting => "Stays in Waiting",
            ActionStage.Doing => "Stays in Doing",
            _ => "Lands in To do",
        };

        var details = Details(request, now);
        return details.Length > 0 ? $"{where} · {details}" : where;
    }

    /// <summary>The dates and priority alone, for a status line: "due Fri 2 Oct · follow up Wed 30 Sep".</summary>
    public static string Details(CaptureRequest request, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (request.DueUtc is { } due) parts.Add($"due {Day(due, now)}");
        if (request.HasWait && request.FollowUpUtc is { } chase) parts.Add($"follow up {Day(chase, now)}");
        if (request.Priority == ActionPriority.High) parts.Add("high priority");
        if (request.Priority == ActionPriority.Low) parts.Add("low priority");
        return string.Join(" · ", parts);
    }

    private static string Day(DateTimeOffset when, DateTimeOffset now) =>
        when.ToOffset(now.Offset).ToString("ddd d MMM");

    [GeneratedRegex(@"^(re|fw|fwd|aw|wg)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();
}
