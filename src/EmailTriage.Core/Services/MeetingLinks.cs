using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Finds the "join" link in a meeting's location or body. Teams, Zoom, Google
/// Meet and Webex are recognised; Safe Links wrappers are unwrapped first,
/// since that is how links arrive in a lot of corporate mail.
/// </summary>
public static partial class MeetingLinks
{
    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = Unwrap(m.Value.TrimEnd('.', ',', ';', ')'));
            if (IsMeetingUrl(url)) return url;
        }

        return null;
    }

    private static bool IsMeetingUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        // Teams writes both the long meetup-join links and, newer, short /meet/<id> ones.
        return (host == "teams.microsoft.com"
                && (path.StartsWith("/l/meetup-join", StringComparison.Ordinal) || path.StartsWith("/meet/", StringComparison.Ordinal)))
            || (host == "teams.live.com" && path.StartsWith("/meet", StringComparison.Ordinal))
            || ((host == "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal))
                && (path.StartsWith("/j/", StringComparison.Ordinal) || path.StartsWith("/my/", StringComparison.Ordinal) || path.StartsWith("/w/", StringComparison.Ordinal)))
            || (host == "meet.google.com" && path.Length > 1)
            || ((host == "webex.com" || host.EndsWith(".webex.com", StringComparison.Ordinal)) && path.Length > 1);
    }

    /// <summary>
    /// Safe Links rewrite every URL to ...safelinks.protection.outlook.com/?url=&lt;real&gt;;
    /// the real one is what should open.
    /// </summary>
    private static string Unwrap(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase))
            return url;

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals("url", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return url;
    }

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
