using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EmailTriage.Outlook;

/// <summary>
/// Helpers for talking to Outlook through IDispatch.
///
/// Late binding is deliberate: it removes any build-time dependency on a
/// particular Outlook type library or PIA, so the solution compiles anywhere and
/// runs against whatever Outlook version is actually installed. The cost is that
/// property access is unchecked, which is why every read goes through the
/// forgiving accessors below rather than touching `dynamic` directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComUtil
{
    /// <summary>Outlook item classes we care about.</summary>
    public const int OlMail = 43;

    /// <summary>olFolderInbox</summary>
    public const int FolderInbox = 6;

    /// <summary>olMailItem, as reported by Folder.DefaultItemType</summary>
    public const int DefaultItemTypeMail = 0;

    /// <summary>OlExchangeStoreType values, as reported by Store.ExchangeStoreType</summary>
    public const int ExchangeStorePrimaryMailbox = 0;
    public const int ExchangeStorePublicFolder = 2;
    public const int ExchangeStoreNotExchange = 3;

    // MAPI property tags, addressed by DASL. These give us the values the
    // object model either hides or reports in Exchange-internal form.
    public const string PropInternetMessageId =
        "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    public const string PropSenderSmtpAddress =
        "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";
    public const string PropRecipientSmtpAddress =
        "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";

    /// <summary>
    /// Releases a runtime callable wrapper. Skipping this is what leaves
    /// outlook.exe running invisibly after the app closes.
    /// </summary>
    public static void Release(object? com)
    {
        if (com is null) return;
        try
        {
            if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch
        {
            // Already released, or the server is gone. Nothing useful to do.
        }
    }

    /// <summary>Releases several wrappers, ignoring nulls.</summary>
    public static void ReleaseAll(params object?[] coms)
    {
        foreach (var c in coms) Release(c);
    }

    /// <summary>
    /// Reads a property that may not exist on this item type or Outlook version.
    /// </summary>
    public static T? Try<T>(Func<T> read, T? fallback = default)
    {
        try { return read(); }
        catch { return fallback; }
    }

    public static string Str(Func<object?> read)
    {
        try { return read()?.ToString() ?? ""; }
        catch { return ""; }
    }

    public static bool Bool(Func<object?> read, bool fallback = false)
    {
        try
        {
            var v = read();
            return v is null ? fallback : Convert.ToBoolean(v);
        }
        catch { return fallback; }
    }

    public static int Int(Func<object?> read, int fallback = 0)
    {
        try
        {
            var v = read();
            return v is null ? fallback : Convert.ToInt32(v);
        }
        catch { return fallback; }
    }

    public static DateTimeOffset Date(Func<object?> read)
    {
        try
        {
            var v = read();
            if (v is null) return default;
            var dt = Convert.ToDateTime(v);
            // Outlook hands back local wall-clock time with no offset attached.
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        }
        catch { return default; }
    }

    /// <summary>
    /// Reads a MAPI property by DASL, returning empty when it is absent - which
    /// is common for drafts and items that never traversed SMTP.
    /// </summary>
    public static string MapiString(object item, string dasl)
    {
        object? accessor = null;
        try
        {
            accessor = ((dynamic)item).PropertyAccessor;
            var value = ((dynamic)accessor!).GetProperty(dasl);
            return value?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Release(accessor);
        }
    }

    /// <summary>
    /// Resolves a usable SMTP address. Exchange reports senders as X.500
    /// distinguished names, which are useless for replying or display, so the
    /// SMTP property is preferred and the legacy DN only used as a last resort.
    /// </summary>
    public static string SenderSmtp(object mailItem)
    {
        dynamic mail = mailItem;

        var smtp = MapiString(mailItem, PropSenderSmtpAddress);
        if (!string.IsNullOrWhiteSpace(smtp)) return smtp;

        var addr = Str(() => mail.SenderEmailAddress);
        var type = Str(() => mail.SenderEmailType);

        if (type.Equals("EX", StringComparison.OrdinalIgnoreCase))
        {
            // Try the address-book route before giving up on the X.500 name.
            object? sender = null, exUser = null;
            try
            {
                sender = mail.Sender;
                if (sender is not null)
                {
                    exUser = ((dynamic)sender).GetExchangeUser();
                    if (exUser is not null)
                    {
                        var primary = Str(() => ((dynamic)exUser!).PrimarySmtpAddress);
                        if (!string.IsNullOrWhiteSpace(primary)) return primary;
                    }
                }
            }
            catch { }
            finally { ReleaseAll(exUser, sender); }
        }

        return addr;
    }

    /// <summary>Escapes a value for embedding in an @SQL restriction.</summary>
    public static string EscapeSql(string value) => value.Replace("'", "''");

    /// <summary>Splits Outlook's semicolon-delimited Categories string.</summary>
    public static IReadOnlyList<string> ParseCategories(string categories) =>
        string.IsNullOrWhiteSpace(categories)
            ? Array.Empty<string>()
            : categories.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string JoinCategories(IEnumerable<string> categories) =>
        string.Join("; ", categories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase));
}
