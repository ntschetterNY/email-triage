using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Reads a little of what already sits in each folder - the newest few
/// messages' senders and subjects - so the move palette can guess folders
/// from day one rather than only after weeks of filing. Runs quietly after
/// the inbox is up, one folder at a time with a pause between, and remembers
/// which folders it has seen so each is sampled only every couple of weeks.
/// Everything is read and kept on this machine.
/// </summary>
public sealed class FolderStudy
{
    private readonly IMailStore _store;
    private readonly IFolderUsageRepository _usage;
    private readonly IClock _clock;

    /// <summary>
    /// Outlook's own folders, where what sits there says nothing about
    /// filing. Matched on the English names, as the index carries only paths.
    /// </summary>
    private static readonly HashSet<string> SkippedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deleted Items", "Junk Email", "Junk E-mail", "Outbox", "Drafts", "Sent Items",
        "Sync Issues", "Conversation History", "RSS Feeds", "RSS Subscriptions", "Search Folders",
        "Snoozed",
    };

    public FolderStudy(IMailStore store, IFolderUsageRepository usage, IClock clock)
    {
        _store = store;
        _usage = usage;
        _clock = clock;
    }

    /// <summary>How long a folder's sample is trusted before it is read again.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromDays(14);

    /// <summary>The newest messages read from each folder.</summary>
    public int SamplePerFolder { get; init; } = 40;

    /// <summary>Folders read in one run; the rest wait for the next launch.</summary>
    public int MaxFoldersPerRun { get; init; } = 300;

    /// <summary>Breathing room between folders, so Outlook stays responsive to triage.</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// True for a folder worth sampling: a filing destination, not the Inbox
    /// itself, a mailbox root, or one of Outlook's own folders.
    /// </summary>
    public static bool IsWorthStudying(FolderNode folder)
    {
        var relative = WithoutStoreRoot(folder.Path);
        if (relative.Length == 0) return false;

        var top = relative.Split('\\')[0];
        if (SkippedRoots.Contains(top)) return false;

        // Folders under the Inbox are filing targets; the Inbox itself is where mail waits.
        return !relative.Equals("Inbox", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Samples every folder in <paramref name="index"/> that is due, recording
    /// what was found. Returns how many folders were read. A folder that
    /// cannot be read is skipped and tried again next run.
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<FolderNode> index, CancellationToken ct = default)
    {
        var studied = await _usage.GetStudiedAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var due = index
            .Where(IsWorthStudying)
            .Where(f => !studied.TryGetValue(f.Path, out var at) || now - at >= Lifetime)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Depth)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFoldersPerRun)
            .ToList();

        var read = 0;
        foreach (var folder in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var mail = await _store.GetMailAsync(folder.Ref, SamplePerFolder, ct).ConfigureAwait(false);
                var features = mail
                    .Where(m => !m.IsSent)
                    .SelectMany(m => FolderGuesser.Features(m.Subject, m.SenderAddress))
                    .ToList();

                if (features.Count > 0)
                    await _usage.RecordEvidenceAsync(folder.Path, features, ct).ConfigureAwait(false);
                await _usage.MarkStudiedAsync(folder.Path, ct).ConfigureAwait(false);
                read++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* a folder that will not open is tried again next time */ }

            if (Pause > TimeSpan.Zero) await Task.Delay(Pause, ct).ConfigureAwait(false);
        }

        return read;
    }

    private static string WithoutStoreRoot(string path)
    {
        var idx = path.IndexOf('\\');
        return idx < 0 ? "" : path[(idx + 1)..];
    }
}
