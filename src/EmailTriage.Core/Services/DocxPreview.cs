using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EmailTriage.Core.Services;

/// <summary>
/// Turns a Word (.docx) file into a self-contained HTML page the reading pane
/// can show: paragraphs, headings, bold/italic/underline, lists, tables,
/// links and pictures. It is a preview, not a renderer - page layout,
/// headers, footers and fonts are left to Word.
/// </summary>
/// <remarks>
/// Everything in the document is escaped, nothing it links to loads, and the
/// page carries a CSP that allows only inline styles and data: pictures, so
/// a hostile document can't run script or phone home from the pane.
/// </remarks>
public static class DocxPreview
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    /// <summary>Parts larger than this are refused rather than inflated - a zip bomb guard.</summary>
    private const long MaxPartBytes = 64L * 1024 * 1024;

    /// <summary>Pictures above this are left out of the preview; the page stays light.</summary>
    private const long MaxPictureBytes = 8L * 1024 * 1024;

    private const long MaxTotalPictureBytes = 40L * 1024 * 1024;

    /// <summary>Writes the preview page for <paramref name="docxPath"/> to <paramref name="htmlPath"/>.</summary>
    public static void WriteHtml(string docxPath, string htmlPath, string title)
    {
        using var stream = File.OpenRead(docxPath);
        var html = ToHtml(stream, title);
        File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
    }

    /// <summary>The preview page for a .docx read from <paramref name="docx"/>.</summary>
    /// <exception cref="InvalidDataException">The file isn't a Word document.</exception>
    public static string ToHtml(Stream docx, string title)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new InvalidDataException("not a Word (.docx) file"); }

        using (zip)
        {
            var document = LoadXml(zip, "word/document.xml")
                           ?? throw new InvalidDataException("not a Word (.docx) file");
            var context = new Context(
                zip,
                LoadRelationships(zip),
                LoadHeadingStyles(zip),
                LoadBulletLists(zip));

            var body = document.Root?.Element(W + "body");
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">");
            sb.Append("<title>").Append(Encode(title)).Append("</title><style>").Append(Css).Append("</style></head><body><article class=\"page\">");
            if (body is not null) RenderBlocks(body.Elements(), sb, context);
            sb.Append("</article></body></html>");
            return sb.ToString();
        }
    }

    private const string Css =
        "html{background:#2a2d34}" +
        "body{margin:0;padding:24px 16px;font:15px/1.5 Calibri,'Segoe UI',Arial,sans-serif;color:#1d1d1f}" +
        ".page{background:#fff;max-width:816px;margin:0 auto;padding:56px 64px;box-shadow:0 2px 12px rgba(0,0,0,.4);box-sizing:border-box;overflow-wrap:break-word}" +
        "p{margin:0 0 8px}h1,h2,h3,h4,h5,h6{margin:16px 0 8px;line-height:1.25}" +
        "h1.title{font-size:2.2em;font-weight:400}" +
        "ul,ol{margin:0 0 8px;padding-left:28px}li{margin:0 0 4px}" +
        "table{border-collapse:collapse;margin:0 0 12px;max-width:100%}" +
        "td,th{border:1px solid #bbb;padding:4px 8px;vertical-align:top}td>p:last-child{margin-bottom:0}" +
        "img{max-width:100%;height:auto}a{color:#0563c1}" +
        "@media (max-width:700px){.page{padding:24px}}";

    private sealed record Context(
        ZipArchive Zip,
        IReadOnlyDictionary<string, (string Target, bool External)> Relationships,
        IReadOnlyDictionary<string, int> HeadingStyles,
        IReadOnlySet<string> BulletLists)
    {
        public long PictureBytes { get; set; }
    }

    // ---- blocks ----------------------------------------------------------------

    private static void RenderBlocks(IEnumerable<XElement> blocks, StringBuilder sb, Context context)
    {
        string? openList = null; // "ul" or "ol" while consecutive list paragraphs run

        void CloseList()
        {
            if (openList is null) return;
            sb.Append("</").Append(openList).Append('>');
            openList = null;
        }

        foreach (var block in Flatten(blocks))
        {
            if (block.Name == W + "p")
            {
                var props = block.Element(W + "pPr");
                var numbering = props?.Element(W + "numPr");
                var numId = numbering?.Element(W + "numId")?.Attribute(W + "val")?.Value;

                if (numId is not null && numId != "0")
                {
                    var tag = context.BulletLists.Contains(numId) ? "ul" : "ol";
                    if (openList != tag)
                    {
                        CloseList();
                        sb.Append('<').Append(tag).Append('>');
                        openList = tag;
                    }

                    var level = int.TryParse(numbering?.Element(W + "ilvl")?.Attribute(W + "val")?.Value, out var l) ? l : 0;
                    sb.Append("<li").Append(level > 0 ? $" style=\"margin-left:{level * 24}px\"" : "").Append('>');
                    RenderInline(block.Elements(), sb, context);
                    sb.Append("</li>");
                    continue;
                }

                CloseList();
                RenderParagraph(block, props, sb, context);
            }
            else if (block.Name == W + "tbl")
            {
                CloseList();
                RenderTable(block, sb, context);
            }
        }

        CloseList();
    }

    /// <summary>Content controls, tracked insertions and the like wrap ordinary blocks; look through them.</summary>
    private static IEnumerable<XElement> Flatten(IEnumerable<XElement> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.Name == W + "sdt")
            {
                var content = block.Element(W + "sdtContent");
                if (content is not null)
                    foreach (var inner in Flatten(content.Elements())) yield return inner;
            }
            else if (block.Name == W + "customXml" || block.Name == W + "ins" || block.Name == W + "smartTag")
            {
                foreach (var inner in Flatten(block.Elements())) yield return inner;
            }
            else
            {
                yield return block;
            }
        }
    }

    private static void RenderParagraph(XElement paragraph, XElement? props, StringBuilder sb, Context context)
    {
        var styleId = props?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        var heading = styleId is not null && context.HeadingStyles.TryGetValue(styleId, out var h) ? h : 0;
        var tag = heading switch { 0 => "p", -1 => "h1", _ => $"h{Math.Clamp(heading, 1, 6)}" };

        var style = new StringBuilder();
        switch (props?.Element(W + "jc")?.Attribute(W + "val")?.Value)
        {
            case "center": style.Append("text-align:center;"); break;
            case "right" or "end": style.Append("text-align:right;"); break;
            case "both" or "distribute": style.Append("text-align:justify;"); break;
        }

        var indent = props?.Element(W + "ind");
        var left = Twips(indent?.Attribute(W + "left")?.Value ?? indent?.Attribute(W + "start")?.Value);
        if (left > 0) style.Append($"margin-left:{left}px;");

        sb.Append('<').Append(tag);
        if (heading == -1) sb.Append(" class=\"title\"");
        if (style.Length > 0) sb.Append(" style=\"").Append(style).Append('"');
        sb.Append('>');

        var start = sb.Length;
        RenderInline(paragraph.Elements(), sb, context);
        if (sb.Length == start) sb.Append("&nbsp;"); // an empty paragraph is still a blank line in Word

        sb.Append("</").Append(tag).Append('>');
    }

    private static void RenderTable(XElement table, StringBuilder sb, Context context)
    {
        sb.Append("<table>");
        foreach (var row in Flatten(table.Elements()).Where(e => e.Name == W + "tr"))
        {
            sb.Append("<tr>");
            foreach (var cell in Flatten(row.Elements()).Where(e => e.Name == W + "tc"))
            {
                var props = cell.Element(W + "tcPr");

                // A cell merged into the one above is drawn by that one.
                var merge = props?.Element(W + "vMerge");
                if (merge is not null && merge.Attribute(W + "val")?.Value != "restart")
                {
                    sb.Append("<td style=\"border-top:none\"></td>");
                    continue;
                }

                var span = int.TryParse(props?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value, out var s) && s > 1 ? s : 1;
                var shade = props?.Element(W + "shd")?.Attribute(W + "fill")?.Value;

                sb.Append("<td");
                if (span > 1) sb.Append(" colspan=\"").Append(span).Append('"');
                if (IsHexColor(shade)) sb.Append(" style=\"background:#").Append(shade).Append('"');
                sb.Append('>');
                RenderBlocks(cell.Elements(), sb, context);
                sb.Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    // ---- inline ----------------------------------------------------------------

    private static void RenderInline(IEnumerable<XElement> nodes, StringBuilder sb, Context context)
    {
        foreach (var node in nodes)
        {
            if (node.Name == W + "r")
            {
                RenderRun(node, sb, context);
            }
            else if (node.Name == W + "hyperlink")
            {
                var id = node.Attribute(R + "id")?.Value;
                var href = id is not null && context.Relationships.TryGetValue(id, out var rel) && rel.External
                    ? SafeLink(rel.Target)
                    : null;

                if (href is not null) sb.Append("<a href=\"").Append(Encode(href)).Append("\">");
                RenderInline(node.Elements(), sb, context);
                if (href is not null) sb.Append("</a>");
            }
            else if (node.Name == W + "ins" || node.Name == W + "smartTag" || node.Name == W + "fldSimple"
                     || node.Name == W + "customXml" || node.Name == W + "sdt" || node.Name == W + "sdtContent")
            {
                RenderInline(node.Elements(), sb, context);
            }
            // w:del (tracked deletions), w:pPr, bookmarks and the rest draw nothing.
        }
    }

    private static void RenderRun(XElement run, StringBuilder sb, Context context)
    {
        var props = run.Element(W + "rPr");
        if (IsOn(props?.Element(W + "vanish"))) return; // hidden text

        var open = new StringBuilder();
        var close = new Stack<string>();

        void Wrap(string tag)
        {
            open.Append('<').Append(tag).Append('>');
            close.Push($"</{tag}>");
        }

        if (IsOn(props?.Element(W + "b"))) Wrap("strong");
        if (IsOn(props?.Element(W + "i"))) Wrap("em");
        var underline = props?.Element(W + "u")?.Attribute(W + "val")?.Value;
        if (props?.Element(W + "u") is not null && underline != "none") Wrap("u");
        if (IsOn(props?.Element(W + "strike")) || IsOn(props?.Element(W + "dstrike"))) Wrap("s");
        switch (props?.Element(W + "vertAlign")?.Attribute(W + "val")?.Value)
        {
            case "superscript": Wrap("sup"); break;
            case "subscript": Wrap("sub"); break;
        }

        var style = new StringBuilder();
        var color = props?.Element(W + "color")?.Attribute(W + "val")?.Value;
        if (IsHexColor(color) && color != "000000") style.Append("color:#").Append(color).Append(';');
        var highlight = props?.Element(W + "highlight")?.Attribute(W + "val")?.Value;
        if (HighlightColor(highlight) is { } hl) style.Append("background:").Append(hl).Append(';');
        if (int.TryParse(props?.Element(W + "sz")?.Attribute(W + "val")?.Value, out var halfPoints) && halfPoints is > 0 and < 400)
            style.Append($"font-size:{halfPoints / 2.0:0.#}pt;");
        if (IsOn(props?.Element(W + "caps"))) style.Append("text-transform:uppercase;");
        if (IsOn(props?.Element(W + "smallCaps"))) style.Append("font-variant:small-caps;");

        if (style.Length > 0)
        {
            open.Append("<span style=\"").Append(style).Append("\">");
            close.Push("</span>");
        }

        var content = new StringBuilder();
        foreach (var piece in run.Elements())
        {
            if (piece.Name == W + "t") content.Append(Encode(piece.Value));
            else if (piece.Name == W + "tab") content.Append("&emsp;");
            else if (piece.Name == W + "br" || piece.Name == W + "cr") content.Append("<br>");
            else if (piece.Name == W + "noBreakHyphen") content.Append("&#8209;");
            else if (piece.Name == W + "sym") content.Append(Symbol(piece));
            else if (piece.Name == W + "drawing" || piece.Name == W + "pict" || piece.Name == W + "object")
                content.Append(Pictures(piece, context));
        }

        if (content.Length == 0) return;
        sb.Append(open).Append(content);
        while (close.Count > 0) sb.Append(close.Pop());
    }

    private static string Symbol(XElement sym) =>
        int.TryParse(sym.Attribute(W + "char")?.Value, System.Globalization.NumberStyles.HexNumber, null, out var code)
        && code is >= 0x20 and < 0xD800
            ? $"&#{code};"
            : "";

    /// <summary>Pictures as data: URIs, so the page loads nothing from anywhere.</summary>
    private static string Pictures(XElement container, Context context)
    {
        var ids = container.Descendants(A + "blip").Select(b => b.Attribute(R + "embed")?.Value)
            .Concat(container.Descendants(V + "imagedata").Select(i => i.Attribute(R + "id")?.Value))
            .OfType<string>();

        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (!context.Relationships.TryGetValue(id, out var rel) || rel.External) continue;

            var mime = Path.GetExtension(rel.Target).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => null, // EMF/WMF and friends: the browser can't draw them
            };
            if (mime is null) continue;

            var entry = context.Zip.GetEntry(PartPath("word/", rel.Target));
            if (entry is null || entry.Length > MaxPictureBytes) continue;
            if (context.PictureBytes + entry.Length > MaxTotalPictureBytes) continue;
            context.PictureBytes += entry.Length;

            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            sb.Append("<img alt=\"\" src=\"data:").Append(mime).Append(";base64,")
              .Append(Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length)).Append("\">");
        }
        return sb.ToString();
    }

    // ---- the package's other parts ----------------------------------------------

    private static Dictionary<string, (string, bool)> LoadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        var rels = LoadXml(zip, "word/_rels/document.xml.rels");
        if (rels?.Root is null) return map;

        foreach (var rel in rels.Root.Elements(Rel + "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (id is null || target is null) continue;
            map[id] = (target, string.Equals(rel.Attribute("TargetMode")?.Value, "External", StringComparison.OrdinalIgnoreCase));
        }
        return map;
    }

    /// <summary>Style id → heading level (1-6), or -1 for the Title style.</summary>
    private static Dictionary<string, int> LoadHeadingStyles(ZipArchive zip)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var styles = LoadXml(zip, "word/styles.xml");
        if (styles?.Root is not null)
        {
            foreach (var style in styles.Root.Elements(W + "style"))
            {
                var id = style.Attribute(W + "styleId")?.Value;
                if (id is null) continue;

                var name = style.Element(W + "name")?.Attribute(W + "val")?.Value ?? id;
                if (HeadingLevel(name) is { } level) map[id] = level;
                else if (int.TryParse(style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value, out var outline) && outline < 6)
                    map[id] = outline + 1;
            }
        }

        // Documents without styles.xml still use the built-in ids.
        for (var i = 1; i <= 6; i++) map.TryAdd($"Heading{i}", i);
        map.TryAdd("Title", -1);
        return map;
    }

    internal static int? HeadingLevel(string styleName)
    {
        var name = styleName.Trim();
        if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return -1;
        if (name.StartsWith("heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(7).Trim(), out var level) && level is >= 1 and <= 9)
            return Math.Min(level, 6);
        return null;
    }

    /// <summary>The numIds whose first level is a bullet; every other list is numbered.</summary>
    private static HashSet<string> LoadBulletLists(ZipArchive zip)
    {
        var bullets = new HashSet<string>(StringComparer.Ordinal);
        var numbering = LoadXml(zip, "word/numbering.xml");
        if (numbering?.Root is null) return bullets;

        var bulletAbstracts = numbering.Root.Elements(W + "abstractNum")
            .Where(a => a.Elements(W + "lvl").FirstOrDefault()?.Element(W + "numFmt")?.Attribute(W + "val")?.Value == "bullet")
            .Select(a => a.Attribute(W + "abstractNumId")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var num in numbering.Root.Elements(W + "num"))
        {
            var numId = num.Attribute(W + "numId")?.Value;
            var abstractId = num.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value;
            if (numId is not null && abstractId is not null && bulletAbstracts.Contains(abstractId)) bullets.Add(numId);
        }
        return bullets;
    }

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > MaxPartBytes) throw new InvalidDataException("the document is too large to preview");

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return XDocument.Load(reader);
    }

    /// <summary>A relationship target made into a zip entry path, relative to <paramref name="baseDir"/>.</summary>
    private static string PartPath(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');

        var parts = new List<string>(baseDir.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != "." && segment.Length > 0) parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    // ---- small helpers ----------------------------------------------------------

    /// <summary>A toggle property: present and not explicitly switched off.</summary>
    private static bool IsOn(XElement? toggle) =>
        toggle is not null && toggle.Attribute(W + "val")?.Value is not ("0" or "false" or "off");

    private static int Twips(string? value) =>
        int.TryParse(value, out var twips) && twips > 0 ? Math.Min(twips / 15, 400) : 0;

    private static bool IsHexColor(string? value) =>
        value is { Length: 6 } && value.All(Uri.IsHexDigit);

    private static string? HighlightColor(string? name) => name switch
    {
        "yellow" => "#ffff00",
        "green" => "#00ff00",
        "cyan" => "#00ffff",
        "magenta" => "#ff00ff",
        "blue" => "#0000ff",
        "red" => "#ff0000",
        "darkBlue" => "#000080",
        "darkCyan" => "#008080",
        "darkGreen" => "#008000",
        "darkMagenta" => "#800080",
        "darkRed" => "#800000",
        "darkYellow" => "#808000",
        "darkGray" => "#808080",
        "lightGray" => "#c0c0c0",
        "black" => "#000000",
        _ => null,
    };

    /// <summary>Only web and mail links survive; file:, javascript: and the rest are dropped.</summary>
    internal static string? SafeLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto)
            ? uri.AbsoluteUri
            : null;

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
