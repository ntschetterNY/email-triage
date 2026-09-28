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

    /// <summary>
    /// Raised when anything in the Inbox changes - mail arriving, leaving, or
    /// being read or flagged, in this app or in Outlook itself - so the list
    /// can refresh. Fired on a background thread.
    /// </summary>
    event EventHandler? InboxChanged;

    Task<FolderRef> GetInboxAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailSummary>> GetMailAsync(
        FolderRef folder, int max, CancellationToken ct = default);

    Task<MailBody> GetBodyAsync(MailRef mail, CancellationToken ct = default);

    /// <summary>
    /// The To and CC lines of each message, with SMTP addresses, keyed by
    /// EntryId. Messages that cannot be opened are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, MailRecipients>> GetRecipientsAsync(
        IReadOnlyList<MailRef> mail, CancellationToken ct = default);

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
    /// An empty message held open like a reply, for the composer to fill in and
    /// send. Its <see cref="ReplyDraft.InReplyTo"/> is empty.
    /// </summary>
    Task<ReplyDraft> BuildNewMailAsync(CancellationToken ct = default);

    /// <summary>
    /// Prepends the user's text above the quoted history and sends. The draft is
    /// released afterwards and its <see cref="DraftRef"/> becomes invalid.
    /// Any line set in <paramref name="recipients"/> replaces what Outlook put
    /// there; if a recipient cannot be resolved the draft stays open and this
    /// throws.
    /// </summary>
    Task SendReplyAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default);

    /// <summary>
    /// Writes the user's text and recipients onto an open draft and saves it to
    /// Drafts instead of sending, returning a lasting reference to it. The open
    /// <paramref name="draft"/> token is released.
    /// </summary>
    Task<DraftRef> SaveDraftForLaterAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default);

    Task<SavedDraftState> GetSavedDraftStateAsync(DraftRef saved, CancellationToken ct = default);

    Task SendSavedDraftAsync(DraftRef saved, CancellationToken ct = default);

    /// <summary>
    /// Every sent or received message in the mail's conversation, wherever it
    /// is filed, newest first, with <see cref="MailSummary.IsSent"/> marking
    /// your own. At least the mail itself when the store has no conversations.
    /// </summary>
    Task<IReadOnlyList<MailSummary>> GetConversationAsync(MailRef mail, int max, CancellationToken ct = default);

    /// <summary>Opens any mail item in its own Outlook window.</summary>
    Task ShowItemAsync(MailRef mail, CancellationToken ct = default);

    /// <summary>Opens a saved draft in Outlook for the user to review.</summary>
    Task ShowSavedDraftAsync(DraftRef saved, CancellationToken ct = default);

    /// <summary>
    /// True when someone other than the user has written in the saved draft's
    /// conversation since <paramref name="sinceUtc"/>.
    /// </summary>
    Task<bool> HasReplySinceAsync(DraftRef saved, DateTimeOffset sinceUtc, CancellationToken ct = default);

    /// <summary>Sent Items of the default store, for showing your side of each conversation.</summary>
    Task<FolderRef> GetSentItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves one attachment to a local cache and returns its path, for opening
    /// in the program Windows associates with it.
    /// </summary>
    Task<string> SaveAttachmentAsync(MailRef mail, int index, CancellationToken ct = default);

    /// <summary>
    /// Contacts and frequent correspondents for recipient autocomplete.
    /// Entries with an empty address are name-only boosts from Sent Items.
    /// </summary>
    Task<IReadOnlyList<ContactEntry>> GetFrequentContactsAsync(CancellationToken ct = default);

    /// <summary>One slice of the company directory; empty when there is none.</summary>
    Task<AddressBookBatch> GetAddressBookBatchAsync(int start, int count, CancellationToken ct = default);

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
