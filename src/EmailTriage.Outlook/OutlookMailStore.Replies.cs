using System.Collections.Concurrent;
using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    /// <summary>
    /// Reply drafts Outlook has built but not yet sent. They are held open
    /// because Outlook composes the quoted history, signature and headers for
    /// us; recreating that by hand is how replies end up looking wrong.
    /// Keyed by a token rather than EntryID, since an unsaved reply has none.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _openDrafts = new();

    public Task<ReplyDraft> BuildReplyAsync(
        MailRef mail, ReplyScope scope, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? source = null;
            object? reply = null;

            try
            {
                source = GetItem(mail);

                reply = scope == ReplyScope.All
                    ? source!.ReplyAll()
                    : source!.Reply();

                if (reply is null)
                    throw new InvalidOperationException("Outlook could not create the reply.");

                var dyn = (dynamic)reply;
                var (to, cc) = ReadRecipients((object)dyn);

                var token = Guid.NewGuid().ToString("N");
                _openDrafts[token] = reply;

                // Hand back a token in EntryId's place: the draft is unsaved and
                // genuinely has no EntryID until it is sent or saved.
                var draft = new ReplyDraft
                {
                    Ref = new DraftRef(token, ""),
                    Scope = scope,
                    Subject = ComUtil.Str(() => dyn.Subject),
                    To = to,
                    Cc = cc,
                    InReplyTo = mail,
                };

                reply = null; // ownership transferred to _openDrafts
                return draft;
            }
            finally
            {
                ComUtil.Release(reply);
                ComUtil.Release(source);
            }
        }, ct);

    public Task SendReplyAsync(DraftRef draft, string bodyHtml, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            if (!_openDrafts.TryRemove(draft.EntryId, out var stored))
                throw new InvalidOperationException(
                    "That reply is no longer open. It may have been sent or discarded already.");

            try
            {
                var reply = (dynamic)stored;

                // Put the new text above Outlook's quoted history rather than
                // replacing the body, so the thread stays intact.
                var existing = ComUtil.Str(() => reply.HTMLBody);
                reply.HTMLBody = bodyHtml + existing;

                reply.Send();
            }
            finally { ComUtil.Release(stored); }
        }, ct);

    public Task DiscardDraftAsync(DraftRef draft, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            if (!_openDrafts.TryRemove(draft.EntryId, out var stored)) return;

            try
            {
                // Close without saving; olDiscard = 1.
                ComUtil.Try<object?>(() => { ((dynamic)stored).Close(1); return null; });
            }
            finally { ComUtil.Release(stored); }
        }, ct);

    public Task<DraftRef> CreateAndShowDraftAsync(
        IReadOnlyList<string> to, string subject, string bodyHtml, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? mail = null;
            try
            {
                // olMailItem = 0
                mail = _app!.CreateItem(0);

                mail!.To = string.Join("; ", to.Where(a => !string.IsNullOrWhiteSpace(a)));
                mail.Subject = subject;
                mail.HTMLBody = bodyHtml;
                mail.Save();

                var entryId = ComUtil.Str(() => mail!.EntryID);
                var storeId = ComUtil.Str(() => mail!.Parent.StoreID);

                // Shown, never sent: the user reviews and sends it themselves.
                mail.Display(false);

                return new DraftRef(entryId, storeId);
            }
            finally { ComUtil.Release(mail); }
        }, ct);

    /// <summary>
    /// Releases any replies the user opened but never sent. Called on shutdown
    /// so Outlook is not left holding orphaned compose windows.
    /// </summary>
    private void ReleaseOpenDrafts()
    {
        foreach (var key in _openDrafts.Keys.ToList())
        {
            if (_openDrafts.TryRemove(key, out var draft))
                ComUtil.Release(draft);
        }
    }
}
