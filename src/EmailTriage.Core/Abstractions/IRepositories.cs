using EmailTriage.Core.Models;

namespace EmailTriage.Core.Abstractions;

public interface IActionItemRepository
{
    Task<IReadOnlyList<ActionItem>> GetOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ActionItem>> GetCompletedAsync(int limit, CancellationToken ct = default);
    Task<ActionItem?> GetByMessageIdAsync(string internetMessageId, CancellationToken ct = default);
    Task<ActionItem> UpsertAsync(ActionItem item, CancellationToken ct = default);

    /// <summary>
    /// Flags a mail with everything the capture popup asked, in one
    /// transaction: the item (kept if already tracked), its title, notes,
    /// priority and due date, and a blocker or hand-off when someone was
    /// named. Returns the item as it now stands, waits included.
    /// </summary>
    Task<ActionItem> CaptureAsync(ActionItem item, CaptureRequest request, CancellationToken ct = default);

    /// <summary>
    /// Puts an item back exactly as a snapshot had it - fields, stage, and
    /// only the waits it held then - for undoing an edit.
    /// </summary>
    Task RestoreAsync(ActionItem snapshot, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Removes items finished before <paramref name="cutoffUtc"/>, waits and all. Returns how many.</summary>
    Task<int> PurgeCompletedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);

    Task SetCompletedAsync(long id, bool complete, CancellationToken ct = default);
    Task UpdateNotesAsync(long id, string notes, CancellationToken ct = default);
    Task UpdatePriorityAsync(long id, ActionPriority priority, CancellationToken ct = default);

    /// <summary>Moves an item on the board. Done also marks it complete; any other stage reopens it.</summary>
    Task UpdateStageAsync(long id, ActionStage stage, CancellationToken ct = default);

    Task UpdateDueAsync(long id, DateTimeOffset? dueUtc, CancellationToken ct = default);

    /// <summary>Stamps that a follow-up chase was drafted, restarting the staleness clock.</summary>
    Task MarkFollowedUpAsync(long id, CancellationToken ct = default);

    Task<BlockingTask> AddBlockerAsync(BlockingTask blocker, CancellationToken ct = default);
    Task SetBlockerResolvedAsync(long blockerId, bool resolved, CancellationToken ct = default);
    Task DeleteBlockerAsync(long blockerId, CancellationToken ct = default);

    Task<Assignment> AddAssignmentAsync(Assignment assignment, CancellationToken ct = default);
    Task SetAssignmentDoneAsync(long assignmentId, bool done, CancellationToken ct = default);
    Task MarkAssignmentDraftedAsync(long assignmentId, CancellationToken ct = default);
    Task DeleteAssignmentAsync(long assignmentId, CancellationToken ct = default);

    /// <summary>Refreshes the cached location after a move, keyed by Message-ID.</summary>
    Task UpdateLocationAsync(
        string internetMessageId, string entryId, string storeId, CancellationToken ct = default);

    /// <summary>
    /// Points an item at the mail it was waiting for: a message just sent has
    /// no Message-ID until its copy lands in Sent Items, so it is filed under
    /// a placeholder and repointed here once found.
    /// </summary>
    Task ReplaceMessageIdAsync(
        long id, string internetMessageId, string entryId, string storeId, CancellationToken ct = default);

    /// <summary>People previously assigned work, most recent first, for autocomplete.</summary>
    Task<IReadOnlyList<(string Name, string Email)>> GetKnownAssigneesAsync(
        CancellationToken ct = default);
}

public interface IScheduledSendRepository
{
    Task<ScheduledSend> AddAsync(ScheduledSend entry, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledSend>> GetPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledSend>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default);
    Task CompleteAsync(long id, ScheduledSendState state, string note, CancellationToken ct = default);
    Task RecordFailureAsync(long id, string error, CancellationToken ct = default);

    /// <summary>Removes sends that were settled (sent, held, cancelled, failed) before the cutoff. Returns how many.</summary>
    Task<int> PurgeSettledBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}

public interface ISnoozeRepository
{
    Task<SnoozeEntry> AddAsync(SnoozeEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<SnoozeEntry>> GetPendingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SnoozeEntry>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default);
    Task MarkRestoredAsync(long id, CancellationToken ct = default);
    Task RecordFailureAsync(long id, string error, CancellationToken ct = default);
    Task CancelAsync(long id, CancellationToken ct = default);

    /// <summary>Removes snoozes returned to the inbox before the cutoff. Returns how many.</summary>
    Task<int> PurgeRestoredBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}

/// <summary>
/// Tracks which folders the user actually files into, so the move palette can
/// rank a folder they use daily above one they have never touched.
/// </summary>
public interface IFolderUsageRepository
{
    Task RecordUseAsync(string folderPath, CancellationToken ct = default);

    /// <summary>Folder path to usage score. Higher is more used.</summary>
    Task<IReadOnlyDictionary<string, double>> GetScoresAsync(CancellationToken ct = default);
}
