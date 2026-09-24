using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Reads and edits the free text of a To/Cc/Bcc line, e.g.
/// <c>Smith, Jane &lt;jane@x.com&gt;; bob@y.com; al</c>.
/// </summary>
public static class RecipientLine
{
    /// <summary>
    /// The partly typed entry at the end of the line - what the user is
    /// searching for. Empty when the last entry is already complete.
    /// </summary>
    public static string CurrentToken(string line)
    {
        var start = TokenStart(line);
        var token = line[start..].Trim();

        // "Name <address>" is a finished entry, not a search.
        return token.Contains('<') || token.Contains('>') ? "" : token;
    }

    /// <summary>Swaps the partly typed entry for the chosen contact and readies the next one.</summary>
    public static string Accept(string line, ContactEntry contact)
    {
        var head = line[..TokenStart(line)].TrimEnd();
        var prefix = head.Length == 0 ? "" : head.EndsWith(';') || head.EndsWith(',') ? head + " " : head + "; ";
        return $"{prefix}{contact.LineText}; ";
    }

    /// <summary>
    /// Splits a line into what Outlook should resolve: the bare address where
    /// one is given in angle brackets, otherwise the text as typed.
    /// </summary>
    public static IReadOnlyList<string> Parse(string line)
    {
        var result = new List<string>();

        foreach (var raw in line.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = raw.Trim();
            if (piece.Length == 0) continue;

            // Commas are ambiguous: "Smith, Jane" is one person, but
            // "a@x.com, b@y.com" is two. Only split when it is plainly a list.
            var parts = piece.Count(c => c == '@') > 1 && !piece.Contains('<')
                ? piece.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { piece };

            foreach (var part in parts)
            {
                var open = part.LastIndexOf('<');
                var close = part.LastIndexOf('>');
                var value = open >= 0 && close > open ? part[(open + 1)..close].Trim() : part;
                if (value.Length > 0) result.Add(value);
            }
        }

        return result;
    }

    /// <summary>Formats existing recipients for display in an editable line.</summary>
    public static string Format(IEnumerable<Recipient> recipients)
    {
        var text = string.Join("; ", recipients.Select(r =>
            string.IsNullOrWhiteSpace(r.Name) || !r.Address.Contains('@') || r.Name == r.Address
                ? r.Display
                : $"{r.Name} <{r.Address}>"));

        return text.Length == 0 ? "" : text + "; ";
    }

    private static int TokenStart(string line)
    {
        var cut = Math.Max(line.LastIndexOf(';'), line.LastIndexOf(','));
        return cut < 0 ? 0 : cut + 1;
    }
}
