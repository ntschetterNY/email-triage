using Dapper;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Data;

/// <summary>
/// Remembers where mail actually gets filed, and what the filed mail looked
/// like. This is what lets the move palette put the right folder first after
/// a week of use, and guess the folder for a mail it has seen the likes of.
/// </summary>
public sealed class FolderUsageRepository : IFolderUsageRepository
{
    private readonly Database _db;
    private readonly IClock _clock;

    public FolderUsageRepository(Database db, IClock clock)
    {
        _db = db;
        _clock = clock;
        SqlMapping.EnsureRegistered();
    }

    public async Task RecordUseAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO folder_usage (folder_path, use_count, last_used_utc)
            VALUES (@path, 1, @now)
            ON CONFLICT(folder_path) DO UPDATE SET
                use_count = use_count + 1,
                last_used_utc = excluded.last_used_utc;
            """, new { path = folderPath, now = _clock.UtcNow }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, double>> GetScoresAsync(
        CancellationToken ct = default)
    {
        await using var conn = _db.Open();

        var rows = await conn.QueryAsync<UsageRow>(new CommandDefinition(
            "SELECT folder_path AS Path, use_count AS Count, last_used_utc AS LastUsed FROM folder_usage",
            cancellationToken: ct)).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            // Halve the weight of a folder's history every 60 days, so habits
            // that have moved on stop dominating the list.
            var ageDays = Math.Max(0, (now - row.LastUsed).TotalDays);
            var decay = Math.Pow(0.5, ageDays / 60.0);
            result[row.Path] = row.Count * decay;
        }

        return result;
    }

    public async Task RecordEvidenceAsync(
        string folderPath, IEnumerable<FilingFeature> features, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        var counts = features
            .Where(f => !string.IsNullOrWhiteSpace(f.Token))
            .GroupBy(f => f)
            .Select(g => new { path = folderPath, kind = (int)g.Key.Kind, token = g.Key.Token, count = g.Count(), now = _clock.UtcNow })
            .ToList();
        if (counts.Count == 0) return;

        await using var conn = _db.Open();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO filing_evidence (folder_path, kind, token, count, last_seen_utc)
            VALUES (@path, @kind, @token, @count, @now)
            ON CONFLICT(folder_path, kind, token) DO UPDATE SET
                count = count + excluded.count,
                last_seen_utc = excluded.last_seen_utc;
            """, counts, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FilingEvidence>> GetEvidenceAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();

        var rows = await conn.QueryAsync<EvidenceRow>(new CommandDefinition(
            "SELECT folder_path AS Path, kind AS Kind, token AS Token, count AS Count FROM filing_evidence WHERE count > 0",
            cancellationToken: ct)).ConfigureAwait(false);

        return rows
            .Select(r => new FilingEvidence(r.Path, (FilingFeatureKind)r.Kind, r.Token, r.Count))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetStudiedAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();

        var rows = await conn.QueryAsync<StudyRow>(new CommandDefinition(
            "SELECT folder_path AS Path, studied_utc AS StudiedUtc FROM folder_study",
            cancellationToken: ct)).ConfigureAwait(false);

        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) result[row.Path] = row.StudiedUtc;
        return result;
    }

    public async Task MarkStudiedAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO folder_study (folder_path, studied_utc) VALUES (@path, @now)
            ON CONFLICT(folder_path) DO UPDATE SET studied_utc = excluded.studied_utc;
            """, new { path = folderPath, now = _clock.UtcNow }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    private sealed class EvidenceRow
    {
        public string Path { get; set; } = "";
        public long Kind { get; set; }
        public string Token { get; set; } = "";
        public long Count { get; set; }
    }

    private sealed class StudyRow
    {
        public string Path { get; set; } = "";
        public DateTimeOffset StudiedUtc { get; set; }
    }

    private sealed class UsageRow
    {
        public string Path { get; set; } = "";
        public long Count { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }
}
