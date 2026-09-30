using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <param name="Indent">
/// Nesting below a matched parent: 0 for a ranked match, 1 for its direct
/// subfolders, and so on.
/// </param>
public sealed record FolderMatch(FolderNode Folder, int Score, int[] NameHighlights, int Indent = 0)
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

    /// <summary>How many parents get their subfolders listed beneath them.</summary>
    public int ExpandedParents { get; init; } = 3;

    /// <summary>Cap on subfolders listed under any one parent.</summary>
    public int SubfoldersPerParent { get; init; } = 60;

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

        var ranked = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Folder.Depth)
            .ThenBy(r => r.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return NestSubfolders(query, ranked);
    }

    /// <summary>
    /// When the query names a folder outright ("1940 Jerome"), list that
    /// folder's subfolders directly beneath it so the user can see what is
    /// already there before filing or creating another one.
    /// </summary>
    private List<FolderMatch> NestSubfolders(string query, List<FolderMatch> ranked)
    {
        var key = Compact(query);
        if (key.Length == 0) return ranked;

        // Pick the parents first, so a subfolder that out-ranked its parent
        // still lands under it rather than floating above on its own.
        var parents = ranked
            .Where(m => Compact(m.Folder.Name).Contains(key, StringComparison.OrdinalIgnoreCase))
            .Where(m => _index.Any(f => IsUnder(f.Path, m.Folder.Path)))
            .Take(ExpandedParents)
            .ToList();
        parents.RemoveAll(p => parents.Any(q => IsUnder(p.Folder.Path, q.Folder.Path)));

        if (parents.Count == 0) return ranked;

        var output = new List<FolderMatch>(ranked.Count);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in ranked)
        {
            // Listed beneath its parent instead.
            if (parents.Any(p => IsUnder(match.Folder.Path, p.Folder.Path))) continue;
            if (!shown.Add(match.Folder.Path)) continue;
            output.Add(match);

            if (!parents.Contains(match)) continue;

            var children = _index
                .Where(f => IsUnder(f.Path, match.Folder.Path))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(SubfoldersPerParent);

            foreach (var child in children)
            {
                if (!shown.Add(child.Path)) continue;
                output.Add(new FolderMatch(
                    child, match.Score, Array.Empty<int>(),
                    Segments(child.Path) - Segments(match.Folder.Path)));
            }
        }

        return output;
    }

    private static bool IsUnder(string path, string parent) =>
        path.Length > parent.Length + 1
        && path[parent.Length] == '\\'
        && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);

    private static int Segments(string path) => path.Count(c => c == '\\');

    /// <summary>Letters and digits only, so "1940 jer" and "1940-Jerome" compare alike.</summary>
    private static string Compact(string text) =>
        new(text.Where(char.IsLetterOrDigit).ToArray());

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
