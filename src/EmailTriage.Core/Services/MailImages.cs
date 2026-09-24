using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Image handling for mail HTML: resolving embedded <c>cid:</c> images to
/// their saved copies, and removing tracking pixels.
/// </summary>
public static partial class MailImages
{
    /// <summary>
    /// Private host the reading pane maps onto the inline image folder, so
    /// embedded images load like ordinary https images. The .example domain
    /// is reserved and can never collide with a real site.
    /// </summary>
    public const string InlineImageHost = "inline-images.example";

    /// <summary>Points <c>cid:</c> image links at the saved copies of those images.</summary>
    public static string ResolveInlineImages(string html, IReadOnlyDictionary<string, string> images)
    {
        if (images.Count == 0) return html;

        return CidRegex().Replace(html, m =>
        {
            var cid = Uri.UnescapeDataString(m.Groups["cid"].Value).Trim('<', '>').ToLowerInvariant();
            return images.TryGetValue(cid, out var path)
                ? $"{m.Groups["pre"].Value}https://{InlineImageHost}/{path}"
                : m.Value;
        });
    }

    /// <summary>
    /// Removes invisible 1x1 (or 0x0) images: the tracking pixels that report
    /// when a mail was opened, so remote images can be shown without them.
    /// </summary>
    public static string StripTrackingPixels(string html) => TrackingPixelRegex().Replace(html, "");

    [GeneratedRegex(@"(?<pre>(?:src|background)\s*=\s*[""']?)cid:(?<cid>[^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRegex();

    [GeneratedRegex(@"<img\b(?=[^>]*\b(?:width\s*=\s*[""']?[01](?:px)?[""'\s/>]|height\s*=\s*[""']?[01](?:px)?[""'\s/>]|style\s*=\s*[""'][^""']*(?:width|height)\s*:\s*[01]px))[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TrackingPixelRegex();
}
