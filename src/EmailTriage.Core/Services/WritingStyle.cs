using System.Text;
using System.Text.RegularExpressions;
using EmailTriage.Core.Abstractions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Learns how the user writes email by reading their recent sent mail once and
/// asking Claude to distil it into a short style guide, which then rides along
/// on every draft prompt so drafts come out in their voice.
///
/// The guide is cached as a plain text file the user can open and edit - the
/// most direct way to "hone" what the AI writes. Deleting the file relearns
/// from scratch on the next draft.
/// </summary>
public sealed partial class WritingStyleService
{
    private readonly IAiAssistant _assistant;
    private readonly IMailStore _store;
    private readonly string _cachePath;

    /// <summary>How many sent messages to scan, and how many usable ones to learn from.</summary>
    public const int ScanLimit = 60;
    public const int MaxSamples = 12;

    /// <summary>A sent mail must have this much of the user's own text to teach anything.</summary>
    public const int MinSampleChars = 120;
    public const int MaxSampleChars = 1500;

    public WritingStyleService(IAiAssistant assistant, IMailStore store, string cachePath)
    {
        _assistant = assistant;
        _store = store;
        _cachePath = cachePath;
    }

    public string CachePath => _cachePath;

    /// <summary>True once a guide exists, so callers can warn about the one-time learning pass.</summary>
    public bool HasProfile
    {
        get
        {
            try { return File.Exists(_cachePath) && new FileInfo(_cachePath).Length > 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// The cached style guide, learning it first if there is none. Returns ""
    /// when there is not enough sent mail to learn from - a valid, style-less
    /// outcome, not an error.
    /// </summary>
    public async Task<string> GetAsync(string? model = null, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var cached = await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cached)) return cached.Trim();
            }
        }
        catch (IOException) { /* unreadable cache reads as absent */ }

        var samples = await CollectSamplesAsync(ct).ConfigureAwait(false);
        if (samples.Count < 3) return "";

        var guide = (await _assistant.AskAsync(BuildPrompt(samples), model, ct).ConfigureAwait(false)).Trim();
        if (guide.Length == 0) return "";

        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cachePath, guide, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* a guide that cannot be cached still styles this draft */ }

        return guide;
    }

    /// <summary>The user's own words from recent sent mail, quoted history stripped.</summary>
    private async Task<List<string>> CollectSamplesAsync(CancellationToken ct)
    {
        var sent = await _store.GetSentItemsAsync(ct).ConfigureAwait(false);
        var mail = await _store.GetMailAsync(sent, ScanLimit, ct).ConfigureAwait(false);

        var samples = new List<string>();
        foreach (var summary in mail)
        {
            if (samples.Count >= MaxSamples) break;

            string text;
            try
            {
                var body = await _store.GetBodyAsync(summary.Ref, ct).ConfigureAwait(false);
                text = ExtractOwnText(body.PlainText);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                continue; // a message that moved or cannot be read teaches nothing
            }

            if (text.Length < MinSampleChars) continue;
            samples.Add(text.Length <= MaxSampleChars ? text : text[..MaxSampleChars]);
        }

        return samples;
    }

    /// <summary>
    /// Cuts a sent mail's plain text down to what the user actually typed:
    /// everything above the first quoted-history marker Outlook writes.
    /// Public and pure so the stripping is testable.
    /// </summary>
    public static string ExtractOwnText(string plainText)
    {
        var lines = plainText.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();

        foreach (var line in lines)
        {
            if (QuoteMarkerRegex().IsMatch(line.TrimStart())) break;
            kept.Add(line.TrimEnd());
        }

        return string.Join('\n', kept).Trim();
    }

    /// <summary>Pure, so tests can see exactly what the model is asked.</summary>
    public static string BuildPrompt(IReadOnlyList<string> samples)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are profiling how one person writes email, so future drafts can be written in their voice. Answer directly; do not use any tools.");
        sb.AppendLine();
        sb.AppendLine("Below are messages they wrote, quoted history removed.");
        sb.AppendLine("Reply with only a compact style guide - short bullet points a ghostwriter could follow, covering:");
        sb.AppendLine("how they open (greeting or none), how they close, typical length, sentence rhythm, formality,");
        sb.AppendLine("capitalisation and punctuation habits, characteristic words and phrases, and how directly they ask for things.");
        sb.AppendLine("Describe only what the samples show; no advice, no commentary.");

        for (var i = 0; i < samples.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Sample {i + 1}");
            sb.AppendLine(samples[i]);
        }

        return sb.ToString();
    }

    // The plain-text shapes Outlook and phones leave above quoted history:
    // "-----Original Message-----", the "________" divider, a "From:" header
    // line, "On Mon, 3 Jun ... wrote:", and mobile signatures.
    [GeneratedRegex(
        @"^(-{3,}\s*Original Message\s*-{3,}|_{8,}|From:\s.+|On .{4,80} wrote:|Sent from my .+|Get Outlook for .+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuoteMarkerRegex();
}
