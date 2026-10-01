using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Lists typed by hand: "- item", "• item", "1. item", "2) item". The
/// composer continues them on Enter, and they go out with a hanging indent so
/// they read as lists in Outlook.
/// </summary>
public static partial class ListMarker
{
    /// <summary>
    /// A marker at the start of a line: the leading whitespace before it, the
    /// marker itself including its trailing space, and whether it counts.
    /// </summary>
    public readonly record struct Match(string Leading, string Marker, int? Number)
    {
        public int Length => Leading.Length + Marker.Length;
        public bool Numbered => Number is not null;
    }

    /// <summary>The marker at the start of <paramref name="line"/>, if there is one.</summary>
    public static Match? Find(string line)
    {
        var m = MarkerRegex().Match(line);
        if (!m.Success) return null;

        var number = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value) : (int?)null;
        return new Match(m.Groups["lead"].Value, m.Groups["marker"].Value, number);
    }

    /// <summary>The marker the next line of the list gets: the same dash or bullet, or the next number.</summary>
    public static string Next(Match marker)
    {
        if (marker.Number is not { } n) return marker.Marker;

        var m = MarkerRegex().Match(marker.Leading + marker.Marker);
        var punctuation = m.Groups["punct"].Value;
        var space = marker.Marker[^1];
        return $"{n + 1}{punctuation}{space}";
    }

    /// <summary>Whether the line is a marker and nothing else - an empty list item.</summary>
    public static bool IsEmptyItem(string line) =>
        Find(line) is { } m && line.Length == m.Length;

    // A dash, bullet or asterisk, or a small number with "." or ")", then a space or tab.
    [GeneratedRegex(@"^(?<lead>[ \t]*)(?<marker>(?:[-–—•·*]|(?<num>\d{1,3})(?<punct>[.)]))[ \t])")]
    private static partial Regex MarkerRegex();
}
