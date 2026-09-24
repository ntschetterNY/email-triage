using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

public sealed record ScheduledSendOutcome(ScheduledSend Entry, ScheduledSendState State, string Note);

/// <summary>
/// Sends scheduled drafts when their time comes - or, for a follow-up, holds
/// it for review if someone replied in the meantime.
///
/// Outlook's own delayed delivery is not used on purpose: Exchange would send
/// the message on schedule even with this app closed, with no chance to check
/// for a reply first. The trade-off, which the UI must be honest about, is
/// that nothing is sent while the app is closed. A send that comes due while
/// it is closed is sent late on the next start, unless it is so late that it
/// is safer to hold it for review.
/// </summary>
public sealed class ScheduledSender : IAsyncDisposable
{
    private readonly IMailStore _store;
    private readonly IScheduledSendRepository _repo;
    private readonly IClock _clock;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _loop;

    /// <summary>Raised on the thread pool whenever a scheduled send is settled.</summary>
    public event EventHandler<ScheduledSendOutcome>? Settled;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Beyond this, a missed send is held for review rather than sent late.</summary>
    public TimeSpan LateLimit { get; init; } = TimeSpan.FromHours(12);

    public int MaxFailures { get; init; } = 5;

    public ScheduledSender(IMailStore store, IScheduledSendRepository repo, IClock clock)
    {
        _store = store;
        _repo = repo;
        _clock = clock;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { await ProcessDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { /* reported per entry; never kill the loop */ }
        }
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Settles every due entry. Serialised, so a draft is never sent twice.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        if (!_store.IsConnected) return 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var due = await _repo.GetDueAsync(_clock.UtcNow, ct).ConfigureAwait(false);
            foreach (var entry in due)
            {
                ct.ThrowIfCancellationRequested();
                await SettleAsync(entry, ct).ConfigureAwait(false);
            }
            return due.Count;
        }
        finally { _gate.Release(); }
    }

    private async Task SettleAsync(ScheduledSend entry, CancellationToken ct)
    {
        try
        {
            switch (await _store.GetSavedDraftStateAsync(entry.Draft, ct).ConfigureAwait(false))
            {
                case SavedDraftState.AlreadySent:
                    await FinishAsync(entry, ScheduledSendState.Sent, "Already sent from Outlook", ct).ConfigureAwait(false);
                    return;
                case SavedDraftState.Deleted:
                case SavedDraftState.Missing:
                    await FinishAsync(entry, ScheduledSendState.Cancelled, "The draft was deleted", ct).ConfigureAwait(false);
                    return;
            }

            if (entry.HoldIfReplied
                && await _store.HasReplySinceAsync(entry.Draft, entry.CreatedUtc, ct).ConfigureAwait(false))
            {
                await HoldAsync(entry, "They replied - review before sending", ct).ConfigureAwait(false);
                return;
            }

            if (_clock.UtcNow - entry.SendAtUtc > LateLimit)
            {
                await HoldAsync(entry, "The app was closed when this was due - review before sending", ct).ConfigureAwait(false);
                return;
            }

            await _store.SendSavedDraftAsync(entry.Draft, ct).ConfigureAwait(false);
            await FinishAsync(entry, ScheduledSendState.Sent, "", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _repo.RecordFailureAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);

            if (entry.FailureCount + 1 >= MaxFailures)
            {
                // Stop retrying, and put the draft in front of the user rather
                // than letting it sit silently in Drafts.
                try { await _store.ShowSavedDraftAsync(entry.Draft, ct).ConfigureAwait(false); } catch { }
                await FinishAsync(entry, ScheduledSendState.Failed, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HoldAsync(ScheduledSend entry, string why, CancellationToken ct)
    {
        await _store.ShowSavedDraftAsync(entry.Draft, ct).ConfigureAwait(false);
        await FinishAsync(entry, ScheduledSendState.Held, why, ct).ConfigureAwait(false);
    }

    private async Task FinishAsync(ScheduledSend entry, ScheduledSendState state, string note, CancellationToken ct)
    {
        await _repo.CompleteAsync(entry.Id, state, note, ct).ConfigureAwait(false);
        Settled?.Invoke(this, new ScheduledSendOutcome(entry, state, note));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts.Dispose();
        _gate.Dispose();
    }
}
