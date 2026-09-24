using Dapper;
using EmailTriage.Core.Abstractions;

namespace EmailTriage.Core.Data;

/// <summary>
/// Remembers where mail actually gets filed. This is what lets the move palette
/// put the right folder first after a week of use.
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

    private sealed class UsageRow
    {
        public string Path { get; set; } = "";
        public long Count { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }
}
