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

    public event EventHandler? NewMailArrived;

    /// <summary>
    /// How often to look for new mail. Outlook's COM events would be lower
    /// latency, but subscribing to them through late binding needs a connection
    /// point plumbed by hand; polling a single folder is cheap and far simpler
    /// to keep correct.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(20);

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
            StartPolling();
        }, ct);

    private void StartPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = new Timer(_ => PollForNewMail(), null, PollInterval, PollInterval);
    }

    private void PollForNewMail()
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

                    bool changed = (_lastSeenCount >= 0 && count > _lastSeenCount)
                                || (newest > _lastSeenReceived && _lastSeenReceived != DateTimeOffset.MinValue);

                    _lastSeenCount = count;
                    if (newest > _lastSeenReceived) _lastSeenReceived = newest;

                    if (changed) NewMailArrived?.Invoke(this, EventArgs.Empty);
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
                var (attachments, inline) = ReadAttachments((object)item!, mail, html);

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
                    Attachments = attachments,
                    InlineImages = inline,
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

    /// <summary>
    /// Splits the attachments into the ones the HTML embeds by <c>cid:</c> -
    /// pasted photos, signature logos - and real attachments. Embedded ones are
    /// written to the inline image cache so the body can show them; real ones
    /// are only listed, so opening a mail never copies a large PDF to disk.
    /// </summary>
    private static (IReadOnlyList<AttachmentInfo> Attachments, IReadOnlyList<InlineImage> Inline)
        ReadAttachments(object mailObj, MailRef mail, string? html)
    {
        dynamic item = mailObj;
        var listed = new List<AttachmentInfo>();
        var inline = new List<InlineImage>();
        var hasCid = html is not null && html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) >= 0;

        dynamic? attachments = null;
        try
        {
            attachments = item.Attachments;
            int count = ComUtil.Int(() => attachments!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? a = null;
                try
                {
                    a = attachments![i];
                    var name = ComUtil.Str(() => a!.FileName);

                    var contentId = hasCid
                        ? ComUtil.MapiString((object)a!, ComUtil.PropAttachContentId).Trim('<', '>')
                        : "";

                    if (contentId.Length > 0 &&
                        html!.IndexOf("cid:" + contentId, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var relative = SaveToCache((object)a!, mail, name, i);
                        if (relative is not null) inline.Add(new InlineImage(contentId, relative));
                        continue;
                    }

                    // Embedded OLE objects and the like have no file name and
                    // cannot be opened meaningfully outside Outlook.
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    listed.Add(new AttachmentInfo(i, name, ComUtil.Int(() => a!.Size)));
                }
                catch { /* one unreadable attachment should not hide the rest */ }
                finally { ComUtil.Release(a); }
            }
        }
        catch { }
        finally { ComUtil.Release(attachments); }

        return (listed, inline);
    }

    /// <summary>Saves an embedded image, returning its path relative to the cache root.</summary>
    private static string? SaveToCache(object attachmentObj, MailRef mail, string name, int index)
    {
        dynamic a = attachmentObj;

        // One folder per message, named by a hash: EntryIds are long and not
        // filename-safe. The index prefix keeps two "image001.png" apart.
        var folderName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(mail.EntryId)))[..16];
        var folder = Path.Combine(InlineImageCache.Root, folderName);
        var fileName = $"{index}_{SafeFileName(name, "image")}";
        var path = Path.Combine(folder, fileName);

        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                a.SaveAsFile(path);
            }
            return folderName + "/" + fileName;
        }
        catch
        {
            return null;
        }
    }

    public Task<string> SaveAttachmentAsync(MailRef mail, int index, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null, attachments = null, a = null;
            try
            {
                item = GetItem(mail);
                attachments = item!.Attachments;
                a = attachments![index];

                // A fresh folder per open, so two attachments with the same
                // name never overwrite each other.
                var folder = Path.Combine(
                    Path.GetTempPath(), "EmailTriage", "Attachments", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(folder);

                var path = Path.Combine(folder, SafeFileName(ComUtil.Str(() => a!.FileName), "attachment"));
                a!.SaveAsFile(path);
                return path;
            }
            finally { ComUtil.ReleaseAll(a, attachments, item); }
        }, ct);

    /// <summary>
    /// Replaces characters that are unsafe in a file name, or that would
    /// break the virtual-host URL the reading pane loads inline images from.
    /// </summary>
    private static string SafeFileName(string name, string fallback) =>
        string.Concat((string.IsNullOrWhiteSpace(name) ? fallback : name)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '#' or '%' or '?' ? '_' : c));

    public async ValueTask DisposeAsync()
    {
        if (_pollTimer is not null)
        {
            await _pollTimer.DisposeAsync().ConfigureAwait(false);
            _pollTimer = null;
        }

        try
        {
            await _sta.InvokeAsync(() =>
            {
                ReleaseOpenDrafts();
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
