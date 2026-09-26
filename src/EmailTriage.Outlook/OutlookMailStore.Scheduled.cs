using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    // olFolderDeletedItems = 3
    private const int FolderDeletedItems = 3;

    /// <summary>Only this many conversation items are inspected when looking for a reply.</summary>
    private const int ConversationScanLimit = 300;

    private HashSet<string>? _myAddresses;

    public Task<SavedDraftState> GetSavedDraftStateAsync(DraftRef saved, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null, parent = null, deleted = null;
            try
            {
                item = ComUtil.Try<object?>(() => GetItem(new MailRef(saved.EntryId, saved.StoreId)));
                if (item is null) return SavedDraftState.Missing;

                if (ComUtil.Try(() => (bool)item!.Sent)) return SavedDraftState.AlreadySent;

                parent = item!.Parent;
                deleted = _session!.GetDefaultFolder(FolderDeletedItems);
                if (ComUtil.Str(() => parent!.EntryID) == ComUtil.Str(() => deleted!.EntryID))
                    return SavedDraftState.Deleted;

                return SavedDraftState.Waiting;
            }
            finally { ComUtil.ReleaseAll(deleted, parent, item); }
        }, ct);

    public Task SendSavedDraftAsync(DraftRef saved, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();
            dynamic? item = null;
            try
            {
                item = GetItem(new MailRef(saved.EntryId, saved.StoreId));
                item.Send();
            }
            finally { ComUtil.Release(item); }
        }, ct);

    public Task ShowSavedDraftAsync(DraftRef saved, CancellationToken ct = default) =>
        ShowItemAsync(new MailRef(saved.EntryId, saved.StoreId), ct);

    public Task ShowItemAsync(MailRef mail, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();
            dynamic? item = null;
            try
            {
                item = GetItem(mail);
                item.Display(false);
            }
            finally { ComUtil.Release(item); }
        }, ct);

    /// <summary>
    /// Looks through the draft's conversation - every folder, so a reply a rule
    /// filed away still counts - for mail from someone else newer than
    /// <paramref name="sinceUtc"/>. Stores without conversation support fall
    /// back to matching the subject thread in the Inbox.
    /// </summary>
    public Task<bool> HasReplySinceAsync(DraftRef saved, DateTimeOffset sinceUtc, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            EnsureConnected();
            var me = MyAddresses();

            dynamic? draft = null, conversation = null, table = null;
            try
            {
                draft = GetItem(new MailRef(saved.EntryId, saved.StoreId));
                var draftId = saved.EntryId;

                conversation = ComUtil.Try<object?>(() => draft!.GetConversation());
                if (conversation is not null)
                {
                    table = conversation.GetTable();
                    for (int i = 0; i < ConversationScanLimit && !ComUtil.Try(() => (bool)table!.EndOfTable, true); i++)
                    {
                        dynamic? row = null;
                        try
                        {
                            row = table!.GetNextRow();
                            var entryId = ComUtil.Str(() => row!["EntryID"]);
                            if (entryId.Length == 0 || entryId == draftId) continue;

                            if (IsReplyFromSomeoneElse(entryId, saved.StoreId, sinceUtc, me)) return true;
                        }
                        finally { ComUtil.Release(row); }
                    }
                    return false;
                }

                return InboxHasReplyByTopic(ComUtil.Str(() => draft!.ConversationTopic), sinceUtc, me);
            }
            finally { ComUtil.ReleaseAll(table, conversation, draft); }
        }, ct);

    private bool IsReplyFromSomeoneElse(string entryId, string storeId, DateTimeOffset sinceUtc, HashSet<string> me)
    {
        dynamic? item = null;
        try
        {
            item = ComUtil.Try<object?>(() => GetItem(new MailRef(entryId, storeId)));
            if (item is null || ComUtil.Int(() => item!.Class) != ComUtil.OlMail) return false;
            if (!ComUtil.Try(() => (bool)item!.Sent)) return false; // someone else's draft? never mind

            var received = ComUtil.Date(() => item!.ReceivedTime);
            if (received <= sinceUtc) return false;

            var sender = ComUtil.SenderSmtp((object)item!);
            return sender.Length > 0 && !me.Contains(sender);
        }
        finally { ComUtil.Release(item); }
    }

    private bool InboxHasReplyByTopic(string topic, DateTimeOffset sinceUtc, HashSet<string> me)
    {
        if (topic.Length == 0) return false;

        dynamic? inbox = null, items = null, found = null;
        try
        {
            inbox = _session!.GetDefaultFolder(ComUtil.FolderInbox);
            items = inbox!.Items;

            // Jet filters take local wall-clock time; quotes are escaped by doubling.
            var since = sinceUtc.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var filter = $"[ConversationTopic] = '{topic.Replace("'", "''")}' AND [ReceivedTime] > '{since}'";
            found = items!.Restrict(filter);

            int count = ComUtil.Int(() => found!.Count);
            for (int i = 1; i <= Math.Min(count, ConversationScanLimit); i++)
            {
                dynamic? item = null;
                try
                {
                    item = found![i];
                    var sender = ComUtil.SenderSmtp((object)item!);
                    if (sender.Length > 0 && !me.Contains(sender)) return true;
                }
                finally { ComUtil.Release(item); }
            }
            return false;
        }
        catch { return false; }
        finally { ComUtil.ReleaseAll(found, items, inbox); }
    }

    /// <summary>Every SMTP address that means "me": each account, plus the signed-in user.</summary>
    private HashSet<string> MyAddresses()
    {
        if (_myAddresses is not null) return _myAddresses;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        dynamic? accounts = null;
        try
        {
            accounts = _session!.Accounts;
            int count = ComUtil.Int(() => accounts!.Count);
            for (int i = 1; i <= count; i++)
            {
                dynamic? account = null;
                try
                {
                    account = accounts![i];
                    var smtp = ComUtil.Str(() => account!.SmtpAddress);
                    if (smtp.Contains('@')) set.Add(smtp);
                }
                finally { ComUtil.Release(account); }
            }
        }
        catch { }
        finally { ComUtil.Release(accounts); }

        dynamic? user = null, entry = null, exchange = null;
        try
        {
            user = _session!.CurrentUser;
            entry = user!.AddressEntry;
            exchange = ComUtil.Try<object?>(() => entry!.GetExchangeUser());
            var smtp = exchange is null ? ComUtil.Str(() => entry!.Address) : ComUtil.Str(() => exchange!.PrimarySmtpAddress);
            if (smtp.Contains('@')) set.Add(smtp);
        }
        catch { }
        finally { ComUtil.ReleaseAll(exchange, entry, user); }

        return _myAddresses = set;
    }
}
