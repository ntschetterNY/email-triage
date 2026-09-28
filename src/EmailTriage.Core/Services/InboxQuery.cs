using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>What a search term looks at.</summary>
public enum QueryField
{
    /// <summary>Bare words: each must appear in the subject, a sender, a recipient or the preview.</summary>
    Text,

    /// <summary><c>from:</c> - the sender, or anyone on the CC line.</summary>
    From,

    /// <summary><c>to:</c> (or <c>2:</c>) - anyone on the To line.</summary>
    To,

    /// <summary><c>subject:</c></summary>
    Subject,
}

public sealed record QueryTerm(QueryField Field, string Value);

/// <summary>
/// The filter box's small query language. Each bare word must appear somewhere
/// in the conversation - subject, people or preview - in any order; <c>from:</c>, <c>to:</c> (typed <c>2:</c> for speed) and <c>subject:</c>
/// narrow to one field, and <c>*</c> stands for anything - so <c>2:*@acme</c>
/// finds every conversation sent to someone at Acme, and
/// <c>from:acme subject:*invoice</c> combines the two.
///
/// A field's value runs until the next field, so <c>subject:site walk</c> needs
/// no quotes. A field left as just <c>*</c> (a template not filled in yet)
/// matches everything rather than blanking the list.
///
/// Bare words used to fuzzy-match (letters in order, gaps allowed), but a long
/// subject contains almost any short name as a scattered subsequence, so a
/// search for "mariela" matched nearly the whole inbox. Words now match as
/// substrings, which keeps prefixes like "inv" working.
/// </summary>
public sealed class InboxQuery
{
    private static readonly Dictionary<string, QueryField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["from"] = QueryField.From,
        ["to"] = QueryField.To,
        ["2"] = QueryField.To,
        ["subject"] = QueryField.Subject,
    };

    // A field name at the start of the text or after whitespace, then a colon.
    private static readonly Regex FieldToken = new(
        @"(?<=^|\s)(?<name>from|to|2|subject):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<(QueryField Field, Regex Pattern)> _patterns;
    private readonly List<Regex> _words;

    public IReadOnlyList<QueryTerm> Terms { get; }

    /// <summary>Everything before the first field; each word of it must match.</summary>
    public string FreeText { get; }

    public bool IsEmpty => _words.Count == 0 && _patterns.Count == 0;

    /// <summary>
    /// A term looks for an address (<c>*@acme</c>), which display names alone
    /// cannot answer - the caller should load recipient addresses.
    /// </summary>
    public bool NeedsAddresses => Terms.Any(t =>
        t.Field is QueryField.From or QueryField.To && t.Value.Contains('@'));

    private InboxQuery(string freeText, IReadOnlyList<QueryTerm> terms)
    {
        FreeText = freeText;
        Terms = terms;
        _words = freeText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Unquote)
            .Where(w => !IsPlaceholder(w))
            .Select(Wildcard)
            .ToList();
        _patterns = terms
            .Where(t => t.Field != QueryField.Text && !IsPlaceholder(t.Value))
            .Select(t => (t.Field, Wildcard(t.Value)))
            .ToList();
    }

    public static InboxQuery Parse(string text)
    {
        text ??= "";
        var matches = FieldToken.Matches(text);

        var free = (matches.Count == 0 ? text : text[..matches[0].Index]).Trim();
        var terms = new List<QueryTerm>();
        if (free.Length > 0) terms.Add(new QueryTerm(QueryField.Text, free));

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var value = Unquote(text[start..end].Trim());
            terms.Add(new QueryTerm(Fields[matches[i].Groups["name"].Value], value));
        }

        return new InboxQuery(free, terms);
    }

    /// <summary>
    /// True when every term finds something. <paramref name="valuesOf"/> gives
    /// the text a field can match: for <see cref="QueryField.Text"/> everything
    /// searchable in the conversation, for the others that field's values.
    /// </summary>
    public bool Matches(Func<QueryField, IEnumerable<string>> valuesOf)
    {
        if (_words.Count > 0)
        {
            var text = valuesOf(QueryField.Text).Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (!_words.All(w => text.Any(w.IsMatch))) return false;
        }

        foreach (var (field, pattern) in _patterns)
        {
            if (!valuesOf(field).Any(v => v.Length > 0 && pattern.IsMatch(v))) return false;
        }

        return true;
    }

    /// <summary><c>*</c> matches any run of characters; everything else is literal, anywhere in the value.</summary>
    private static Regex Wildcard(string value)
    {
        var body = string.Join(".*", value.Split('*').Select(Regex.Escape));
        return new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Nothing typed yet, or only the template's asterisks and an @.</summary>
    private static bool IsPlaceholder(string value) =>
        value.All(c => c is '*' or '@' || char.IsWhiteSpace(c));

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
