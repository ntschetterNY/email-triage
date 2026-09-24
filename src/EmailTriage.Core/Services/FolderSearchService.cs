using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

public sealed record FolderMatch(FolderNode Folder, int Score, int[] NameHighlights)
{
    public string Display => Folder.Path;
}

/// <summary>
/// Backs the move palette. Holds a cached flat folder index and ranks it
/// against whatever the user has typed, blending match quality with how often
/// that folder is actually used.
/// </summary>
public sealed class FolderSearchService
{
    private readonly IMailStore _store;
    private readonly IFolderUsageRepository _usage;

    private IReadOnlyList<FolderNode> _index = Array.Empty<FolderNode>();
    private IReadOnlyDictionary<string, double> _scores =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _indexedAt = DateTimeOffset.MinValue;

    /// <summary>Folder trees change rarely; re-reading them on every keystroke would be wasteful.</summary>
    public TimeSpan IndexLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public FolderSearchService(IMailStore store, IFolderUsageRepository usage)
    {
        _store = store;
        _usage = usage;
    }

    public bool IsIndexed => _index.Count > 0;

    public int FolderCount => _index.Count;

    public async Task EnsureIndexedAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _index.Count > 0 && DateTimeOffset.UtcNow - _indexedAt < IndexLifetime)
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _index.Count > 0 && DateTimeOffset.UtcNow - _indexedAt < IndexLifetime)
                return;

            _index = await _store.GetFolderIndexAsync(ct).ConfigureAwait(false);
            _scores = await _usage.GetScoresAsync(ct).ConfigureAwait(false);
            _indexedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Adds a folder created during this session to the index, so it is
    /// immediately searchable without a full re-read.
    /// </summary>
    public void AddToIndex(FolderNode folder)
    {
        if (_index.Any(f => f.Ref.EntryId == folder.Ref.EntryId)) return;
        _index = _index.Append(folder).ToList();
    }

    public async Task RecordUseAsync(FolderNode folder, CancellationToken ct = default)
    {
        await _usage.RecordUseAsync(folder.Path, ct).ConfigureAwait(false);
        _scores = await _usage.GetScoresAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ranks folders for the given query. With no query, returns the most-used
    /// folders so the palette is useful before a single key is pressed.
    /// </summary>
    public IReadOnlyList<FolderMatch> Search(string query, int limit = 40)
    {
        query = query.Trim();

        if (query.Length == 0)
        {
            return _index
                .OrderByDescending(f => UsageBoost(f))
                .ThenBy(f => f.Depth)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(f => new FolderMatch(f, 0, Array.Empty<int>()))
                .ToList();
        }

        var results = new List<FolderMatch>();

        foreach (var folder in _index)
        {
            // Score the leaf name and the full path separately: a hit on the
            // folder's own name should beat an incidental hit on its parents.
            int? nameScore = FuzzyMatcher.Score(query, folder.Name, out var namePos);
            int? pathScore = FuzzyMatcher.Score(query, folder.Path, out _);

            if (nameScore is null && pathScore is null) continue;

            double score = Math.Max(
                (nameScore ?? int.MinValue / 4) * 1.6,
                (pathScore ?? int.MinValue / 4) * 1.0);

            score += UsageBoost(folder);

            // Mildly prefer shallow folders; deep ones are usually archives.
            score -= folder.Depth * 1.5;

            results.Add(new FolderMatch(
                folder,
                (int)Math.Round(score),
                nameScore is not null ? namePos : Array.Empty<int>()));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Folder.Depth)
            .ThenBy(r => r.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Diminishing-returns boost so a folder used 200 times does not permanently
    /// drown out a better textual match.
    /// </summary>
    private double UsageBoost(FolderNode folder) =>
        _scores.TryGetValue(folder.Path, out var uses) && uses > 0
            ? 14.0 * Math.Log(1 + uses)
            : 0.0;

    /// <summary>
    /// Splits typed text into a parent folder and a new leaf name, for the
    /// "no match - create it" path. "Clients\Acme\Q3" creates Q3 under an
    /// existing Clients\Acme when that exists.
    /// </summary>
    public (FolderNode? Parent, string Name) ResolveCreationTarget(string typed)
    {
        typed = typed.Trim().Trim('\\', '/');
        if (typed.Length == 0) return (null, "");

        var parts = typed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1];

        if (parts.Length == 1) return (null, name);

        var parentQuery = string.Join('\\', parts[..^1]);

        var parent = _index
            .Where(f => IsPathSuffix(f.Path, parentQuery)
                     || f.Name.Equals(parentQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Depth)
            .FirstOrDefault();

        return (parent, name);
    }

    /// <summary>
    /// True when <paramref name="suffix"/> matches whole trailing segments of
    /// <paramref name="path"/>. Guards against "Clients\Acme" matching
    /// "Mailbox\OtherClients\Acme" on a plain string comparison.
    /// </summary>
    private static bool IsPathSuffix(string path, string suffix)
    {
        if (path.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return true;

        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var boundary = path.Length - suffix.Length - 1;
        return boundary >= 0 && path[boundary] == '\\';
    }
}
