using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class ComposeHtmlTests
{
    private const string Open = "<div style=\"font-family:Calibri,sans-serif;font-size:11pt\">";
    private const string Close = "<br></div>";

    private static ComposeParagraph Para(params ComposeInline[] inlines) => new(inlines);

    private static string Body(string html)
    {
        Assert.StartsWith(Open, html);
        Assert.EndsWith(Close, html);
        return html[Open.Length..^Close.Length];
    }

    [Fact]
    public void Plain_text_goes_out_one_div_per_line_as_before()
    {
        var html = ComposeHtml.Render(ComposeDocument.FromPlainText("Hi Jo,\r\n\r\nThanks")).Html;

        Assert.Equal("<div>Hi Jo,</div><div>&nbsp;</div><div>Thanks</div>", Body(html));
    }

    [Fact]
    public void Nothing_written_is_just_the_wrapper()
    {
        var html = ComposeHtml.Render(ComposeDocument.Empty).Html;

        Assert.Equal("", Body(html));
    }

    [Fact]
    public void Styles_wrap_the_run()
    {
        var doc = new ComposeDocument(new[]
        {
            Para(new ComposeRun("a ", ComposeStyle.Bold), new ComposeRun("b", ComposeStyle.Italic | ComposeStyle.Underline),
                 new ComposeRun(" c", ComposeStyle.Strikethrough)),
        });

        Assert.Equal("<div><b>a </b><i><u>b</u></i><s> c</s></div>", Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void Text_is_encoded()
    {
        var doc = new ComposeDocument(new[] { Para(new ComposeRun("a < b & c", ComposeStyle.Bold)) });

        Assert.Equal("<div><b>a &lt; b &amp; c</b></div>", Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void Links_and_bare_addresses_become_anchors()
    {
        var doc = new ComposeDocument(new[]
        {
            Para(new ComposeRun("the doc") { Url = "https://x.com/a" }, new ComposeRun(" or see x.com/b")),
            Para(new ComposeRun("[c](https://x.com/c) and https://x.com/d.")),
        });

        Assert.Equal(
            "<div><a href=\"https://x.com/a\">the doc</a> or see x.com/b</div>" +
            "<div><a href=\"https://x.com/c\">c</a> and <a href=\"https://x.com/d\">https://x.com/d</a>.</div>",
            Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void A_mention_run_goes_out_as_outlook_writes_one()
    {
        var jane = new ContactEntry("Jane Smith", "jane@x.com", 1);
        var doc = new ComposeDocument(new[] { Para(new ComposeRun("@Jane Smith") { Mention = jane }, new ComposeRun(" hi")) });

        var body = Body(ComposeHtml.Render(doc).Html);

        Assert.Matches("^<div><a id=\"OWAAM[0-9A-F]{32}Z\" href=\"mailto:jane@x.com\"><span style=\"font-family:Calibri,sans-serif;text-decoration:none\">@Jane Smith</span></a> hi</div>$", body);
    }

    [Fact]
    public void Mentions_typed_into_plain_text_are_still_found()
    {
        var jane = new ContactEntry("Jane Smith", "jane@x.com", 1);
        var html = ComposeHtml.Render(ComposeDocument.FromPlainText("@Jane can you?"), new[] { jane }).Html;

        Assert.Contains("href=\"mailto:jane@x.com\"", html);
        Assert.Contains(">@Jane</span></a> can you?", html);
    }

    [Fact]
    public void Lists_nest()
    {
        var doc = new ComposeDocument(new ComposeBlock[]
        {
            new ComposeList(ComposeListStyle.Bullet, new[]
            {
                new ComposeListItem(new ComposeBlock[]
                {
                    Para(new ComposeRun("one")),
                    new ComposeList(ComposeListStyle.Number, new[] { new ComposeListItem(new ComposeBlock[] { Para(new ComposeRun("inner")) }) }),
                }),
                new ComposeListItem(new ComposeBlock[] { Para(new ComposeRun("two", ComposeStyle.Bold)) }),
                new ComposeListItem(Array.Empty<ComposeBlock>()),
            }),
            Para(new ComposeRun("after")),
        });

        Assert.Equal(
            "<ul><li>one<ol><li>inner</li></ol></li><li><b>two</b></li><li>&nbsp;</li></ul><div>after</div>",
            Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void Typed_dashes_and_numbers_hang_their_indent()
    {
        var html = ComposeHtml.Render(ComposeDocument.FromPlainText("Plan:\n- one\n  - nested\n1. first\n2) second\n-not a list")).Html;

        Assert.Equal(
            "<div>Plan:</div>" +
            "<div style=\"margin-left:12pt;text-indent:-12pt;\">- one</div>" +
            "<div style=\"margin-left:24pt;text-indent:-12pt;\">- nested</div>" +
            "<div style=\"margin-left:18pt;text-indent:-18pt;\">1. first</div>" +
            "<div style=\"margin-left:18pt;text-indent:-18pt;\">2) second</div>" +
            "<div>-not a list</div>",
            Body(html));
    }

    [Fact]
    public void An_indented_paragraph_keeps_its_margin()
    {
        var doc = new ComposeDocument(new[] { Para(new ComposeRun("in")) with { IndentPt = 15 } });

        Assert.Equal("<div style=\"margin-left:15pt;\">in</div>", Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void Line_breaks_stay_in_the_paragraph()
    {
        var doc = new ComposeDocument(new[] { Para(new ComposeRun("a"), new ComposeLineBreak(), new ComposeRun("b")) });

        Assert.Equal("<div>a<br>b</div>", Body(ComposeHtml.Render(doc).Html));
    }

    [Fact]
    public void Pictures_are_referenced_by_content_id_and_listed_to_attach()
    {
        var doc = new ComposeDocument(new[]
        {
            Para(new ComposeRun("see "), new ComposeImage(@"C:\img\abc123.png", 320, 200)),
            Para(new ComposeImage(@"C:\img\def456.png", 40, 40)),
        });

        var result = ComposeHtml.Render(doc);

        Assert.Equal(
            "<div>see <img src=\"cid:abc123@emailtriage\" width=\"320\" height=\"200\" style=\"border:0\"></div>" +
            "<div><img src=\"cid:def456@emailtriage\" width=\"40\" height=\"40\" style=\"border:0\"></div>",
            Body(result.Html));
        Assert.Equal(new[] { new InlineImage("abc123@emailtriage", @"C:\img\abc123.png"), new InlineImage("def456@emailtriage", @"C:\img\def456.png") }, result.Images);
    }
}

public class ComposeDocumentTests
{
    [Fact]
    public void Plain_text_round_trips()
    {
        var doc = ComposeDocument.FromPlainText("one\n\nthree");

        Assert.Equal(3, doc.Blocks.Count);
        Assert.Equal("one\n\nthree", doc.PlainText);
        Assert.False(doc.IsBlank);
        Assert.False(doc.HasImages);
    }

    [Fact]
    public void Whitespace_alone_is_blank_but_a_picture_is_not()
    {
        Assert.True(ComposeDocument.FromPlainText(" \n ").IsBlank);
        Assert.True(ComposeDocument.Empty.IsBlank);

        var picture = new ComposeDocument(new[] { new ComposeParagraph(new ComposeInline[] { new ComposeImage("a.png", 1, 1) }) });
        Assert.False(picture.IsBlank);
        Assert.True(picture.HasImages);
        Assert.Equal("", picture.PlainText);
    }

    [Fact]
    public void Lists_read_as_text_with_markers()
    {
        var doc = new ComposeDocument(new ComposeBlock[]
        {
            new ComposeParagraph(new ComposeInline[] { new ComposeRun("Plan:") }),
            new ComposeList(ComposeListStyle.Number, new[]
            {
                new ComposeListItem(new ComposeBlock[]
                {
                    new ComposeParagraph(new ComposeInline[] { new ComposeRun("first") }),
                    new ComposeList(ComposeListStyle.Bullet, new[] { new ComposeListItem(new ComposeBlock[] { new ComposeParagraph(new ComposeInline[] { new ComposeRun("sub") }) }) }),
                }),
                new ComposeListItem(new ComposeBlock[] { new ComposeParagraph(new ComposeInline[] { new ComposeRun("second", ComposeStyle.Bold) }) }),
            }),
        });

        Assert.Equal("Plan:\n1. first\n  - sub\n2. second", doc.PlainText);
    }
}

public class ListMarkerTests
{
    [Theory]
    [InlineData("- item", "", "- ", null)]
    [InlineData("  • item", "  ", "• ", null)]
    [InlineData("* item", "", "* ", null)]
    [InlineData("3. item", "", "3. ", 3)]
    [InlineData("12) item", "", "12) ", 12)]
    public void Finds_markers(string line, string leading, string marker, int? number)
    {
        var m = ListMarker.Find(line);

        Assert.NotNull(m);
        Assert.Equal(leading, m!.Value.Leading);
        Assert.Equal(marker, m.Value.Marker);
        Assert.Equal(number, m.Value.Number);
    }

    [Theory]
    [InlineData("-item")]
    [InlineData("item - item")]
    [InlineData("1234. too long")]
    [InlineData("")]
    [InlineData("-")]
    public void Ignores_what_is_not_a_marker(string line) => Assert.Null(ListMarker.Find(line));

    [Fact]
    public void The_next_line_keeps_the_dash_or_counts_on()
    {
        Assert.Equal("- ", ListMarker.Next(ListMarker.Find("- a")!.Value));
        Assert.Equal("4. ", ListMarker.Next(ListMarker.Find("3. a")!.Value));
        Assert.Equal("10) ", ListMarker.Next(ListMarker.Find("9) a")!.Value));
    }

    [Fact]
    public void An_empty_item_is_the_marker_alone()
    {
        Assert.True(ListMarker.IsEmptyItem("- "));
        Assert.False(ListMarker.IsEmptyItem("- a"));
        Assert.False(ListMarker.IsEmptyItem("a"));
    }
}
