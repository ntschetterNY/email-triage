using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Hyperlinks in the plain-text reply box. Ctrl+K writes them as
/// "[text](address)", and at send time those, and any bare web addresses
/// typed into the message, go out as links.
/// </summary>
public static partial class LinkText
{
    /// <summary>One run of a message line: plain text, or <see cref="Text"/> linking to <see cref="Url"/>.</summary>
    public readonly record struct Segment(string Text, string? Url);

    /// <summary>
    /// Replaces the selection (or puts at the caret) a link to
    /// <paramref name="address"/>, and says where the caret goes. With no
    /// text the address is written as it is.
    /// </summary>
    public static (string Text, int Caret) Insert(string text, int start, int length, string label, string address)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);

        var url = Normalize(address);
        label = CleanLabel(label);

        var link = label.Length == 0 || label == address.Trim() || label == url
            ? address.Trim()
            : $"[{label}]({url.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")})";

        return (text[..start] + link + text[(start + length)..], start + link.Length);
    }

    /// <summary>
    /// The address as it should go in the link: "example.com" gets https://,
    /// a bare email address gets mailto:. Empty when there is nothing usable.
    /// </summary>
    public static string Normalize(string address)
    {
        var a = address.Trim();
        if (a.Length == 0) return "";
        if (SchemeRegex().IsMatch(a)) return a;
        if (!a.Contains('/') && a.IndexOf('@') > 0) return "mailto:" + a;
        return "https://" + a;
    }

    /// <summary>
    /// Whether the text looks like something to link to - used to fill in the
    /// address from the clipboard.
    /// </summary>
    public static bool LooksLikeAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Any(char.IsWhiteSpace)) return false;
        return SchemeRegex().IsMatch(t) || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a line into plain text and links: "[text](address)" as Ctrl+K
    /// writes it, and bare http(s):// or www. addresses.
    /// </summary>
    public static IReadOnlyList<Segment> Split(string line)
    {
        var segments = new List<Segment>();
        var plainStart = 0;

        foreach (Match m in LinkRegex().Matches(line))
        {
            if (m.Index > plainStart) segments.Add(new Segment(line[plainStart..m.Index], null));

            if (m.Groups["label"].Success)
            {
                segments.Add(new Segment(m.Groups["label"].Value, Normalize(m.Groups["url"].Value)));
                plainStart = m.Index + m.Length;
                continue;
            }

            // A bare address ends before trailing punctuation: "see example.com."
            var bare = TrimTrailing(m.Value);
            segments.Add(new Segment(bare, Normalize(bare)));
            plainStart = m.Index + bare.Length;
        }

        if (plainStart < line.Length) segments.Add(new Segment(line[plainStart..], null));
        return MergePlain(segments);
    }

    private static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"') { url = url[..^1]; continue; }
            // "(see https://x.com/a)" - the bracket closes the sentence, not the address.
            if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')')) { url = url[..^1]; continue; }
            break;
        }
        return url;
    }

    // Trimming a bare address leaves its punctuation as a plain run of its own.
    private static IReadOnlyList<Segment> MergePlain(List<Segment> segments)
    {
        var merged = new List<Segment>();
        foreach (var s in segments)
        {
            if (s.Url is null && merged.Count > 0 && merged[^1].Url is null)
                merged[^1] = new Segment(merged[^1].Text + s.Text, null);
            else
                merged.Add(s);
        }
        return merged;
    }

    private static string CleanLabel(string label) =>
        label.Replace("\r", " ").Replace("\n", " ").Replace('[', '(').Replace(']', ')').Trim();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    // A written link, or a bare address that starts a word.
    [GeneratedRegex(@"\[(?<label>[^\[\]]+)\]\((?<url>[^()\s]+)\)|(?<![\w@/.])(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}
