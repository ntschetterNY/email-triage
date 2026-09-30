using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Services;

namespace EmailTriage.Core.Data;

/// <summary>
/// Retires records nothing will show again, so the database does not grow
/// for ever: done cards past the policy's window (their waits go with them),
/// snoozes already returned to the inbox, and scheduled sends that have been
/// sent, held, cancelled or failed. Runs once at startup; anything still
/// open, pending or on the board is never touched.
/// </summary>
public sealed class RetentionSweep
{
    /// <summary>Enough deleted rows to be worth giving the file back to disk.</summary>
    public const int VacuumThreshold = 200;

    private readonly IActionItemRepository _actions;
    private readonly ISnoozeRepository _snoozes;
    private readonly IScheduledSendRepository _sends;
    private readonly Database _db;
    private readonly IClock _clock;

    public RetentionSweep(
        IActionItemRepository actions, ISnoozeRepository snoozes, IScheduledSendRepository sends,
        Database db, IClock clock)
    {
        _actions = actions;
        _snoozes = snoozes;
        _sends = sends;
        _db = db;
        _clock = clock;
    }

    public async Task<RetentionResult> RunAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var done = Retention.Cutoff(now, policy.DoneDays) is { } doneBefore
            ? await _actions.PurgeCompletedBeforeAsync(doneBefore, ct).ConfigureAwait(false)
            : 0;
        var snoozes = Retention.Cutoff(now, policy.SnoozeDays) is { } snoozeBefore
            ? await _snoozes.PurgeRestoredBeforeAsync(snoozeBefore, ct).ConfigureAwait(false)
            : 0;
        var sends = Retention.Cutoff(now, policy.ScheduledSendDays) is { } sendBefore
            ? await _sends.PurgeSettledBeforeAsync(sendBefore, ct).ConfigureAwait(false)
            : 0;

        var vacuum = done + snoozes + sends >= VacuumThreshold;
        if (vacuum) _db.Vacuum();

        return new RetentionResult(done, snoozes, sends, vacuum);
    }
}
