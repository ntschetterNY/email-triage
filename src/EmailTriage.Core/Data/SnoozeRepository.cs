using Dapper;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Data;

public sealed class SnoozeRepository : ISnoozeRepository
{
    private readonly Database _db;
    private readonly IClock _clock;

    public SnoozeRepository(Database db, IClock clock)
    {
        _db = db;
        _clock = clock;
        SqlMapping.EnsureRegistered();
    }

    private const string Select = """
        SELECT id                     AS Id,
               internet_message_id    AS InternetMessageId,
               entry_id               AS EntryId,
               store_id               AS StoreId,
               subject                AS Subject,
               sender_name            AS SenderName,
               origin_folder_entry_id AS OriginFolderEntryId,
               origin_folder_store_id AS OriginFolderStoreId,
               origin_folder_path     AS OriginFolderPath,
               snoozed_utc            AS SnoozedUtc,
               return_utc             AS ReturnUtc,
               restored_utc           AS RestoredUtc,
               last_error             AS LastError,
               failure_count          AS FailureCount
        FROM snoozes
        """;

    public async Task<SnoozeEntry> AddAsync(SnoozeEntry entry, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        if (entry.SnoozedUtc == default) entry.SnoozedUtc = _clock.UtcNow;

        entry.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO snoozes
                (internet_message_id, entry_id, store_id, subject, sender_name,
                 origin_folder_entry_id, origin_folder_store_id, origin_folder_path,
                 snoozed_utc, return_utc, restored_utc, last_error, failure_count)
            VALUES
                (@InternetMessageId, @EntryId, @StoreId, @Subject, @SenderName,
                 @OriginFolderEntryId, @OriginFolderStoreId, @OriginFolderPath,
                 @SnoozedUtc, @ReturnUtc, @RestoredUtc, @LastError, @FailureCount)
            RETURNING id;
            """, entry, cancellationToken: ct)).ConfigureAwait(false);

        return entry;
    }

    public async Task<IReadOnlyList<SnoozeEntry>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        var rows = await conn.QueryAsync<SnoozeEntry>(new CommandDefinition(
            $"{Select} WHERE restored_utc IS NULL ORDER BY return_utc",
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SnoozeEntry>> GetDueAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        var rows = await conn.QueryAsync<SnoozeEntry>(new CommandDefinition(
            $"{Select} WHERE restored_utc IS NULL AND return_utc <= @now ORDER BY return_utc",
            new { now }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task MarkRestoredAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE snoozes SET restored_utc = @when, last_error = '' WHERE id = @id",
            new { id, when = _clock.UtcNow }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(long id, string error, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE snoozes
            SET last_error = @error, failure_count = failure_count + 1
            WHERE id = @id
            """, new { id, error }, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops trying to restore an entry. Recorded as restored so the loop skips
    /// it, with the error text left in place for the UI to explain why.
    /// </summary>
    public async Task CancelAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE snoozes SET restored_utc = @when WHERE id = @id",
            new { id, when = _clock.UtcNow }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
