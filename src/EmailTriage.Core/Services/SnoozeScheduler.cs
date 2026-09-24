using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

public sealed record SnoozeRestored(SnoozeEntry Entry, MailRef NewLocation);

/// <summary>
/// Returns snoozed mail to the Inbox when its time comes.
///
/// Outlook cannot do this itself, so the app owns it. Two consequences the UI
/// must surface honestly: nothing comes back while the app is closed, and a
/// mail the user moved by hand out of the holding folder has to be re-found by
/// Message-ID before it can be restored.
/// </summary>
public sealed class SnoozeScheduler : IAsyncDisposable
{
    private readonly IMailStore _store;
    private readonly ISnoozeRepository _repo;
    private readonly IClock _clock;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on the thread pool once an item has been put back.</summary>
    public event EventHandler<SnoozeRestored>? Restored;

    /// <summary>Raised when an item could not be returned; the entry stays pending.</summary>
    public event EventHandler<(SnoozeEntry Entry, string Error)>? RestoreFailed;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After this many consecutive failures the entry is given up on.</summary>
    public int MaxFailures { get; init; } = 5;

    /// <summary>Relative path of the holding folder created under the default store.</summary>
    public string SnoozeFolderPath { get; init; } = "Snoozed";

    public SnoozeScheduler(IMailStore store, ISnoozeRepository repo, IClock clock)
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
        // Sweep immediately: anything that came due while the app was closed
        // should land the moment the user opens it, not up to a poll later.
        try { await ProcessDueAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { /* surfaced per-entry below; never kill the loop */ }

        using var timer = new PeriodicTimer(PollInterval);
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            try { await ProcessDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Moves every due item back. Safe to call directly, e.g. from a manual refresh.
    /// </summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        if (!_store.IsConnected) return 0;

        var due = await _repo.GetDueAsync(_clock.UtcNow, ct).ConfigureAwait(false);
        if (due.Count == 0) return 0;

        int restored = 0;

        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var target = new FolderRef(
                    entry.OriginFolderEntryId, entry.OriginFolderStoreId, entry.OriginFolderPath);

                if (target.IsEmpty)
                    target = await _store.GetInboxAsync(ct).ConfigureAwait(false);

                var mail = new MailRef(entry.EntryId, entry.StoreId);

                // The EntryId may be stale - the user could have moved or read
                // the item in Outlook meanwhile. Fall back to the stable Message-ID.
                var resolved = await ResolveAsync(mail, entry, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    await _repo.RecordFailureAsync(
                        entry.Id, "Message no longer found - it may have been deleted.", ct)
                        .ConfigureAwait(false);
                    RestoreFailed?.Invoke(this, (entry, "Message no longer found."));
                    continue;
                }

                var moved = await _store.MoveAsync(resolved.Value, target, ct).ConfigureAwait(false);

                // Returning to the Inbox should feel like new mail arriving.
                await _store.SetReadAsync(moved, false, ct).ConfigureAwait(false);
                await _repo.MarkRestoredAsync(entry.Id, ct).ConfigureAwait(false);

                restored++;
                Restored?.Invoke(this, new SnoozeRestored(entry, moved));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await _repo.RecordFailureAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);

                if (entry.FailureCount + 1 >= MaxFailures)
                    await _repo.CancelAsync(entry.Id, ct).ConfigureAwait(false);

                RestoreFailed?.Invoke(this, (entry, ex.Message));
            }
        }

        return restored;
    }

    private async Task<MailRef?> ResolveAsync(
        MailRef candidate, SnoozeEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.InternetMessageId))
            return candidate.IsEmpty ? null : candidate;

        try
        {
            var snoozeFolder = await _store
                .EnsureFolderPathAsync(SnoozeFolderPath, ct).ConfigureAwait(false);

            var found = await _store
                .FindByMessageIdAsync(entry.InternetMessageId, snoozeFolder, ct)
                .ConfigureAwait(false);

            if (found is not null) return found;
        }
        catch
        {
            // Fall through to the cached EntryId.
        }

        return candidate.IsEmpty ? null : candidate;
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
    }
}
