using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    /// <summary>
    /// Every sent or received message in the mail's conversation, wherever it
    /// is filed, newest first. Unsent drafts are left out. Falls back to just
    /// the mail itself on stores without conversation support.
    /// </summary>
    public Task<IReadOnlyList<MailSummary>> GetConversationAsync(
        MailRef mail, int max, CancellationToken ct = default) =>
        _sta.InvokeAsync<IReadOnlyList<MailSummary>>(() =>
        {
            EnsureConnected();
            var me = MyAddresses();
            var found = new List<MailSummary>();

            dynamic? item = null, conversation = null, table = null;
            try
            {
                item = GetItem(mail);
                conversation = ComUtil.Try<object?>(() => item!.GetConversation());

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
                            if (entryId.Length == 0) continue;

                            if (ReadConversationMessage(new MailRef(entryId, mail.StoreId), me) is { } summary)
                                found.Add(summary);
                        }
                        finally { ComUtil.Release(row); }
                    }
                }

                if (found.Count == 0 && ReadConversationMessage(mail, me) is { } self) found.Add(self);

                return found
                    .GroupBy(m => m.Ref.EntryId)
                    .Select(g => g.First())
                    .OrderByDescending(m => m.ReceivedUtc)
                    .Take(Math.Max(1, max))
                    .ToList();
            }
            finally { ComUtil.ReleaseAll(table, conversation, item); }
        }, ct);

    private MailSummary? ReadConversationMessage(MailRef mail, HashSet<string> me)
    {
        dynamic? item = null;
        try
        {
            item = ComUtil.Try<object?>(() => GetItem(mail));
            if (item is null || !ComUtil.IsMailLike(ComUtil.Int(() => item!.Class))) return null;
            if (!ComUtil.Try(() => (bool)item!.Sent)) return null; // an unsent draft

            var storeId = ComUtil.Str(() => item!.Parent.StoreID);
            var summary = SummaryFromItem((object)item!, storeId.Length > 0 ? storeId : mail.StoreId);

            var mine = me.Contains(ComUtil.SenderSmtp((object)item!));
            return mine
                ? summary with { IsSent = true, ReceivedUtc = ComUtil.Date(() => item!.SentOn) is { } sent && sent != default ? sent : summary.ReceivedUtc }
                : summary;
        }
        finally { ComUtil.Release(item); }
    }
}
