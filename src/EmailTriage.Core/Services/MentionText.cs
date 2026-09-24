using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// Outlook-style @mentions in the plain-text reply box: finding the "@jan"
/// being typed, swapping it for the chosen person, and finding the finished
/// mentions again at send time so they go out as links.
/// </summary>
public static class MentionText
{
    private const int MaxQueryLength = 40;
    private const int MaxQuerySpaces = 2;
    private static readonly char[] QueryEnders = { ',', ';', ':', '!', '?', '(', ')', '[', ']', '<', '>', '"' };

    /// <summary>A mention being typed: the '@' at <see cref="Start"/> and the search after it.</summary>
    public readonly record struct Query(int Start, string Text);

    /// <summary>One run of a message line: plain text, or a mention of <see cref="Contact"/>.</summary>
    public readonly record struct Segment(string Text, ContactEntry? Contact);

    /// <summary>
    /// The mention being typed just before <paramref name="caret"/>, if any.
    /// The '@' must start a word, so typing an address like bob@x.com never
    /// triggers it, and a mention already accepted is not searched again.
    /// </summary>
    public static Query? Find(string text, int caret, IEnumerable<ContactEntry>? accepted = null)
    {
        if (caret < 1 || caret > text.Length) return null;

        var floor = Math.Max(0, caret - MaxQueryLength - 1);
        for (var i = caret - 1; i >= floor; i--)
        {
            var c = text[i];
            if (c is '\n' or '\r') return null;
            if (c != '@') continue;

            if (i > 0 && !IsOpening(text[i - 1])) return null;

            var query = text[(i + 1)..caret];
            if (query.Length > 0 && char.IsWhiteSpace(query[0])) return null;
            if (query.Count(char.IsWhiteSpace) > MaxQuerySpaces) return null;
            if (query.IndexOfAny(QueryEnders) >= 0) return null; // "@jane, see below" has moved on

            // The caret has moved past a finished mention: "@Jane Smith |".
            if (accepted is not null && accepted.Any(m => Forms(m).Any(f =>
                    caret > i + f.Length && MatchesAt(text, i, f))))
                return null;

            return new Query(i, query);
        }

        return null;
    }

    /// <summary>Swaps the typed "@jan" for "@Jane Smith " and says where the caret goes.</summary>
    public static (string Text, int Caret) Insert(string text, Query query, int caret, ContactEntry contact)
    {
        var mention = "@" + NameOf(contact);
        var after = text[caret..];
        var space = after.Length > 0 && char.IsWhiteSpace(after[0]) ? "" : " ";

        return (text[..query.Start] + mention + space + after, query.Start + mention.Length + 1);
    }

    /// <summary>
    /// The name a mention shows. Directory names written "Smith, Jane" read
    /// badly mid-sentence, so they are turned round.
    /// </summary>
    public static string NameOf(ContactEntry contact)
    {
        var name = contact.Name.Trim();
        if (name.Length == 0 || name == contact.Address)
        {
            var at = contact.Address.IndexOf('@');
            return at > 0 ? contact.Address[..at] : contact.Address;
        }

        var parts = name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !name.Contains('(') ? $"{parts[1]} {parts[0]}" : name;
    }

    /// <summary>
    /// Splits a line into plain text and mentions. A mention cut back to the
    /// first name ("@Jane") still counts, as it does in Outlook.
    /// </summary>
    public static IReadOnlyList<Segment> Split(string line, IEnumerable<ContactEntry> mentions)
    {
        var forms = mentions
            .SelectMany(m => Forms(m).Select(f => (Form: f, Contact: m)))
            .OrderByDescending(f => f.Form.Length)
            .ToList();

        var segments = new List<Segment>();
        var plainStart = 0;
        var i = 0;

        while (i < line.Length)
        {
            if (line[i] == '@' && (i == 0 || IsOpening(line[i - 1])))
            {
                var hit = forms.FirstOrDefault(f => MatchesAt(line, i, f.Form));
                if (hit.Contact is not null)
                {
                    if (i > plainStart) segments.Add(new Segment(line[plainStart..i], null));
                    segments.Add(new Segment(hit.Form, hit.Contact));
                    i += hit.Form.Length;
                    plainStart = i;
                    continue;
                }
            }
            i++;
        }

        if (plainStart < line.Length) segments.Add(new Segment(line[plainStart..], null));
        return segments;
    }

    private static IEnumerable<string> Forms(ContactEntry contact)
    {
        var name = NameOf(contact);
        yield return "@" + name;

        var space = name.IndexOf(' ');
        if (space > 0) yield return "@" + name[..space];
    }

    /// <summary>The form appears at <paramref name="index"/> as a whole word.</summary>
    private static bool MatchesAt(string text, int index, string form)
    {
        if (string.CompareOrdinal(text, index, form, 0, form.Length) != 0) return false;
        var end = index + form.Length;
        return end == text.Length || !char.IsLetterOrDigit(text[end]);
    }

    private static bool IsOpening(char c) => char.IsWhiteSpace(c) || c is '(' or '[' or '{' or '"' or '\'';
}
