using System.Runtime.Versioning;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

/// <summary>
/// Talks to the classic Outlook desktop client over its COM object model, which
/// reads and writes the local .ost/.pst directly. No network calls, no Graph,
/// no app registration - but it does require Outlook to be installed, and the
/// "new Outlook" does not expose this interface at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore : IMailStore
{
    private readonly StaDispatcher _sta = new();

    // These live for the lifetime of the store and are only ever touched on the
    // dispatcher thread.
    private dynamic? _app;
    private dynamic? _session;

    private Timer? _pollTimer;
    private DateTimeOffset _lastSeenReceived = DateTimeOffset.MinValue;
    private int _lastSeenCount = -1;
    private int _pollInFlight;

    public bool IsConnected { get; private set; }

    public event EventHandler? InboxChanged;

    /// <summary>
    /// How often to re-check the Inbox as a backstop. Outlook's item events
    /// (see OutlookMailStore.Watch.cs) are what make the list live; they are
    /// known to drop events when many items arrive at once, so this catches
    /// whatever they miss.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    public Task ConnectAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            if (IsConnected) return;

            var progId = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException(
                    "Outlook is not installed, or the classic desktop client is missing. " +
                    "This app needs classic Outlook (the new Outlook does not expose COM).");

            // For Outlook this attaches to the running instance when there is
            // one, and starts it otherwise.
            _app = Activator.CreateInstance(progId)
                ?? throw new InvalidOperationException("Could not start Outlook.");

            _session = _app!.GetNamespace("MAPI");

            // Reuses the profile already signed in; does not prompt when Outlook
            // is running.
            try { _session!.Logon(Type.Missing, Type.Missing, false, false); }
            catch { /* already logged on */ }

            IsConnected = true;
            PruneInlineImages();
            StartWatching();
            StartPolling();
        }, ct);

    private void StartPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = new Timer(_ => PollForChanges(), null, PollInterval, PollInterval);
    }

    private void PollForChanges()
    {
        // Skip rather than queue up if a previous poll is still running.
        if (Interlocked.Exchange(ref _pollInFlight, 1) == 1) return;

        _ = _sta.InvokeAsync(() =>
        {
            try
            {
                if (!IsConnected) return;

                dynamic? inbox = null, items = null;
                try
                {
                    inbox = _session!.GetDefaultFolder(ComUtil.FolderInbox);
                    items = inbox!.Items;

                    int count = ComUtil.Int(() => items!.Count);
                    var newest = NewestReceived((object)items!);

                    // Any difference counts: a lower count is mail moved or
                    // deleted in Outlook, not just new mail arriving.
                    bool changed = _lastSeenCount >= 0
                                && (count != _lastSeenCount || newest != _lastSeenReceived);

                    _lastSeenCount = count;
                    _lastSeenReceived = newest;

                    if (changed) SignalInboxChanged();
                }
                finally
                {
                    ComUtil.ReleaseAll(items, inbox);
                }
            }
            catch
            {
                // Outlook may be mid-restart or showing a modal dialog. The next
                // tick will pick things up.
            }
            finally
            {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        });
    }

    private static DateTimeOffset NewestReceived(object itemsObj)
    {
        dynamic items = itemsObj;
        dynamic? first = null;
        try
        {
            items.Sort("[ReceivedTime]", true);
            first = items.GetFirst();
            return first is null ? DateTimeOffset.MinValue : ComUtil.Date(() => first!.ReceivedTime);
        }
        catch { return DateTimeOffset.MinValue; }
        finally { ComUtil.Release(first); }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _session is null)
            throw new InvalidOperationException("Not connected to Outlook. Call ConnectAsync first.");
    }

    public Task<FolderRef> GetInboxAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();
            dynamic? inbox = null;
            try
            {
                inbox = _session!.GetDefaultFolder(ComUtil.FolderInbox);
                return ToFolderRef((object)inbox!);
            }
            finally { ComUtil.Release(inbox); }
        }, ct);

    private static FolderRef ToFolderRef(object folderObj)
    {
        dynamic folder = folderObj;
        return new FolderRef(
            ComUtil.Str(() => folder.EntryID),
            ComUtil.Str(() => folder.StoreID),
            ComUtil.Str(() => folder.FolderPath));
    }

    public Task<IReadOnlyList<MailSummary>> GetMailAsync(
        FolderRef folder, int max, CancellationToken ct = default) =>
        _sta.InvokeAsync<IReadOnlyList<MailSummary>>(() =>
        {
            EnsureConnected();

            dynamic? f = null;
            try
            {
                f = _session!.GetFolderFromID(folder.EntryId, folder.StoreId);

                // A MAPI table reads many properties in one pass and is an order
                // of magnitude faster than walking Items and touching each item.
                try { return ReadViaTable((object)f!, folder.StoreId, max); }
                catch { return ReadViaItems((object)f!, folder.StoreId, max); }
            }
            finally { ComUtil.Release(f); }
        }, ct);

    private const string PropHasAttach = "http://schemas.microsoft.com/mapi/proptag/0x0E1B000B";

    /// <summary>PR_CONVERSATION_ID: shared by every message in a thread, sent or received.</summary>
    private const string PropConversationId = "http://schemas.microsoft.com/mapi/proptag/0x30130102";

    /// <summary>Tables hand binary columns back as byte arrays; some stores give a hex string.</summary>
    private static string ConversationKeyFrom(object? value) => value switch
    {
        byte[] bytes when bytes.Length > 0 => Convert.ToHexString(bytes),
        string s => s.Trim(),
        _ => "",
    };

    public Task<FolderRef> GetSentItemsAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();
            dynamic? sent = null;
            try
            {
                sent = _session!.GetDefaultFolder(FolderSentMail);
                return ToFolderRef((object)sent!);
            }
            finally { ComUtil.Release(sent); }
        }, ct);

    private static IReadOnlyList<MailSummary> ReadViaTable(object folderObj, string storeId, int max)
    {
        dynamic folder = folderObj;
        dynamic? table = null, columns = null;
        var results = new List<MailSummary>(Math.Min(max, 256));

        try
        {
            table = folder.GetTable(Type.Missing, Type.Missing);
            columns = table!.Columns;

            columns!.RemoveAll();
            columns.Add("EntryID");
            columns.Add("Subject");
            columns.Add("SenderName");
            columns.Add("SenderEmailAddress");
            columns.Add("ReceivedTime");
            columns.Add("UnRead");
            columns.Add("Categories");
            columns.Add("MessageClass");
            columns.Add(ComUtil.PropInternetMessageId);
            columns.Add(PropHasAttach);
            columns.Add(PropConversationId);

            table.Sort("ReceivedTime", 2 /* olDescending */);

            while (results.Count < max && !(bool)table.EndOfTable)
            {
                dynamic? row = null;
                try
                {
                    row = table.GetNextRow();
                    if (row is null) break;

                    var messageClass = ComUtil.Str(() => row!["MessageClass"]);
                    if (!messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(new MailSummary
                    {
                        Ref = new MailRef(ComUtil.Str(() => row!["EntryID"]), storeId),
                        InternetMessageId = ComUtil.Str(() => row![ComUtil.PropInternetMessageId]),
                        Subject = ComUtil.Str(() => row!["Subject"]),
                        SenderName = ComUtil.Str(() => row!["SenderName"]),
                        SenderAddress = ComUtil.Str(() => row!["SenderEmailAddress"]),
                        ReceivedUtc = ComUtil.Date(() => row!["ReceivedTime"]),
                        IsUnread = ComUtil.Bool(() => row!["UnRead"]),
                        HasAttachments = ComUtil.Bool(() => row![PropHasAttach]),
                        Categories = ComUtil.ParseCategories(ComUtil.Str(() => row!["Categories"])),
                        ConversationKey = ConversationKeyFrom(ComUtil.Try<object?>(() => row![PropConversationId])),
                    });
                }
                finally { ComUtil.Release(row); }
            }

            return results;
        }
        finally { ComUtil.ReleaseAll(columns, table); }
    }

    /// <summary>
    /// Fallback for stores where GetTable is unavailable. Correct but slower,
    /// because each property read is a separate cross-process call.
    /// </summary>
    private static IReadOnlyList<MailSummary> ReadViaItems(object folderObj, string storeId, int max)
    {
        dynamic folder = folderObj;
        dynamic? items = null;
        var results = new List<MailSummary>(Math.Min(max, 256));

        try
        {
            items = folder.Items;
            items!.Sort("[ReceivedTime]", true);

            dynamic? item = items.GetFirst();
            while (item is not null && results.Count < max)
            {
                try
                {
                    if (ComUtil.Int(() => item!.Class) == ComUtil.OlMail)
                        results.Add(SummaryFromItem((object)item!, storeId));
                }
                finally
                {
                    var previous = item;
                    item = items.GetNext();
                    ComUtil.Release(previous);
                }
            }

            return results;
        }
        finally { ComUtil.Release(items); }
    }

    private static MailSummary SummaryFromItem(object mailObj, string storeId)
    {
        dynamic mail = mailObj;
        return new MailSummary
        {
        Ref = new MailRef(ComUtil.Str(() => mail.EntryID), storeId),
        InternetMessageId = ComUtil.MapiString((object)mail, ComUtil.PropInternetMessageId),
        Subject = ComUtil.Str(() => mail.Subject),
        SenderName = ComUtil.Str(() => mail.SenderName),
        SenderAddress = ComUtil.SenderSmtp((object)mail),
        ReceivedUtc = ComUtil.Date(() => mail.ReceivedTime),
        IsUnread = ComUtil.Bool(() => mail.UnRead),
        HasAttachments = ComUtil.Int(() => mail.Attachments.Count) > 0,
            Categories = ComUtil.ParseCategories(ComUtil.Str(() => mail.Categories)),
            ConversationKey = ComUtil.Str(() => mail.ConversationID),
        };
    }

    public Task<MailBody> GetBodyAsync(MailRef mail, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null;
            try
            {
                item = GetItem(mail);

                var (to, cc) = ReadRecipients((object)item!);
                var html = NullIfEmpty(ComUtil.Str(() => item!.HTMLBody));
                var inline = SaveInlineImages((object)item!, mail.EntryId, html);

                return new MailBody
                {
                    Ref = mail,
                    Subject = ComUtil.Str(() => item!.Subject),
                    SenderName = ComUtil.Str(() => item!.SenderName),
                    SenderAddress = ComUtil.SenderSmtp((object)item!),
                    ReceivedUtc = ComUtil.Date(() => item!.ReceivedTime),
                    Html = html,
                    PlainText = ComUtil.Str(() => item!.Body),
                    To = to,
                    Cc = cc,
                    InlineImages = inline,
                    Attachments = ReadAttachments((object)item!, html)
                        .Select(a => a with { Source = mail }).ToList(),
                };
            }
            finally { ComUtil.Release(item); }
        }, ct);

    /// <summary>Plain-text mail has an empty HTMLBody; treat that as "no HTML".</summary>
    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private dynamic GetItem(MailRef mail)
    {
        var item = string.IsNullOrEmpty(mail.StoreId)
            ? _session!.GetItemFromID(mail.EntryId)
            : _session!.GetItemFromID(mail.EntryId, mail.StoreId);

        return item ?? throw new InvalidOperationException(
            "That message could not be found - it may have been moved or deleted in Outlook.");
    }

    private static (List<Recipient> To, List<Recipient> Cc) ReadRecipients(object mailObj)
    {
        dynamic mail = mailObj;
        var to = new List<Recipient>();
        var cc = new List<Recipient>();

        dynamic? recipients = null;
        try
        {
            recipients = mail.Recipients;
            int count = ComUtil.Int(() => recipients!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? r = null;
                try
                {
                    r = recipients![i];
                    var name = ComUtil.Str(() => r!.Name);

                    var address = ComUtil.MapiString((object)r!, ComUtil.PropRecipientSmtpAddress);
                    if (string.IsNullOrWhiteSpace(address))
                        address = ComUtil.Str(() => r!.Address);

                    var entry = new Recipient(name, address);

                    // olTo = 1, olCC = 2, olBCC = 3
                    switch (ComUtil.Int(() => r!.Type))
                    {
                        case 1: to.Add(entry); break;
                        case 2: cc.Add(entry); break;
                    }
                }
                finally { ComUtil.Release(r); }
            }
        }
        catch { }
        finally { ComUtil.Release(recipients); }

        return (to, cc);
    }


    public async ValueTask DisposeAsync()
    {
        if (_pollTimer is not null)
        {
            await _pollTimer.DisposeAsync().ConfigureAwait(false);
            _pollTimer = null;
        }

        if (_changeSettle is not null)
        {
            await _changeSettle.DisposeAsync().ConfigureAwait(false);
            _changeSettle = null;
        }

        try
        {
            await _sta.InvokeAsync(() =>
            {
                ReleaseOpenDrafts();
                StopWatching();
                ComUtil.ReleaseAll(_session, _app);
                _session = null;
                _app = null;
                IsConnected = false;
            }).ConfigureAwait(false);
        }
        catch { /* dispatcher already gone */ }

        _sta.Dispose();
        GC.SuppressFinalize(this);
    }
}
