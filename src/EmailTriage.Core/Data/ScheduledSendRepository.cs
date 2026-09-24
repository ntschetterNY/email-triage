using Dapper;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Data;

public sealed class ScheduledSendRepository : IScheduledSendRepository
{
    private readonly Database _db;
    private readonly IClock _clock;

    public ScheduledSendRepository(Database db, IClock clock)
    {
        _db = db;
        _clock = clock;
        SqlMapping.EnsureRegistered();
    }

    private const string Select = """
        SELECT id              AS Id,
               draft_entry_id  AS DraftEntryId,
               draft_store_id  AS DraftStoreId,
               subject         AS Subject,
               recipients      AS Recipients,
               created_utc     AS CreatedUtc,
               send_at_utc     AS SendAtUtc,
               hold_if_replied AS HoldIfReplied,
               state           AS State,
               completed_utc   AS CompletedUtc,
               note            AS Note,
               failure_count   AS FailureCount
        FROM scheduled_sends
        """;

    public async Task<ScheduledSend> AddAsync(ScheduledSend entry, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        if (entry.CreatedUtc == default) entry.CreatedUtc = _clock.UtcNow;

        entry.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO scheduled_sends
                (draft_entry_id, draft_store_id, subject, recipients, created_utc,
                 send_at_utc, hold_if_replied, state, completed_utc, note, failure_count)
            VALUES
                (@DraftEntryId, @DraftStoreId, @Subject, @Recipients, @CreatedUtc,
                 @SendAtUtc, @HoldIfReplied, @State, @CompletedUtc, @Note, @FailureCount)
            RETURNING id;
            """, entry, cancellationToken: ct)).ConfigureAwait(false);

        return entry;
    }

    public async Task<IReadOnlyList<ScheduledSend>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        var rows = await conn.QueryAsync<ScheduledSend>(new CommandDefinition(
            $"{Select} WHERE state = 0 ORDER BY send_at_utc", cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ScheduledSend>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        var rows = await conn.QueryAsync<ScheduledSend>(new CommandDefinition(
            $"{Select} WHERE state = 0 AND send_at_utc <= @now ORDER BY send_at_utc",
            new { now }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task CompleteAsync(long id, ScheduledSendState state, string note, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE scheduled_sends SET state = @state, note = @note, completed_utc = @when WHERE id = @id",
            new { id, state = (int)state, note, when = _clock.UtcNow }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(long id, string error, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE scheduled_sends
            SET note = @error, failure_count = failure_count + 1
            WHERE id = @id
            """, new { id, error }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
