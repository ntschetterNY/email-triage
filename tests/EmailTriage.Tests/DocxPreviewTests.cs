using System.IO.Compression;
using System.Text;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class DocxPreviewTests
{
    private const string Ns =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    private static MemoryStream Docx(string body, string? rels = null, string? numbering = null, byte[]? picture = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("word/document.xml", $"<w:document {Ns}><w:body>{body}</w:body></w:document>");
            if (rels is not null)
                Add("word/_rels/document.xml.rels",
                    $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");
            if (numbering is not null) Add("word/numbering.xml", $"<w:numbering {Ns}>{numbering}</w:numbering>");
            if (picture is not null)
            {
                using var entry = zip.CreateEntry("word/media/image1.png").Open();
                entry.Write(picture);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Html(string body, string? rels = null, string? numbering = null, byte[]? picture = null)
    {
        using var docx = Docx(body, rels, numbering, picture);
        return DocxPreview.ToHtml(docx, "Test.docx");
    }

    [Fact]
    public void Paragraphs_and_formatting_come_through()
    {
        var html = Html(
            "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Bold</w:t></w:r><w:r><w:t xml:space=\"preserve\"> and </w:t></w:r>" +
            "<w:r><w:rPr><w:i/><w:u w:val=\"single\"/></w:rPr><w:t>fancy</w:t></w:r></w:p>");

        Assert.Contains("<p><strong>Bold</strong> and <em><u>fancy</u></em></p>", html);
    }

    [Fact]
    public void Text_is_escaped_and_the_page_runs_no_script()
    {
        var html = Html("<w:p><w:r><w:t>&lt;script&gt;alert(1)&lt;/script&gt;</w:t></w:r></w:p>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Content-Security-Policy", html);
    }

    [Fact]
    public void Built_in_heading_styles_become_headings() =>
        Assert.Contains("<h2>Scope</h2>",
            Html("<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr><w:r><w:t>Scope</w:t></w:r></w:p>"));

    [Fact]
    public void Bulleted_and_numbered_lists_are_told_apart()
    {
        const string numbering =
            "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl></w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>" +
            "<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num>";

        static string Item(int numId, string text) =>
            $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{numId}\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";

        var html = Html(Item(1, "a") + Item(1, "b") + Item(2, "one"), numbering: numbering);

        Assert.Contains("<ul><li>a</li><li>b</li></ul><ol><li>one</li></ol>", html);
    }

    [Fact]
    public void Tables_keep_rows_cells_and_spans()
    {
        var html = Html(
            "<w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr><w:p><w:r><w:t>Head</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");

        Assert.Contains("<table><tr><td colspan=\"2\"><p>Head</p></td></tr><tr><td><p>A</p></td><td><p>B</p></td></tr></table>", html);
    }

    [Fact]
    public void Web_links_survive_and_others_are_dropped()
    {
        const string rels =
            "<Relationship Id=\"rId1\" Type=\"hyperlink\" Target=\"https://example.com/a\" TargetMode=\"External\"/>" +
            "<Relationship Id=\"rId2\" Type=\"hyperlink\" Target=\"file:///C:/secret.txt\" TargetMode=\"External\"/>";

        var html = Html(
            "<w:p><w:hyperlink r:id=\"rId1\"><w:r><w:t>site</w:t></w:r></w:hyperlink>" +
            "<w:hyperlink r:id=\"rId2\"><w:r><w:t>file</w:t></w:r></w:hyperlink></w:p>", rels);

        Assert.Contains("<a href=\"https://example.com/a\">site</a>", html);
        Assert.DoesNotContain("file:///", html);
        Assert.Contains("file</p>", html);
    }

    [Fact]
    public void Tracked_deletions_and_hidden_text_are_left_out()
    {
        var html = Html(
            "<w:p><w:r><w:t>kept</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del>" +
            "<w:r><w:rPr><w:vanish/></w:rPr><w:t>hidden</w:t></w:r></w:p>");

        Assert.Contains("<p>kept</p>", html);
        Assert.DoesNotContain("gone", html);
        Assert.DoesNotContain("hidden", html);
    }

    [Fact]
    public void Pictures_are_inlined_as_data()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var html = Html(
            "<w:p><w:r><w:drawing><a:graphic><a:graphicData><a:blip r:embed=\"rId5\"/></a:graphicData></a:graphic></w:drawing></w:r></w:p>",
            "<Relationship Id=\"rId5\" Type=\"image\" Target=\"media/image1.png\"/>",
            picture: png);

        Assert.Contains("src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\"", html);
    }

    [Fact]
    public void A_file_that_is_not_a_docx_says_so()
    {
        using var junk = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        Assert.Throws<InvalidDataException>(() => DocxPreview.ToHtml(junk, "x.docx"));
    }

    [Theory]
    [InlineData("Report.docx", true)]
    [InlineData("REPORT.DOCX", true)]
    [InlineData("Old.doc", false)]
    public void Word_attachments_preview(string name, bool preview)
    {
        var attachment = new MailAttachment(1, name, 100);
        Assert.Equal(preview, attachment.CanPreview);
        Assert.Equal(preview, attachment.IsWordDocument);
    }
}
