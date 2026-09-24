using System.Globalization;
using System.Text.RegularExpressions;

namespace EmailTriage.Core.Services;

/// <summary>
/// Re-maps the colors a mail body hard-codes so it reads on a dark surface.
///
/// Mail is authored for white paper: Word and Outlook pin text to black or
/// navy, quoted history to grey, and tables to white. Simply blanking those
/// colors loses the ones that mean something (red warnings, highlights,
/// banners), so instead every color is moved in OKLab lightness - dark text
/// becomes light, light fills become dark - while hue and most of the chroma
/// survive. Navy signatures stay blue, yellow highlights stay yellow.
///
/// Only CSS in <c>style</c> attributes and <c>&lt;style&gt;</c> blocks, plus
/// the legacy color attributes, is touched; message text never is.
/// </summary>
public static partial class DarkMailColors
{
    public enum Role { Text, Background, Border }

    // OKLab lightness of the preview surface (#16181D) and body text (#D6D9E0).
    private const double SurfaceL = 0.21;
    private const double TextL = 0.88;

    public static string Adapt(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        html = StyleBlockRegex().Replace(html, m =>
            m.Groups["open"].Value + AdaptCss(m.Groups["css"].Value) + m.Groups["close"].Value);

        return TagRegex().Replace(html, m => AdaptTag(m.Value));
    }

    private static string AdaptTag(string tag)
    {
        tag = StyleAttrRegex().Replace(tag, m =>
        {
            var q = m.Groups["q"].Value;
            return m.Groups["pre"].Value + q + AdaptCss(m.Groups["v"].Value) + q;
        });

        return LegacyAttrRegex().Replace(tag, m =>
        {
            var role = m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "bgcolor" => Role.Background,
                "bordercolor" => Role.Border,
                _ => Role.Text,
            };
            var value = m.Groups["v"].Value;
            var mapped = TryParse(value, out var c, allowBareHex: true) ? Format(Map(c, role)) : value;
            var q = m.Groups["q"].Success ? m.Groups["q"].Value : "\"";
            return m.Groups["pre"].Value + q + mapped + q;
        });
    }

    public static string AdaptCss(string css) =>
        DeclarationRegex().Replace(css, m =>
        {
            var prop = m.Groups["prop"].Value.ToLowerInvariant();
            var role = prop.StartsWith("background") ? Role.Background
                : prop.StartsWith("border") || prop.StartsWith("outline") ? Role.Border
                : Role.Text;

            var value = ColorTokenRegex().Replace(m.Groups["val"].Value, t =>
                TryParse(t.Value, out var c) ? Format(Map(c, role)) : t.Value);

            return m.Groups["head"].Value + value;
        });

    // ---- mapping -----------------------------------------------------------

    /// <summary>Returns the dark-surface equivalent of <paramref name="c"/>, or null for transparent.</summary>
    public static Rgba? Map(Rgba c, Role role)
    {
        var (l, a, b) = ToOklab(c);
        var chroma = Math.Sqrt(a * a + b * b);
        var hue = Math.Atan2(b, a);

        double targetL, targetC;
        switch (role)
        {
            case Role.Text:
                // Already light enough to read: leave it alone.
                if (l >= 0.70) return c;
                // Black lands exactly on the default text color; greys land
                // progressively dimmer, so the author's hierarchy survives.
                targetL = TextL - l * 0.26;
                targetC = Math.Min(chroma, 0.16);
                break;

            case Role.Background:
                // Dark fills were chosen for a reason (banners, buttons).
                if (l <= 0.45) return c;
                // White and near-white paper just disappears into the surface.
                if (l > 0.85 && chroma < 0.03) return null;
                // Tinted fills become a dark wash of the same hue.
                targetL = SurfaceL + (1 - l) * 0.35;
                targetC = Math.Min(chroma * 0.8, 0.09);
                break;

            // A strongly colored border is an accent (a warning bar, a quote
            // rule), so it keeps its color like text does.
            case Role.Border when chroma > 0.08:
                return Map(c, Role.Text);

            default:
                // Rules and table lines: dark ones become visible, pale ones recede.
                targetL = 0.30 + (1 - l) * 0.30;
                targetC = Math.Min(chroma * 0.6, 0.08);
                break;
        }

        return FromOklchInGamut(targetL, targetC, hue, c.A);
    }

    // ---- color parsing and formatting --------------------------------------

    public readonly record struct Rgba(double R, double G, double B, double A = 1);

    public static bool TryParse(string token, out Rgba color, bool allowBareHex = false)
    {
        color = default;
        var t = token.Trim();

        if (t.StartsWith('#')) return TryParseHex(t[1..], out color);

        if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = t.IndexOf('(');
            var close = t.LastIndexOf(')');
            if (open < 0 || close < open) return false;

            var parts = t[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 4) return false;

            var ch = new double[4];
            ch[3] = 1;
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                var pct = p.EndsWith('%');
                if (!double.TryParse(pct ? p[..^1] : p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return false;
                ch[i] = i < 3 ? (pct ? v / 100 : v / 255) : (pct ? v / 100 : v);
            }
            color = new Rgba(Clamp(ch[0]), Clamp(ch[1]), Clamp(ch[2]), Clamp(ch[3]));
            return true;
        }

        if (NamedColors.TryGetValue(t, out var hex)) return TryParseHex(hex, out color);

        // Mail sometimes carries bare hex in legacy attributes: bgcolor="FFFFFF".
        return allowBareHex && t.Length == 6 && TryParseHex(t, out color);
    }

    private static bool TryParseHex(string hex, out Rgba color)
    {
        color = default;
        if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length is not (6 or 8)) return false;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;

        if (hex.Length == 6) v = (v << 8) | 0xFF;
        color = new Rgba(((v >> 24) & 0xFF) / 255.0, ((v >> 16) & 0xFF) / 255.0,
                         ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
        return true;
    }

    private static string Format(Rgba? color)
    {
        if (color is not { } c) return "transparent";

        int r = To255(c.R), g = To255(c.G), b = To255(c.B);
        return c.A >= 0.999
            ? $"#{r:x2}{g:x2}{b:x2}"
            : string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{Math.Round(c.A, 3)})");
    }

    private static int To255(double v) => (int)Math.Round(Clamp(v) * 255);
    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    // ---- OKLab ---------------------------------------------------------------

    private static (double L, double A, double B) ToOklab(Rgba c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);

        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) FromOklab(double L, double a, double b)
    {
        var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);

        return (FromLinear(+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }

    /// <summary>Keeps lightness and hue, giving up chroma until the color fits in sRGB.</summary>
    private static Rgba FromOklchInGamut(double L, double chroma, double hue, double alpha)
    {
        (double R, double G, double B) At(double c) => FromOklab(L, c * Math.Cos(hue), c * Math.Sin(hue));
        static bool InGamut((double R, double G, double B) p) =>
            p.R is >= -1e-4 and <= 1 + 1e-4 && p.G is >= -1e-4 and <= 1 + 1e-4 && p.B is >= -1e-4 and <= 1 + 1e-4;

        var rgb = At(chroma);
        if (!InGamut(rgb))
        {
            double lo = 0, hi = chroma;
            for (var i = 0; i < 24; i++)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(At(mid))) lo = mid; else hi = mid;
            }
            rgb = At(lo);
        }
        return new Rgba(Clamp(rgb.R), Clamp(rgb.G), Clamp(rgb.B), alpha);
    }

    private static double ToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double FromLinear(double c) =>
        c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(Math.Max(c, 0), 1 / 2.4) - 0.055;

    // ---- markup ----------------------------------------------------------------

    [GeneratedRegex(@"(?<open><style\b[^>]*>)(?<css>.*?)(?<close></style\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex(@"<[a-zA-Z][^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?<pre>\sstyle\s*=\s*)(?<q>[""'])(?<v>.*?)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleAttrRegex();

    [GeneratedRegex(@"(?<pre>\s(?<name>color|bgcolor|bordercolor|text|link|vlink|alink)\s*=\s*)(?:(?<q>[""'])(?<v>[^""']*)\k<q>|(?<v>[^\s>""']+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex LegacyAttrRegex();

    // The lookbehind keeps vendor properties like mso-color-alt out.
    [GeneratedRegex(@"(?<head>(?<![\w-])(?<prop>color|background-color|background|border(?:-(?:top|right|bottom|left))?(?:-color)?|outline(?:-color)?|text-decoration-color)\s*:\s*)(?<val>[^;{}]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeclarationRegex();

    // url(...) is matched only so the words inside it are skipped.
    [GeneratedRegex(@"url\([^)]*\)|#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|\b[a-zA-Z]+\b")]
    private static partial Regex ColorTokenRegex();

    private static readonly Dictionary<string, string> NamedColors =
        ("aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc " +
         "bisque ffe4c4 black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a " +
         "burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 chocolate d2691e coral ff7f50 " +
         "cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b darkcyan 008b8b " +
         "darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b " +
         "darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 " +
         "darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f " +
         "darkslategrey 2f4f4f darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff " +
         "dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 " +
         "forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 " +
         "gray 808080 green 008000 greenyellow adff2f grey 808080 honeydew f0fff0 hotpink ff69b4 " +
         "indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa lavenderblush fff0f5 " +
         "lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff " +
         "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 " +
         "lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 " +
         "lightslategrey 778899 lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 " +
         "linen faf0e6 magenta ff00ff maroon 800000 mediumaquamarine 66cdaa mediumblue 0000cd " +
         "mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 mediumslateblue 7b68ee " +
         "mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 " +
         "mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 oldlace fdf5e6 " +
         "olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa " +
         "palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 " +
         "peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 " +
         "red ff0000 rosybrown bc8f8f royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 " +
         "seagreen 2e8b57 seashell fff5ee sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd " +
         "slategray 708090 slategrey 708090 snow fffafa springgreen 00ff7f steelblue 4682b4 tan d2b48c " +
         "teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 violet ee82ee wheat f5deb3 " +
         "white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32 " +
         // System colors Word emits; these assume the light paper Outlook composes on.
         "windowtext 000000 window ffffff buttontext 000000 buttonface f0f0f0")
        .Split(' ')
        .Chunk(2)
        .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
}
