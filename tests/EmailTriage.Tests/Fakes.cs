using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 3, 11, 10, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => UtcNow;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// An in-memory stand-in for Outlook. Only implements what the tests exercise;
/// everything else throws loudly rather than silently returning empty.
/// </summary>
public sealed class FakeMailStore : IMailStore
{
    public bool IsConnected { get; private set; }
    public event EventHandler? InboxChanged;

    public List<FolderNode> Folders { get; } = new();
    public Dictionary<string, MailRef> ByMessageId { get; } = new();
    public List<(MailRef Mail, FolderRef Target)> Moves { get; } = new();

    public FolderRef Inbox { get; set; } = new("inbox", "store", "Mailbox\\Inbox");

    /// <summary>When set, the next MoveAsync throws this instead of succeeding.</summary>
    public Exception? NextMoveFailure { get; set; }

    public int MoveCount => Moves.Count;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public void RaiseInboxChanged() => InboxChanged?.Invoke(this, EventArgs.Empty);

    public Task<FolderRef> GetInboxAsync(CancellationToken ct = default) => Task.FromResult(Inbox);

    public Task<IReadOnlyList<FolderNode>> GetFolderIndexAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FolderNode>>(Folders);

    public Task<MailRef> MoveAsync(MailRef mail, FolderRef target, CancellationToken ct = default)
    {
        if (NextMoveFailure is { } failure)
        {
            NextMoveFailure = null;
            throw failure;
        }

        Moves.Add((mail, target));

        // Mirrors the real store: a move mints a new EntryId.
        return Task.FromResult(new MailRef($"{mail.EntryId}-moved", target.StoreId));
    }

    public Task<MailRef?> FindByMessageIdAsync(
        string internetMessageId, FolderRef? searchFolder, CancellationToken ct = default)
        => Task.FromResult<MailRef?>(
            ByMessageId.TryGetValue(internetMessageId, out var found) ? found : null);

    public Task<FolderRef> EnsureFolderPathAsync(string relativePath, CancellationToken ct = default)
        => Task.FromResult(new FolderRef("snoozed", "store", $"Mailbox\\{relativePath}"));

    public Task SetReadAsync(MailRef mail, bool read, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetCategoryAsync(MailRef mail, string category, bool on, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<FolderNode> CreateFolderAsync(
        FolderRef parent, string name, CancellationToken ct = default)
    {
        var node = new FolderNode
        {
            Ref = new FolderRef(name, "store", $"Mailbox\\{name}"),
            Name = name,
            Path = $"Mailbox\\{name}",
            Depth = 1,
            StoreName = "store",
        };
        Folders.Add(node);
        return Task.FromResult(node);
    }

    public Task<IReadOnlyList<MailSummary>> GetMailAsync(
        FolderRef folder, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MailSummary>>(Array.Empty<MailSummary>());

    public Task<MailBody> GetBodyAsync(MailRef mail, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<ReplyDraft> BuildReplyAsync(
        MailRef mail, ReplyScope scope, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<ReplyDraft> BuildNewMailAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task SendReplyAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by these tests.");

    // Saved drafts, for scheduled sends.
    public Dictionary<string, SavedDraftState> SavedDrafts { get; } = new();
    public HashSet<string> RepliedTo { get; } = new();
    public List<string> SentDrafts { get; } = new();
    public List<string> ShownDrafts { get; } = new();
    public Exception? NextSendFailure { get; set; }

    public Task<DraftRef> SaveDraftForLaterAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default)
    {
        var saved = new DraftRef($"saved-{draft.EntryId}", "store");
        SavedDrafts[saved.EntryId] = SavedDraftState.Waiting;
        return Task.FromResult(saved);
    }

    public Task<SavedDraftState> GetSavedDraftStateAsync(DraftRef saved, CancellationToken ct = default)
        => Task.FromResult(SavedDrafts.GetValueOrDefault(saved.EntryId, SavedDraftState.Missing));

    public Task SendSavedDraftAsync(DraftRef saved, CancellationToken ct = default)
    {
        if (NextSendFailure is { } fail) { NextSendFailure = null; throw fail; }
        SentDrafts.Add(saved.EntryId);
        SavedDrafts[saved.EntryId] = SavedDraftState.AlreadySent;
        return Task.CompletedTask;
    }

    public Task ShowItemAsync(MailRef mail, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MailSummary>> GetConversationAsync(MailRef mail, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MailSummary>>(Array.Empty<MailSummary>());

    public Task ShowSavedDraftAsync(DraftRef saved, CancellationToken ct = default)
    {
        ShownDrafts.Add(saved.EntryId);
        return Task.CompletedTask;
    }

    public Task<bool> HasReplySinceAsync(DraftRef saved, DateTimeOffset sinceUtc, CancellationToken ct = default)
        => Task.FromResult(RepliedTo.Contains(saved.EntryId));

    public Task<FolderRef> GetSentItemsAsync(CancellationToken ct = default)
        => Task.FromResult(new FolderRef("sent", "store", "Mailbox\\Sent Items"));

    public Task<string> SaveAttachmentAsync(MailRef mail, int index, CancellationToken ct = default)
        => throw new NotSupportedException("Not exercised by these tests.");

    public List<ContactEntry> FrequentContacts { get; } = new();
    public List<ContactEntry> Directory { get; } = new();
    public int DirectoryReads { get; private set; }

    public Task<IReadOnlyList<ContactEntry>> GetFrequentContactsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContactEntry>>(FrequentContacts);

    public Task<AddressBookBatch> GetAddressBookBatchAsync(int start, int count, CancellationToken ct = default)
    {
        DirectoryReads++;
        return Task.FromResult(new AddressBookBatch(Directory.Skip(start).Take(count).ToList(), Directory.Count));
    }

    public Task DiscardDraftAsync(DraftRef draft, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<DraftRef> CreateAndShowDraftAsync(
        IReadOnlyList<string> to, string subject, string bodyHtml, CancellationToken ct = default)
        => Task.FromResult(new DraftRef("draft", "store"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Folder usage backed by a plain dictionary.</summary>
public sealed class FakeFolderUsage : IFolderUsageRepository
{
    private readonly Dictionary<string, double> _scores = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(string path, double score) => _scores[path] = score;

    public Task RecordUseAsync(string folderPath, CancellationToken ct = default)
    {
        _scores[folderPath] = _scores.GetValueOrDefault(folderPath) + 1;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, double>> GetScoresAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, double>>(_scores);
}
