using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>What a search term looks at.</summary>
public enum QueryField
{
    /// <summary>Bare words: fuzzy-matched against subject and sender, as before.</summary>
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
/// The filter box's small query language. Bare words fuzzy-match as they always
/// have; <c>from:</c>, <c>to:</c> (typed <c>2:</c> for speed) and <c>subject:</c>
/// narrow to one field, and <c>*</c> stands for anything - so <c>2:*@acme</c>
/// finds every conversation sent to someone at Acme, and
/// <c>from:acme subject:*invoice</c> combines the two.
///
/// A field's value runs until the next field, so <c>subject:site walk</c> needs
/// no quotes. A field left as just <c>*</c> (a template not filled in yet)
/// matches everything rather than blanking the list.
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

    public IReadOnlyList<QueryTerm> Terms { get; }

    /// <summary>Everything before the first field, for the fuzzy matcher.</summary>
    public string FreeText { get; }

    public bool IsEmpty => FreeText.Length == 0 && _patterns.Count == 0;

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
    /// the text a field can match: for <see cref="QueryField.Text"/> the subject
    /// and sender, for the others every name and address in the conversation.
    /// </summary>
    public bool Matches(Func<QueryField, IEnumerable<string>> valuesOf)
    {
        if (FreeText.Length > 0 &&
            !valuesOf(QueryField.Text).Any(v => FuzzyMatcher.Score(FreeText, v) is not null))
            return false;

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
