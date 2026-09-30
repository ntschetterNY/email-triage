using System.Text;
using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// How the user names folders, and how those names nest. Both are templates
/// over the same named parts:
///
///   name pattern  "{Project} - {Type} - {Company}"
///   layout        "{Project}\{Type}\{Company}"
///
/// turn a folder called "Elara - Field Reports - Rimkus" into the path
/// Elara\Field Reports\Rimkus. The layout may reorder the parts, leave some
/// out, or add fixed folders ("Projects\{Project}\{Type}").
/// </summary>
public sealed class FolderScheme
{
    private static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>Characters Outlook refuses in a folder name.</summary>
    private static readonly char[] Forbidden = { ':', '[', ']' };

    public string NamePattern { get; }
    public string Layout { get; }

    /// <summary>The parts, in the order they appear in a folder's name.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Why the scheme cannot be used, or null when it can.</summary>
    public string? Error { get; }

    public bool IsValid => Error is null;

    /// <summary>The literal text between parts, e.g. " - ". Separators[i] sits before Fields[i + 1].</summary>
    private readonly IReadOnlyList<string> _separators = Array.Empty<string>();
    private readonly string _prefix = "";
    private readonly string _suffix = "";

    public FolderScheme(string? namePattern, string? layout)
    {
        NamePattern = namePattern?.Trim() ?? "";
        Layout = (layout?.Trim() ?? "").Replace('/', '\\').Trim('\\');

        var matches = Token.Matches(NamePattern);
        Fields = matches.Select(m => m.Groups[1].Value.Trim()).ToList();

        Error = Check(matches);
        if (Error is not null) return;

        _prefix = NamePattern[..matches[0].Index];
        var last = matches[^1];
        _suffix = NamePattern[(last.Index + last.Length)..];
        _separators = matches.Zip(matches.Skip(1),
                (a, b) => NamePattern[(a.Index + a.Length)..b.Index])
            .ToList();
    }

    public static FolderScheme Default => new(DefaultNamePattern, DefaultLayout);

    public const string DefaultNamePattern = "{Project} - {Type} - {Company}";
    public const string DefaultLayout = @"{Project}\{Type}\{Company}";

    private string? Check(MatchCollection matches)
    {
        if (NamePattern.Length == 0) return "Describe how your folders are named.";
        if (Fields.Count < 2) return "The name needs at least two parts in {braces}, e.g. {Project} - {Type}.";
        if (Fields.Any(f => f.Length == 0)) return "A part in {braces} needs a name.";

        var dupe = Fields.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null) return $"{{{dupe.Key}}} appears twice in the name.";

        for (int i = 1; i < matches.Count; i++)
        {
            var gap = NamePattern[(matches[i - 1].Index + matches[i - 1].Length)..matches[i].Index];
            if (gap.Length == 0)
                return $"Put something between {{{Fields[i - 1]}}} and {{{Fields[i]}}}, like \" - \", so the name can be split.";
        }

        if (Layout.Length == 0) return "Describe how the folders nest, e.g. {Project}\\{Type}\\{Company}.";

        foreach (Match m in Token.Matches(Layout))
        {
            var field = m.Groups[1].Value.Trim();
            if (!Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                return $"{{{field}}} is in the nesting but not in the name.";
        }

        if (Token.Replace(Layout, "").IndexOfAny(Forbidden) >= 0)
            return "A folder name cannot contain : [ or ]";

        if (LayoutLevels.Count < 2) return "The nesting needs at least two levels, separated by \\.";

        return null;
    }

    private IReadOnlyList<string> LayoutLevels =>
        Layout.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads the parts out of a folder name. A name may stop early - with this
    /// scheme "Elara - Field Reports" gives Project and Type - but it needs at
    /// least two parts to count; one on its own is just an ordinary folder.
    /// Extra separators end up in the last part.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Parse(string? name) => Parse(name, minParts: 2);

    private IReadOnlyDictionary<string, string>? Parse(string? name, int minParts)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        // The longest form first: every part, then one fewer, and so on.
        for (int count = Fields.Count; count >= minParts; count--)
        {
            var pattern = new StringBuilder("^");
            pattern.Append(Regex.Escape(_prefix));
            for (int i = 0; i < count; i++)
            {
                if (i > 0) pattern.Append(Regex.Escape(_separators[i - 1]));
                pattern.Append(i == count - 1 ? "(.+)" : "(.+?)");
            }
            if (count == Fields.Count) pattern.Append(Regex.Escape(_suffix));
            pattern.Append('$');

            var m = Regex.Match(name, pattern.ToString(), RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++) values[Fields[i]] = m.Groups[i + 1].Value.Trim();

            // "Elara -  - Rimkus" has an empty part, which would otherwise
            // surface as a part called "- Rimkus"; it is not this scheme.
            if (values.Values.All(v => v.Length > 0 && !EdgedBySeparator(v))
                // A lone part holding a separator is a name that did not split.
                && (count > 1 || !HoldsSeparator(values.Values.Single())))
                return values;
        }

        return null;
    }

    /// <summary>Spaced as the scheme spaces it, so "Smith-Jones" is still one part under " - ".</summary>
    private bool HoldsSeparator(string value) =>
        _separators.Where(s => s.Trim().Length > 0).Any(s =>
            value.Contains(s, StringComparison.OrdinalIgnoreCase));

    private bool EdgedBySeparator(string value) =>
        _separators.Select(s => s.Trim()).Where(s => s.Length > 0).Any(s =>
            value.StartsWith(s, StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The nested folders a name belongs in, outermost first, or null when the
    /// name does not follow the scheme. Levels whose parts the name lacks are
    /// left out, so "Elara - Field Reports" nests as Elara\Field Reports.
    /// </summary>
    public IReadOnlyList<string>? ToLevels(string? name) => ToLevels(name, minParts: 2);

    private IReadOnlyList<string>? ToLevels(string? name, int minParts)
    {
        var values = Parse(name, minParts);
        if (values is null) return null;

        var levels = new List<string>();
        foreach (var level in LayoutLevels)
        {
            bool missing = false;
            var text = Token.Replace(level, m =>
            {
                if (values.TryGetValue(m.Groups[1].Value.Trim(), out var v)) return v;
                missing = true;
                return "";
            }).Trim();

            if (missing || text.Length == 0) continue;
            levels.Add(text);
        }

        return levels.Count >= minParts ? levels : null;
    }

    /// <summary>
    /// Reads a name still being typed: the folders it already names, and the
    /// start of the one after. "Elara - Procurement - " gives Elara\Procurement
    /// and nothing yet; "Elara - Procurement - Ri" gives Elara\Procurement and
    /// "Ri". Null when the text does not follow the scheme.
    /// </summary>
    public (IReadOnlyList<string> Parent, string Partial)? ToTypingLevels(string? typed)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(typed)) return null;

        var complete = WithoutTrailingSeparator(typed);
        if (complete.Length < typed.Trim().Length)
            return ToLevels(complete, minParts: 1) is { } parent ? (parent, "") : null;

        return ToLevels(typed) is { Count: >= 2 } levels ? (levels.Take(levels.Count - 1).ToList(), levels[^1]) : null;
    }

    /// <summary>
    /// The text without a separator left dangling at its end, so "Elara -
    /// Procurement - " reads as "Elara - Procurement". The separator must be
    /// typed as the scheme spaces it: "Smith-" is left alone under " - ".
    /// </summary>
    public string WithoutTrailingSeparator(string typed)
    {
        var text = typed.TrimStart();
        foreach (var separator in _separators.OrderByDescending(s => s.Length))
        {
            var core = separator.TrimEnd();
            if (core.Length == 0) continue;

            var end = text.TrimEnd();
            if (end.EndsWith(core, StringComparison.OrdinalIgnoreCase))
                return end[..^core.Length].Trim();
        }
        return text.Trim();
    }

    /// <summary>A name's nesting written as a path, e.g. Elara\Field Reports\Rimkus.</summary>
    public string? ToPath(string? name) =>
        ToLevels(name) is { } levels ? string.Join('\\', levels) : null;
}
