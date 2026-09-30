using System.Text.Json;

namespace EmailTriage.Core.Services;

/// <summary>
/// One AI command, as the Claude Code CLI reported it. Cost is the CLI's own
/// list-price estimate: billed for real on an API key, but on a Claude
/// subscription it only measures how much of the plan's allowance went.
/// </summary>
public sealed record AiCall(
    DateTimeOffset At,
    string Feature,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd,
    int DurationMs,
    bool Succeeded,
    string? Error = null)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

public sealed record AiFeatureUsage(string Feature, int Calls, long Tokens, decimal CostUsd);

public sealed record AiUsageSummary(
    int Calls,
    int Failures,
    long InputTokens,
    long OutputTokens,
    long CacheTokens,
    decimal CostUsd,
    IReadOnlyList<AiFeatureUsage> ByFeature)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheTokens;
}

/// <summary>Which account the CLI answers through, from `claude auth status`.</summary>
public sealed record AiAuthInfo(bool LoggedIn, string Method, string? Account, string? Plan, string? KeySource)
{
    /// <summary>True when calls draw on a Claude subscription login rather than API credits.</summary>
    public bool IsSubscription => Method.Equals("claude.ai", StringComparison.OrdinalIgnoreCase);

    public string Describe() => !LoggedIn
        ? "Claude Code is not signed in"
        : IsSubscription
            ? $"Your Claude login{(Plan is { Length: > 0 } ? $" ({Plan} plan)" : "")}{(Account is { Length: > 0 } ? $" · {Account}" : "")}"
            : $"API key{(KeySource is { Length: > 0 } ? $" from {KeySource}" : "")} - billed to API credits, not your Claude plan";
}

/// <summary>
/// Every AI command the app has run in the last 30 days, kept as one JSON
/// line per call in %LOCALAPPDATA%\EmailTriage\ai-usage.jsonl so the top bar
/// survives a restart and the raw numbers stay open to inspection.
/// </summary>
public sealed class AiUsageLog
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<AiCall> _calls = new();

    public event EventHandler? Changed;

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage",
            "ai-usage.jsonl");

    /// <param name="path">The log file; null keeps calls in memory only.</param>
    public AiUsageLog(string? path, DateTimeOffset? now = null)
    {
        _path = path;
        if (_path is null || !File.Exists(_path)) return;

        var cutoff = (now ?? DateTimeOffset.Now) - Retention;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AiCall>(line, Json) is { } call && call.At >= cutoff)
                        _calls.Add(call);
                }
                catch (JsonException) { /* a torn line from a crash reads as absent */ }
            }
        }
        catch (IOException) { /* an unreadable log starts empty */ }
    }

    public IReadOnlyList<AiCall> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    public void Record(AiCall call)
    {
        lock (_gate)
        {
            _calls.Add(call);
            if (_path is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.AppendAllText(_path, JsonSerializer.Serialize(call, Json) + Environment.NewLine);
                }
                catch (IOException) { /* the top bar still counts it this session */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Totals for calls made at or after <paramref name="since"/>.</summary>
    public AiUsageSummary Summarise(DateTimeOffset since)
    {
        var calls = Calls.Where(c => c.At >= since).ToList();

        var byFeature = calls
            .GroupBy(c => c.Feature)
            .Select(g => new AiFeatureUsage(g.Key, g.Count(), g.Sum(c => c.TotalTokens), g.Sum(c => c.CostUsd)))
            .OrderByDescending(f => f.CostUsd)
            .ToList();

        return new AiUsageSummary(
            calls.Count,
            calls.Count(c => !c.Succeeded),
            calls.Sum(c => c.InputTokens),
            calls.Sum(c => c.OutputTokens),
            calls.Sum(c => c.CacheReadTokens + c.CacheWriteTokens),
            calls.Sum(c => c.CostUsd),
            byFeature);
    }
}
