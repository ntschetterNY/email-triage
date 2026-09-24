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

                reply = scope switch
                {
                    ReplyScope.All => source!.ReplyAll(),
                    ReplyScope.Forward => source!.Forward(),
                    _ => source!.Reply(),
                };

                if (reply is null)
                    throw new InvalidOperationException(
                        scope == ReplyScope.Forward
                            ? "Outlook could not create the forward."
                            : "Outlook could not create the reply.");

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

    public Task<ReplyDraft> BuildNewMailAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            // olMailItem = 0. Held open like a reply: nothing is saved to
            // Drafts unless it is scheduled, and discarding leaves no trace.
            var mail = (object?)_app!.CreateItem(0)
                ?? throw new InvalidOperationException("Outlook could not create a new message.");

            var token = Guid.NewGuid().ToString("N");
            _openDrafts[token] = mail;

            return new ReplyDraft
            {
                Ref = new DraftRef(token, ""),
                Scope = ReplyScope.New,
                Subject = "",
                To = Array.Empty<Recipient>(),
                Cc = Array.Empty<Recipient>(),
                InReplyTo = default,
            };
        }, ct);

    public Task SendReplyAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            var stored = PrepareOpenDraft(draft, bodyHtml, recipients);
            try { ((dynamic)stored).Send(); }
            finally { ComUtil.Release(stored); }
        }, ct);

    public Task<DraftRef> SaveDraftForLaterAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            var stored = PrepareOpenDraft(draft, bodyHtml, recipients);
            try
            {
                var item = (dynamic)stored;

                // Saving an unsent reply files it in Drafts, where it only now
                // gets an EntryID that lasts beyond this session.
                item.Save();
                return new DraftRef(
                    ComUtil.Str(() => item.EntryID),
                    ComUtil.Str(() => item.Parent.StoreID));
            }
            finally { ComUtil.Release(stored); }
        }, ct);

    /// <summary>
    /// Applies recipient edits and the user's text to an open draft and takes
    /// it out of <see cref="_openDrafts"/>; the caller must release it. If a
    /// recipient does not resolve, this throws and the draft stays open so the
    /// user can correct it.
    /// </summary>
    private object PrepareOpenDraft(DraftRef draft, string bodyHtml, RecipientOverrides? recipients)
    {
        if (!_openDrafts.TryGetValue(draft.EntryId, out var stored))
            throw new InvalidOperationException(
                "That reply is no longer open. It may have been sent or discarded already.");

        var item = (dynamic)stored;

        if (recipients?.Subject is { } subject) item.Subject = subject;

        if (recipients is { ChangesRecipients: true })
        {
            static string Join(IReadOnlyList<string> list) =>
                string.Join("; ", list.Where(a => !string.IsNullOrWhiteSpace(a)));

            if (recipients.To is { } to) item.To = Join(to);
            if (recipients.Cc is { } cc) item.CC = Join(cc);
            if (recipients.Bcc is { } bcc) item.BCC = Join(bcc);

            dynamic? list = null;
            try
            {
                list = item.Recipients;
                if (!(bool)list!.ResolveAll())
                    throw new InvalidOperationException(
                        "Outlook could not resolve every recipient. Check the To, Cc and Bcc lines.");
            }
            finally { ComUtil.Release(list); }
        }

        _openDrafts.TryRemove(draft.EntryId, out _);

        // Put the new text above Outlook's quoted history rather than
        // replacing the body, so the thread stays intact.
        var existing = ComUtil.Str(() => item.HTMLBody);
        item.HTMLBody = bodyHtml + existing;

        return stored;
    }

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
