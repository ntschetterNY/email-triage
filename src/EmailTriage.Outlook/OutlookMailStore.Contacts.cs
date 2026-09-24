using System.Runtime.Versioning;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    // olFolderContacts = 10, olFolderSentMail = 5
    private const int FolderContacts = 10;
    private const int FolderSentMail = 5;

    private const string PropDisplayName = "http://schemas.microsoft.com/mapi/proptag/0x3001001F";
    private const string PropSenderName = "http://schemas.microsoft.com/mapi/proptag/0x0C1A001F";
    private const string PropSenderEmail = "http://schemas.microsoft.com/mapi/proptag/0x0C1F001F";
    private const string PropDisplayTo = "http://schemas.microsoft.com/mapi/proptag/0x0E04001F";
    private const string PropDisplayCc = "http://schemas.microsoft.com/mapi/proptag/0x0E03001F";

    // Contact e-mail slots are named properties in PSETID_Address.
    private const string PropEmail1 = "http://schemas.microsoft.com/mapi/id/{00062004-0000-0000-C000-000000000046}/8083001F";
    private const string PropEmail2 = "http://schemas.microsoft.com/mapi/id/{00062004-0000-0000-C000-000000000046}/8093001F";
    private const string PropEmail3 = "http://schemas.microsoft.com/mapi/id/{00062004-0000-0000-C000-000000000046}/80A3001F";

    /// <summary>Contacts get a head start over people who merely emailed once.</summary>
    private const int ContactWeight = 20;

    /// <summary>
    /// Reads the Contacts folder plus who you have been corresponding with.
    /// Uses Outlook's Table API, which returns whole columns in a single call
    /// instead of one cross-process round trip per property per item.
    /// </summary>
    public Task<IReadOnlyList<ContactEntry>> GetFrequentContactsAsync(CancellationToken ct = default) =>
        _sta.InvokeAsync<IReadOnlyList<ContactEntry>>(() =>
        {
            EnsureConnected();
            var result = new List<ContactEntry>();

            // Your own contacts.
            foreach (var row in ReadTable(FolderContacts, null, 5000,
                         PropDisplayName, PropEmail1, PropEmail2, PropEmail3))
            {
                var name = row[0];
                for (var i = 1; i <= 3; i++)
                {
                    if (row[i].Contains('@')) result.Add(new ContactEntry(name, row[i], ContactWeight));
                }
            }

            // People who write to you, counted.
            var senders = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ReadTable(ComUtil.FolderInbox, "[ReceivedTime]", 2000,
                         PropSenderName, ComUtil.PropSenderSmtpAddress, PropSenderEmail))
            {
                var address = row[1].Contains('@') ? row[1] : row[2];
                if (!address.Contains('@')) continue;

                var seen = senders.GetValueOrDefault(address);
                senders[address] = (string.IsNullOrWhiteSpace(seen.Name) ? row[0] : seen.Name, seen.Count + 1);
            }
            result.AddRange(senders.Select(s => new ContactEntry(s.Value.Name, s.Key, s.Value.Count)));

            // People you write to. Sent Items only holds display names in a
            // form the Table can return, so these become name boosts that the
            // directory attaches to whoever carries that name.
            var sentTo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ReadTable(FolderSentMail, "[SentOn]", 1500, PropDisplayTo, PropDisplayCc))
            {
                foreach (var name in (row[0] + ";" + row[1]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    sentTo[name] = sentTo.GetValueOrDefault(name) + 2; // writing to someone says more than hearing from them
            }
            result.AddRange(sentTo.Select(s => new ContactEntry(s.Key, "", s.Value)));

            return result;
        }, ct);

    /// <summary>
    /// Reads rows [start, start + count) of the company directory. Kept to one
    /// small slice per call so the Outlook thread is free between slices.
    /// </summary>
    public Task<AddressBookBatch> GetAddressBookBatchAsync(int start, int count, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? gal = null, entries = null;
            try
            {
                gal = ComUtil.Try<object?>(() => _session!.GetGlobalAddressList());
                if (gal is null) return new AddressBookBatch(Array.Empty<ContactEntry>(), 0);

                entries = gal.AddressEntries;
                int total = ComUtil.Int(() => entries!.Count);
                var result = new List<ContactEntry>(count);

                for (int i = start + 1; i <= Math.Min(total, start + count); i++)
                {
                    dynamic? entry = null;
                    try
                    {
                        entry = entries![i];
                        var address = ComUtil.MapiString((object)entry!, ComUtil.PropRecipientSmtpAddress);
                        if (!address.Contains('@')) continue;

                        result.Add(new ContactEntry(ComUtil.Str(() => entry!.Name), address, 0));
                    }
                    catch { /* one unreadable entry should not cost the rest */ }
                    finally { ComUtil.Release(entry); }
                }

                return new AddressBookBatch(result, total);
            }
            finally { ComUtil.ReleaseAll(entries, gal); }
        }, ct);

    /// <summary>
    /// Pulls up to <paramref name="max"/> rows of the given DASL columns from a
    /// default folder, newest first when a sort column is given. Missing or
    /// odd values come back as empty strings. Returns nothing if the folder or
    /// its table is unavailable.
    /// </summary>
    private List<string[]> ReadTable(int defaultFolder, string? sortBy, int max, params string[] columns)
    {
        var rows = new List<string[]>();

        dynamic? folder = null, table = null, cols = null;
        try
        {
            folder = _session!.GetDefaultFolder(defaultFolder);
            table = folder!.GetTable("", 0); // olUserItems

            cols = table!.Columns;
            cols!.RemoveAll();
            foreach (var c in columns) cols.Add(c);

            if (sortBy is not null) ComUtil.Try<object?>(() => { table.Sort(sortBy, true); return null; });

            if (ComUtil.Try<object?>(() => table.GetArray(max)) is object[,] data)
            {
                int first = data.GetLowerBound(0), last = data.GetUpperBound(0);
                int col0 = data.GetLowerBound(1);

                for (int r = first; r <= last; r++)
                {
                    var row = new string[columns.Length];
                    for (int c = 0; c < columns.Length; c++)
                        row[c] = data[r, col0 + c] is string s ? s.Trim() : "";
                    rows.Add(row);
                }
            }
        }
        catch { /* no such folder, or the store does not support tables */ }
        finally { ComUtil.ReleaseAll(cols, table, folder); }

        return rows;
    }
}
