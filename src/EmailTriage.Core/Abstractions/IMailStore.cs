using EmailTriage.Core.Models;

namespace EmailTriage.Core.Abstractions;

/// <summary>
/// Everything the app needs from the mail backend. The Outlook implementation
/// marshals every call onto a single STA thread; callers may treat this as
/// ordinary async and must not assume any particular thread on return.
/// </summary>
public interface IMailStore : IAsyncDisposable
{
    /// <summary>Attaches to a running Outlook, starting it if necessary.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>True once <see cref="ConnectAsync"/> has succeeded.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when Outlook reports new mail, so the list can refresh.</summary>
    event EventHandler? NewMailArrived;

    Task<FolderRef> GetInboxAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailSummary>> GetMailAsync(
        FolderRef folder, int max, CancellationToken ct = default);

    Task<MailBody> GetBodyAsync(MailRef mail, CancellationToken ct = default);

    /// <summary>Flattened index of every mail folder across every open store.</summary>
    Task<IReadOnlyList<FolderNode>> GetFolderIndexAsync(CancellationToken ct = default);

    Task<FolderNode> CreateFolderAsync(
        FolderRef parent, string name, CancellationToken ct = default);

    /// <summary>
    /// Moves an item and returns its new location: the EntryId changes, so the
    /// caller must replace any reference it was holding with this result.
    /// </summary>
    Task<MailRef> MoveAsync(MailRef mail, FolderRef target, CancellationToken ct = default);

    Task SetCategoryAsync(MailRef mail, string category, bool on, CancellationToken ct = default);

    Task SetReadAsync(MailRef mail, bool read, CancellationToken ct = default);

    Task<ReplyDraft> BuildReplyAsync(
        MailRef mail, ReplyScope scope, CancellationToken ct = default);

    /// <summary>
    /// Prepends the user's text above the quoted history and sends. The draft is
    /// released afterwards and its <see cref="DraftRef"/> becomes invalid.
    /// </summary>
    Task SendReplyAsync(DraftRef draft, string bodyHtml, CancellationToken ct = default);

    /// <summary>Abandons a reply the user backed out of.</summary>
    Task DiscardDraftAsync(DraftRef draft, CancellationToken ct = default);

    /// <summary>Creates an unsent mail and shows it in Outlook for review.</summary>
    Task<DraftRef> CreateAndShowDraftAsync(
        IReadOnlyList<string> to, string subject, string bodyHtml, CancellationToken ct = default);

    /// <summary>
    /// Re-finds an item by Message-ID after its EntryId has gone stale.
    /// <paramref name="searchFolder"/> narrows the scan; null searches the
    /// default store. Returns null when the mail no longer exists.
    /// </summary>
    Task<MailRef?> FindByMessageIdAsync(
        string internetMessageId, FolderRef? searchFolder, CancellationToken ct = default);

    /// <summary>
    /// Returns the folder at <paramref name="relativePath"/> under the default
    /// store root, creating each missing segment. Used for the snooze folder.
    /// </summary>
    Task<FolderRef> EnsureFolderPathAsync(string relativePath, CancellationToken ct = default);
}
