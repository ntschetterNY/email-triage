using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// How far a Lavish note has got on its way to a release. The repo's Lavish
/// workflow moves the issue along with `lavish: …` labels as a branch, pull
/// request, merge and release reference it; the app reads them back.
/// </summary>
public enum LavishStage
{
    /// <summary>Opened on GitHub in the browser, but not submitted there yet.</summary>
    Draft,
    Filed,
    Branch,
    InReview,
    Merged,
    Released,
    /// <summary>Closed without being fixed.</summary>
    Declined,
}

/// <summary>
/// The element a note is about, as the window described it. Text is what
/// the element says, and is left out when it could be mail (a row in the
/// list, the reading pane): Lavish issues are public.
/// </summary>
public sealed record LavishTarget(string Kind, string Label, string Path, string Area, string? Text = null)
{
    public string Describe() =>
        Label.Length > 0 && !Label.Equals(Kind, StringComparison.OrdinalIgnoreCase) ? $"{Kind} · {Label}" : Kind;
}

/// <summary>One comment sent from the app, and what has become of it.</summary>
public sealed class LavishNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Comment { get; set; } = "";
    public LavishTarget Target { get; set; } = new("Window", "", "", "");
    public int? IssueNumber { get; set; }
    public string? IssueUrl { get; set; }
    public LavishStage Stage { get; set; } = LavishStage.Draft;
}

/// <summary>What goes on GitHub for a note: the issue's title and body, and how to read its progress back.</summary>
public static class LavishIssue
{
    public const string Label = "lavish";

    /// <summary>Labels the workflow sets, in the order a note moves through them.</summary>
    public static readonly IReadOnlyList<(string Label, LavishStage Stage)> StageLabels =
    [
        ("lavish: filed", LavishStage.Filed),
        ("lavish: branch", LavishStage.Branch),
        ("lavish: in review", LavishStage.InReview),
        ("lavish: merged", LavishStage.Merged),
        ("lavish: released", LavishStage.Released),
    ];

    /// <summary>
    /// GitHub turns away a new-issue link much past 8 KB; the body is cut to
    /// keep the whole address under this.
    /// </summary>
    public const int MaxUrlLength = 7500;

    private const int TitleLength = 72;

    public static string Title(string comment)
    {
        var line = comment.Trim().Split('\n', 2)[0].Trim();
        if (line.Length == 0) line = "Feedback";
        if (line.Length > TitleLength) line = line[..(TitleLength - 1)].TrimEnd() + "…";
        return $"Lavish: {line}";
    }

    /// <summary>
    /// The hidden marker that ties an issue to the note it came from, so a
    /// note submitted in the browser is still found again.
    /// </summary>
    public static string Marker(string id) => $"<!-- lavish:{id} -->";

    private static readonly Regex MarkerPattern = new(@"<!--\s*lavish:([0-9a-f]{32})\s*-->", RegexOptions.IgnoreCase);

    public static string? ParseMarker(string? body) =>
        body is null ? null : MarkerPattern.Match(body) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    public static string Body(LavishNote note, string version, string os)
    {
        var t = note.Target;
        var b = new StringBuilder();
        b.AppendLine(Marker(note.Id));
        foreach (var line in note.Comment.Trim().Split('\n'))
            b.Append("> ").AppendLine(line.TrimEnd('\r'));
        b.AppendLine();
        b.AppendLine("| | |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| **Element** | {Cell(t.Describe())} |");
        if (t.Path.Length > 0) b.AppendLine($"| **Where** | {Cell(t.Path)} |");
        if (t.Area.Length > 0) b.AppendLine($"| **Tab** | {Cell(t.Area)} |");
        if (t.Text is { Length: > 0 } text) b.AppendLine($"| **Says** | {Cell(Clip(text, 200))} |");
        b.AppendLine($"| **Version** | {Cell(version)} · {Cell(os)} |");
        b.AppendLine();
        b.AppendLine("### Tracking");
        b.AppendLine("- [ ] Branch");
        b.AppendLine("- [ ] Pull request");
        b.AppendLine("- [ ] Merged into `main`");
        b.AppendLine("- [ ] Released");
        b.AppendLine();
        b.AppendLine("<sub>Sent with Lavish from Email Triage. Name a branch `lavish-<this issue's number>-…`, "
                   + "or write `Fixes #<number>` in its pull request, and this issue follows it to a release.</sub>");
        return b.ToString();
    }

    /// <summary>
    /// A link that opens GitHub's new-issue form filled in, for when there is
    /// no token to file it directly. The person presses Submit themselves.
    /// </summary>
    public static string NewIssueUrl(string repo, string title, string body)
    {
        var head = $"https://github.com/{repo}/issues/new?labels={Uri.EscapeDataString(Label)}&title={Uri.EscapeDataString(title)}&body=";
        var encoded = Uri.EscapeDataString(body);
        if (head.Length + encoded.Length <= MaxUrlLength) return head + encoded;

        // Cut the body, not the marker at its top, and never mid-escape.
        const string cut = "\n\n…(cut to fit the link)";
        var room = MaxUrlLength - head.Length - Uri.EscapeDataString(cut).Length;
        var keep = body.Length;
        while (keep > 0 && Uri.EscapeDataString(body[..keep]).Length > room) keep = keep * 9 / 10;
        if (keep > 0 && char.IsHighSurrogate(body[keep - 1])) keep--;
        return head + Uri.EscapeDataString(body[..keep] + cut);
    }

    /// <summary>The furthest stage an issue's state and labels show.</summary>
    public static LavishStage StageOf(bool closed, string? stateReason, IEnumerable<string> labels)
    {
        var set = labels.Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
        var stage = StageLabels.LastOrDefault(s => set.Contains(s.Label)) is { Label: not null } hit
            ? hit.Stage
            : LavishStage.Filed;

        if (!closed) return stage;
        if (string.Equals(stateReason, "not_planned", StringComparison.OrdinalIgnoreCase)) return LavishStage.Declined;
        return stage < LavishStage.Merged ? LavishStage.Merged : stage;
    }

    public static string Describe(LavishStage stage) => stage switch
    {
        LavishStage.Draft => "Not submitted",
        LavishStage.Filed => "Filed",
        LavishStage.Branch => "Branch",
        LavishStage.InReview => "Pull request",
        LavishStage.Merged => "Merged",
        LavishStage.Released => "Released",
        LavishStage.Declined => "Closed",
        _ => stage.ToString(),
    };

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// Every note sent from this machine, in %LOCALAPPDATA%\EmailTriage\lavish.json,
/// so the Lavish panel can show their progress after a restart.
/// </summary>
public sealed class LavishLog
{
    private readonly string _path;

    public LavishLog(string path) => _path = path;

    public List<LavishNote> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<LavishNote>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch { /* a damaged log starts again */ }
        return [];
    }

    public void Save(IEnumerable<LavishNote> notes)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(notes, Json));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>A Lavish issue as GitHub lists it.</summary>
public sealed record LavishIssueState(int Number, string Url, string? MarkerId, LavishStage Stage);

/// <summary>
/// The two calls Lavish makes to GitHub's REST API: file an issue (needs a
/// token) and list the repo's Lavish issues (works without one on a public
/// repo, within the 60-an-hour anonymous limit).
/// </summary>
public sealed class GitHubIssues
{
    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly string? _token;

    public GitHubIssues(HttpClient http, string repo, string? token)
    {
        _http = http;
        _repo = repo;
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public bool CanFile => _token is not null;

    public async Task<(int Number, string Url)> CreateAsync(string title, string body, CancellationToken ct = default)
    {
        if (_token is null) throw new InvalidOperationException("Filing an issue needs a GitHub token.");

        var payload = JsonSerializer.Serialize(new { title, body, labels = new[] { LavishIssue.Label } });
        using var request = Request(HttpMethod.Post, $"repos/{_repo}/issues");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Explain(response.StatusCode, text), null, response.StatusCode);

        using var doc = JsonDocument.Parse(text);
        return (doc.RootElement.GetProperty("number").GetInt32(), doc.RootElement.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>The repo's Lavish issues, open and closed, newest first.</summary>
    public async Task<IReadOnlyList<LavishIssueState>> ListAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get,
            $"repos/{_repo}/issues?labels={Uri.EscapeDataString(LavishIssue.Label)}&state=all&per_page=100");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Explain(response.StatusCode, text), null, response.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var list = new List<LavishIssueState>();
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            if (issue.TryGetProperty("pull_request", out _)) continue;

            var labels = issue.GetProperty("labels").EnumerateArray()
                .Select(l => l.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
            var closed = issue.GetProperty("state").GetString() == "closed";
            var reason = issue.TryGetProperty("state_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var body = issue.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

            list.Add(new LavishIssueState(
                issue.GetProperty("number").GetInt32(),
                issue.GetProperty("html_url").GetString() ?? "",
                LavishIssue.ParseMarker(body),
                LavishIssue.StageOf(closed, reason, labels)));
        }
        return list;
    }

    /// <summary>
    /// Brings each note up to date from the listed issues, matching on the
    /// marker in the body. Returns whether anything changed.
    /// </summary>
    public static bool Apply(IEnumerable<LavishNote> notes, IReadOnlyList<LavishIssueState> issues)
    {
        var byMarker = issues.Where(i => i.MarkerId is not null).GroupBy(i => i.MarkerId!).ToDictionary(g => g.Key, g => g.First());
        var byNumber = issues.ToDictionary(i => i.Number);
        var changed = false;

        foreach (var note in notes)
        {
            var hit = note.IssueNumber is { } n && byNumber.TryGetValue(n, out var numbered) ? numbered
                    : byMarker.GetValueOrDefault(note.Id);
            if (hit is null) continue;

            if (note.IssueNumber != hit.Number || note.IssueUrl != hit.Url || note.Stage != hit.Stage)
            {
                note.IssueNumber = hit.Number;
                note.IssueUrl = hit.Url;
                note.Stage = hit.Stage;
                changed = true;
            }
        }
        return changed;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, "https://api.github.com/" + path);
        request.Headers.UserAgent.ParseAdd("EmailTriage-Lavish");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static string Explain(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized => "GitHub did not accept the token (401). Sign in again with `gh auth login`.",
        HttpStatusCode.Forbidden when body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) =>
            "GitHub's rate limit is used up for now; try again in a while.",
        HttpStatusCode.Forbidden => "The token may not open issues on this repo (403).",
        HttpStatusCode.NotFound => "GitHub cannot see that repo with this token (404).",
        HttpStatusCode.Gone => "Issues are turned off on this repo (410).",
        _ => $"GitHub answered {(int)status}.",
    };
}
