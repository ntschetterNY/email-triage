using Dapper;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Data;

public sealed class ActionItemRepository : IActionItemRepository
{
    private readonly Database _db;
    private readonly IClock _clock;

    public ActionItemRepository(Database db, IClock clock)
    {
        _db = db;
        _clock = clock;
        SqlMapping.EnsureRegistered();
    }

    private const string SelectItem = """
        SELECT id                  AS Id,
               internet_message_id AS InternetMessageId,
               entry_id            AS EntryId,
               store_id            AS StoreId,
               subject             AS Subject,
               sender_name         AS SenderName,
               sender_address      AS SenderAddress,
               received_utc        AS ReceivedUtc,
               created_utc         AS CreatedUtc,
               completed_utc       AS CompletedUtc,
               priority            AS Priority,
               notes               AS Notes,
               stage               AS Stage,
               due_utc             AS DueUtc
        FROM action_items
        """;

    public async Task<IReadOnlyList<ActionItem>> GetOpenAsync(CancellationToken ct = default)
        => await LoadAsync($"{SelectItem} WHERE completed_utc IS NULL", null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActionItem>> GetCompletedAsync(
        int limit, CancellationToken ct = default)
        => await LoadAsync(
                $"{SelectItem} WHERE completed_utc IS NOT NULL ORDER BY completed_utc DESC LIMIT @limit",
                new { limit }, ct)
            .ConfigureAwait(false);

    public async Task<ActionItem?> GetByMessageIdAsync(
        string internetMessageId, CancellationToken ct = default)
    {
        var items = await LoadAsync(
            $"{SelectItem} WHERE internet_message_id = @mid",
            new { mid = internetMessageId }, ct).ConfigureAwait(false);

        return items.FirstOrDefault();
    }

    /// <summary>
    /// Loads items plus their children in three queries rather than N+1, then
    /// stitches them together in memory.
    /// </summary>
    private async Task<IReadOnlyList<ActionItem>> LoadAsync(
        string sql, object? args, CancellationToken ct)
    {
        await using var conn = _db.Open();

        var items = (await conn.QueryAsync<ActionItem>(
            new CommandDefinition(sql, args, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        if (items.Count == 0) return items;

        var ids = items.Select(i => i.Id).ToArray();

        var blockers = await conn.QueryAsync<BlockingTask>(new CommandDefinition("""
            SELECT id AS Id, action_item_id AS ActionItemId, description AS Description,
                   waiting_on AS WaitingOn, due_utc AS DueUtc, created_utc AS CreatedUtc,
                   resolved_utc AS ResolvedUtc
            FROM blocking_tasks WHERE action_item_id IN @ids
            ORDER BY resolved_utc IS NOT NULL, created_utc
            """, new { ids }, cancellationToken: ct)).ConfigureAwait(false);

        var assignments = await conn.QueryAsync<Assignment>(new CommandDefinition("""
            SELECT id AS Id, action_item_id AS ActionItemId, person_name AS PersonName,
                   person_email AS PersonEmail, task AS Task, due_utc AS DueUtc,
                   created_utc AS CreatedUtc, done_utc AS DoneUtc, notified_utc AS NotifiedUtc
            FROM assignments WHERE action_item_id IN @ids
            ORDER BY done_utc IS NOT NULL, created_utc
            """, new { ids }, cancellationToken: ct)).ConfigureAwait(false);

        var blockersById = blockers.GroupBy(b => b.ActionItemId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var assignmentsById = assignments.GroupBy(a => a.ActionItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            item.Blockers = blockersById.GetValueOrDefault(item.Id) ?? new List<BlockingTask>();
            item.Assignments = assignmentsById.GetValueOrDefault(item.Id) ?? new List<Assignment>();
        }

        return items;
    }

    public async Task<ActionItem> UpsertAsync(ActionItem item, CancellationToken ct = default)
    {
        await using var conn = _db.Open();

        if (item.CreatedUtc == default) item.CreatedUtc = _clock.UtcNow;

        // Re-flagging a mail that is already tracked must not wipe the notes the
        // user has built up, so the update list is deliberately narrow.
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO action_items
                (internet_message_id, entry_id, store_id, subject, sender_name,
                 sender_address, received_utc, created_utc, completed_utc, priority, notes)
            VALUES
                (@InternetMessageId, @EntryId, @StoreId, @Subject, @SenderName,
                 @SenderAddress, @ReceivedUtc, @CreatedUtc, @CompletedUtc, @Priority, @Notes)
            ON CONFLICT(internet_message_id) DO UPDATE SET
                entry_id      = excluded.entry_id,
                store_id      = excluded.store_id,
                subject       = excluded.subject,
                sender_name   = excluded.sender_name,
                sender_address= excluded.sender_address,
                completed_utc = NULL,
                stage         = CASE WHEN stage = 3 THEN 0 ELSE stage END
            RETURNING id;
            """, item, cancellationToken: ct)).ConfigureAwait(false);

        item.Id = id;
        return item;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM action_items WHERE id = @id", new { id }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task SetCompletedAsync(long id, bool complete, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            // Completion and the Done column are one fact, so they move together.
            // Reopening lands in Doing: it was being worked on before.
            "UPDATE action_items SET completed_utc = @when, stage = @stage WHERE id = @id",
            new { id, when = complete ? (DateTimeOffset?)_clock.UtcNow : null, stage = complete ? 3 : 1 },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpdateStageAsync(long id, ActionStage stage, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE action_items
            SET stage = @stage,
                completed_utc = CASE WHEN @stage = 3 THEN COALESCE(completed_utc, @now) ELSE NULL END
            WHERE id = @id
            """, new { id, stage = (int)stage, now = _clock.UtcNow }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpdateDueAsync(long id, DateTimeOffset? dueUtc, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE action_items SET due_utc = @dueUtc WHERE id = @id",
            new { id, dueUtc }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpdateNotesAsync(long id, string notes, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE action_items SET notes = @notes WHERE id = @id",
            new { id, notes }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpdatePriorityAsync(
        long id, ActionPriority priority, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE action_items SET priority = @priority WHERE id = @id",
            new { id, priority = (int)priority }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<BlockingTask> AddBlockerAsync(
        BlockingTask blocker, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        if (blocker.CreatedUtc == default) blocker.CreatedUtc = _clock.UtcNow;

        blocker.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO blocking_tasks
                (action_item_id, description, waiting_on, due_utc, created_utc, resolved_utc)
            VALUES (@ActionItemId, @Description, @WaitingOn, @DueUtc, @CreatedUtc, @ResolvedUtc)
            RETURNING id;
            """, blocker, cancellationToken: ct)).ConfigureAwait(false);

        return blocker;
    }

    public async Task SetBlockerResolvedAsync(
        long blockerId, bool resolved, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE blocking_tasks SET resolved_utc = @when WHERE id = @id",
            new { id = blockerId, when = resolved ? (DateTimeOffset?)_clock.UtcNow : null },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteBlockerAsync(long blockerId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM blocking_tasks WHERE id = @id",
            new { id = blockerId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Assignment> AddAssignmentAsync(
        Assignment assignment, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        if (assignment.CreatedUtc == default) assignment.CreatedUtc = _clock.UtcNow;

        assignment.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO assignments
                (action_item_id, person_name, person_email, task, due_utc,
                 created_utc, done_utc, notified_utc)
            VALUES (@ActionItemId, @PersonName, @PersonEmail, @Task, @DueUtc,
                    @CreatedUtc, @DoneUtc, @NotifiedUtc)
            RETURNING id;
            """, assignment, cancellationToken: ct)).ConfigureAwait(false);

        return assignment;
    }

    public async Task SetAssignmentDoneAsync(
        long assignmentId, bool done, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE assignments SET done_utc = @when WHERE id = @id",
            new { id = assignmentId, when = done ? (DateTimeOffset?)_clock.UtcNow : null },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task MarkAssignmentDraftedAsync(long assignmentId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE assignments SET notified_utc = @when WHERE id = @id",
            new { id = assignmentId, when = _clock.UtcNow }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task DeleteAssignmentAsync(long assignmentId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM assignments WHERE id = @id",
            new { id = assignmentId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpdateLocationAsync(
        string internetMessageId, string entryId, string storeId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE action_items SET entry_id = @entryId, store_id = @storeId
            WHERE internet_message_id = @mid
            """, new { mid = internetMessageId, entryId, storeId }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Name, string Email)>> GetKnownAssigneesAsync(
        CancellationToken ct = default)
    {
        await using var conn = _db.Open();

        var rows = await conn.QueryAsync<AssigneeRow>(new CommandDefinition("""
            SELECT person_name AS Name, person_email AS Email
            FROM assignments
            WHERE person_name <> ''
            GROUP BY LOWER(person_name), LOWER(person_email)
            ORDER BY MAX(created_utc) DESC
            LIMIT 200
            """, cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(r => (r.Name, r.Email)).ToList();
    }

    /// <summary>
    /// Dapper maps ValueTuples positionally, which silently breaks if the SELECT
    /// list is ever reordered. A named row type is mapped by column name.
    /// </summary>
    private sealed class AssigneeRow
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }
}
