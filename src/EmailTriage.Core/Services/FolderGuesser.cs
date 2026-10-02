using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>A folder the mail probably belongs in, and the evidence for it.</summary>
public sealed record FolderGuess(FolderNode Folder, double Score, string Reason);

/// <summary>
/// Guesses where a mail goes from where mail like it went before. Pure: the
/// evidence comes from <see cref="Abstractions.IFolderUsageRepository"/>, and
/// the sampling that fills it is <see cref="FolderStudy"/>.
///
/// Each folder is scored by what it has in common with the mail: the same
/// sender counts most, the same company some, and each shared subject word a
/// little, less again when that word turns up in many folders ("rfi", "invoice").
/// </summary>
public static class FolderGuesser
{
    /// <summary>Below this a folder is not suggested at all; one mail from the same sender is just enough.</summary>
    public const double MinimumScore = 2.0;

    private const double SenderWeight = 3.0;
    private const double DomainWeight = 1.2;
    private const double SubjectWeight = 1.0;

    /// <summary>Words that say nothing about where a mail belongs.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "from", "with", "your", "you", "this", "that", "are", "was", "were",
        "has", "have", "had", "not", "our", "out", "all", "any", "can", "new", "per", "via", "about",
        "into", "will", "been", "than", "then", "them", "they", "its", "also", "just", "please",
        "thanks", "thank", "hello", "regards", "mail", "email", "message", "action", "required",
        "reminder", "notification", "automatic", "reply", "fwd", "copy", "attached", "see", "here",
        "today", "tomorrow", "week", "day", "one", "two", "what", "when", "where", "how", "who",
    };

    /// <summary>Addresses at these providers say who wrote, not which company.</summary>
    private static readonly HashSet<string> PersonalProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com",
        "yahoo.com", "icloud.com", "me.com", "aol.com", "comcast.net", "proton.me", "protonmail.com",
    };

    /// <summary>
    /// What is remembered about a mail: its sender, the sender's domain, and
    /// the distinct words of its subject ("RE:" and "[External]" dropped,
    /// short words dropped, numbers of two or more digits kept because a
    /// project number is often the best clue there is).
    /// </summary>
    public static IReadOnlyList<FilingFeature> Features(string subject, string senderAddress)
    {
        var features = new List<FilingFeature>();

        var sender = (senderAddress ?? "").Trim().ToLowerInvariant();
        if (sender.Length > 0)
        {
            features.Add(new FilingFeature(FilingFeatureKind.Sender, sender));

            var at = sender.LastIndexOf('@');
            if (at > 0 && at < sender.Length - 1)
            {
                var domain = sender[(at + 1)..];
                if (!PersonalProviders.Contains(domain))
                    features.Add(new FilingFeature(FilingFeatureKind.Domain, domain));
            }
        }

        foreach (var word in SubjectWords(subject))
            features.Add(new FilingFeature(FilingFeatureKind.SubjectWord, word));

        return features;
    }

    /// <summary>Distinct subject words worth remembering, in order of first appearance.</summary>
    public static IReadOnlyList<string> SubjectWords(string subject)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(subject)) return words;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = ConversationGrouper.StripPrefixes(subject).ToLowerInvariant();
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                var word = text[start..i];
                start = -1;
                if (!Keep(word) || !seen.Add(word)) continue;
                words.Add(word);
                if (words.Count >= 24) break;
            }
        }

        return words;
    }

    private static bool Keep(string word)
    {
        if (word.All(char.IsDigit)) return word.Length >= 2;
        return word.Length >= 3 && !Stopwords.Contains(word);
    }

    /// <summary>
    /// The folders this mail most likely belongs in, best first. Only folders
    /// still in <paramref name="index"/> are offered, and only when the
    /// evidence clears <see cref="MinimumScore"/>. Empty when nothing is known.
    /// </summary>
    public static IReadOnlyList<FolderGuess> Guess(
        string subject,
        string senderAddress,
        IReadOnlyList<FilingEvidence> evidence,
        IReadOnlyList<FolderNode> index,
        int limit = 3)
    {
        if (evidence.Count == 0 || index.Count == 0 || limit <= 0) return Array.Empty<FolderGuess>();

        var wanted = Features(subject, senderAddress).ToHashSet();
        if (wanted.Count == 0) return Array.Empty<FolderGuess>();

        // Only evidence about this mail matters; the rest of the table is skipped.
        var relevant = evidence.Where(e => e.Count > 0 && wanted.Contains(new FilingFeature(e.Kind, e.Token))).ToList();
        if (relevant.Count == 0) return Array.Empty<FolderGuess>();

        // A word filed into thirty folders tells little; one filed into a
        // single folder tells a lot.
        var spread = relevant
            .Where(e => e.Kind == FilingFeatureKind.SubjectWord)
            .GroupBy(e => e.Token, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.Ordinal);

        var folders = index
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var guesses = new List<FolderGuess>();

        foreach (var group in relevant.GroupBy(e => e.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!folders.TryGetValue(group.Key, out var folder)) continue;

            double sender = 0, domain = 0, words = 0;
            long senderCount = 0, domainCount = 0, wordCount = 0;
            string domainToken = "";

            foreach (var e in group)
            {
                var strength = Math.Log(1 + e.Count);
                switch (e.Kind)
                {
                    case FilingFeatureKind.Sender:
                        sender += SenderWeight * strength;
                        senderCount = Math.Max(senderCount, e.Count);
                        break;
                    case FilingFeatureKind.Domain:
                        domain += DomainWeight * strength;
                        domainCount = Math.Max(domainCount, e.Count);
                        domainToken = e.Token;
                        break;
                    default:
                        var discount = 1.0 / (1.0 + Math.Log(spread.GetValueOrDefault(e.Token, 1)));
                        words += SubjectWeight * strength * discount;
                        wordCount = Math.Max(wordCount, e.Count);
                        break;
                }
            }

            var score = sender + domain + words;
            if (score < MinimumScore) continue;

            // Name the strongest clue, so the user can tell a good guess from a lucky one.
            var reason =
                sender >= domain && sender >= words ? $"{senderCount} from this sender"
                : domain >= words ? $"{domainCount} from @{domainToken}"
                : $"subject matches {wordCount} {(wordCount == 1 ? "email" : "emails")} here";

            guesses.Add(new FolderGuess(folder, score, reason));
        }

        return guesses
            .OrderByDescending(g => g.Score)
            .ThenBy(g => g.Folder.Depth)
            .ThenBy(g => g.Folder.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}
