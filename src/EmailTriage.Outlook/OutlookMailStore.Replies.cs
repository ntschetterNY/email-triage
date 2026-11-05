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

    /// <summary>
    /// The bookmark Word puts round the signature Outlook inserted - the one
    /// thing about it that does not change between Outlook versions.
    /// </summary>
    private const string SignatureBookmark = "_MailAutoSig";

    public Task<ReplyDraft> BuildReplyAsync(
        MailRef mail, ReplyScope scope, CancellationToken ct = default) =>
        RunAsync(() =>
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
                    Attachments = scope == ReplyScope.Forward
                        ? ReadAttachments((object)dyn, NullIfEmpty(ComUtil.Str(() => dyn.HTMLBody)))
                        : Array.Empty<MailAttachment>(),
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
        RunAsync(() =>
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
        RunAsync(() =>
        {
            EnsureConnected();

            var stored = PrepareOpenDraft(draft, bodyHtml, recipients);
            try { ((dynamic)stored).Send(); }
            finally { ComUtil.Release(stored); }
        }, ct);

    public Task<DraftRef> SaveDraftForLaterAsync(
        DraftRef draft, string bodyHtml, RecipientOverrides? recipients = null, CancellationToken ct = default) =>
        RunAsync(() =>
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

        if (recipients is { RemoveAttachments.Count: > 0 })
        {
            // Highest first, so each removal leaves the other positions as read.
            dynamic? attachments = null;
            try
            {
                attachments = item.Attachments;
                foreach (var index in recipients.RemoveAttachments.Distinct().OrderByDescending(i => i))
                    attachments!.Remove(index);
            }
            finally { ComUtil.Release(attachments); }
        }

        if (recipients is { Attachments.Count: > 0 })
        {
            // All checked before any is added, so a file that has gone fails
            // cleanly and a retry does not attach the others twice.
            var missing = recipients.Attachments.FirstOrDefault(f => !File.Exists(f));
            if (missing is not null)
                throw new InvalidOperationException(
                    $"\"{Path.GetFileName(missing)}\" is no longer there to attach. Remove it and add it again.");

            dynamic? attachments = null;
            try
            {
                attachments = item.Attachments;
                foreach (var file in recipients.Attachments)
                    ComUtil.Release(attachments!.Add(file));
            }
            finally { ComUtil.Release(attachments); }
        }

        // Before the draft is given up: a signature that cannot be put on
        // leaves it open, to send once Settings says otherwise.
        if (recipients?.Signature is { } signature) ApplySignature(item, signature);

        _openDrafts.TryRemove(draft.EntryId, out _);

        if (recipients is { InlineImages.Count: > 0 }) EmbedImages(item, recipients.InlineImages);

        // Put the new text above Outlook's quoted history rather than
        // replacing the body, so the thread stays intact.
        var existing = ComUtil.Str(() => item.HTMLBody);
        item.HTMLBody = bodyHtml + existing;

        return stored;
    }

    /// <summary>
    /// Takes the signature Outlook put on the draft off and puts the chosen
    /// one where it was, through Outlook's editor rather than the HTML: the
    /// editor is Word, which marks the signature it inserted with the
    /// <see cref="SignatureBookmark"/> bookmark, whereas in the HTML it is
    /// spread over spans and styles that differ by Outlook version. The
    /// chosen signature's own .htm goes in through Word too, so its pictures
    /// come with it as they do when Outlook inserts it. <see cref="PrepareOpenDraft"/>
    /// then reads the body back and puts the typed text above it all.
    /// </summary>
    private static void ApplySignature(dynamic item, MailSignature signature)
    {
        if (!signature.IsNone && !File.Exists(signature.HtmlPath))
            throw new InvalidOperationException(
                $"The signature \"{signature.Name}\" is no longer in Outlook. Pick another in Settings (Ctrl+,) › Signature.");

        dynamic? inspector = null, doc = null, bookmarks = null, range = null;
        try
        {
            // Asking for the inspector is also what makes Outlook put its
            // default signature on a new message, so there is one to take off.
            inspector = item.GetInspector;
            doc = inspector is null ? null : ComUtil.Try<object?>(() => inspector!.WordEditor);
            if (doc is null)
                throw new InvalidOperationException(
                    "Outlook's editor is not available for this message, so its signature could not be changed. " +
                    "Set the signature to \"Outlook's choice\" in Settings (Ctrl+,) to send it as Outlook built it.");

            bookmarks = doc.Bookmarks;
            if ((bool)bookmarks!.Exists(SignatureBookmark))
            {
                dynamic? bookmark = null;
                try
                {
                    bookmark = bookmarks.Item(SignatureBookmark);
                    range = bookmark!.Range;
                }
                finally { ComUtil.Release(bookmark); }

                // Leaves the range collapsed where the signature began.
                range!.Delete();
            }
            else
            {
                // Outlook put none on, so the chosen one goes where it would
                // have: the top, above any quoted history. The typed text is
                // put above it afterwards.
                range = doc.Range(0, 0);
            }

            if (!signature.IsNone) range!.InsertFile(signature.HtmlPath);
        }
        finally { ComUtil.ReleaseAll(range, bookmarks, doc, inspector); }
    }

    /// <summary>
    /// Attaches each pasted picture with the Content-ID its HTML uses, which
    /// is how Outlook shows an image in the body rather than as a file.
    /// </summary>
    private static void EmbedImages(dynamic item, IReadOnlyList<InlineImage> images)
    {
        var missing = images.FirstOrDefault(i => !File.Exists(i.Path));
        if (missing is not null)
            throw new InvalidOperationException("A picture in the message is no longer on disk. Paste it again.");

        dynamic? attachments = null;
        try
        {
            attachments = item.Attachments;
            foreach (var image in images)
            {
                dynamic? attachment = null;
                dynamic? accessor = null;
                try
                {
                    // olByValue = 1; the display name is what a client that
                    // cannot show it inline calls the file.
                    attachment = attachments!.Add(image.Path, 1, 0, Path.GetFileName(image.Path));
                    accessor = attachment!.PropertyAccessor;
                    accessor!.SetProperty(PropAttachContentId, image.ContentId);
                    // Best effort: marks it as part of the body, not a file to open.
                    ComUtil.Try<object?>(() => { accessor.SetProperty(PropAttachmentHidden, true); return null; });
                    ComUtil.Try<object?>(() => { accessor.SetProperty(PropAttachMimeTag, "image/png"); return null; });
                }
                finally
                {
                    ComUtil.Release(accessor);
                    ComUtil.Release(attachment);
                }
            }
        }
        finally { ComUtil.Release(attachments); }
    }

    public Task DiscardDraftAsync(DraftRef draft, CancellationToken ct = default) =>
        RunAsync(() =>
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
        RunAsync(() =>
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
