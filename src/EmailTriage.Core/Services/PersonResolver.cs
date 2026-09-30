using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Turns whatever the user typed into an assignee box - "sam", "Sam Lee",
/// "sam@corp.com", "Sam Lee &lt;sam@corp.com&gt;" - into a name and address,
/// preferring people already on the thread so a first name is usually enough.
/// </summary>
public static class PersonResolver
{
    /// <summary>
    /// Resolves <paramref name="text"/> against <paramref name="candidates"/>,
    /// which should be ordered most-likely first. Falls back to the literal
    /// text when nothing matches; the email is empty if none can be inferred.
    /// </summary>
    public static Recipient? Resolve(string text, IReadOnlyList<Recipient> candidates)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var open = text.IndexOf('<');
        var close = text.IndexOf('>');
        if (open > 0 && close > open)
            return new Recipient(text[..open].Trim(), text[(open + 1)..close].Trim());

        var byAddress = candidates.FirstOrDefault(c =>
            c.Address.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(byAddress.Address)) return byAddress;

        if (text.Contains('@')) return new Recipient(text, text);

        Recipient? best = null;
        var bestScore = int.MinValue;

        foreach (var c in candidates)
        {
            var name = c.Display;
            if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) return c;

            // Strict greater-than keeps ties with the earlier, likelier candidate.
            if (FuzzyMatcher.Score(text, name) is { } score && score > bestScore)
            {
                best = c;
                bestScore = score;
            }
        }

        return best ?? new Recipient(text, "");
    }
}
