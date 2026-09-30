using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class LinkTextTests
{
    [Fact]
    public void Puts_a_link_over_the_selection()
    {
        var (text, caret) = LinkText.Insert("see the doc here", 8, 3, "doc", "https://x.com/a");

        Assert.Equal("see the [doc](https://x.com/a) here", text);
        Assert.Equal(30, caret);
    }

    [Fact]
    public void Puts_a_link_at_the_caret()
    {
        var (text, caret) = LinkText.Insert("see ", 4, 0, "the doc", "x.com");

        Assert.Equal("see [the doc](https://x.com)", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void With_no_text_writes_the_address_as_it_is()
    {
        var (text, _) = LinkText.Insert("see ", 4, 0, "", "https://x.com");

        Assert.Equal("see https://x.com", text);
    }

    [Fact]
    public void Keeps_brackets_from_breaking_the_link()
    {
        var (text, _) = LinkText.Insert("", 0, 0, "a [b]", "https://en.wikipedia.org/wiki/A_(b)");

        Assert.Equal("[a (b)](https://en.wikipedia.org/wiki/A_%28b%29)", text);
        Assert.Equal(new LinkText.Segment("a (b)", "https://en.wikipedia.org/wiki/A_%28b%29"), LinkText.Split(text).Single());
    }

    [Theory]
    [InlineData("https://x.com", "https://x.com")]
    [InlineData("  x.com/a ", "https://x.com/a")]
    [InlineData("www.x.com", "https://www.x.com")]
    [InlineData("jane@x.com", "mailto:jane@x.com")]
    [InlineData("mailto:jane@x.com", "mailto:jane@x.com")]
    [InlineData("", "")]
    public void Normalizes_addresses(string address, string url)
    {
        Assert.Equal(url, LinkText.Normalize(address));
    }

    [Fact]
    public void Splits_written_links_and_bare_addresses()
    {
        var parts = LinkText.Split("Read [the doc](https://x.com/d) or www.y.com.");

        Assert.Equal(new[]
        {
            new LinkText.Segment("Read ", null),
            new LinkText.Segment("the doc", "https://x.com/d"),
            new LinkText.Segment(" or ", null),
            new LinkText.Segment("www.y.com", "https://www.y.com"),
            new LinkText.Segment(".", null),
        }, parts);
    }

    [Fact]
    public void A_closing_bracket_after_a_bare_address_is_the_sentence()
    {
        var parts = LinkText.Split("(see https://x.com/a)");

        Assert.Equal("https://x.com/a", parts[1].Text);
        Assert.Equal(")", parts[2].Text);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("mail bob@y.com")]
    [InlineData("[not a link] (x)")]
    public void Leaves_plain_text_alone(string line)
    {
        Assert.Equal(new[] { new LinkText.Segment(line, null) }, LinkText.Split(line));
    }

    [Theory]
    [InlineData("https://x.com", true)]
    [InlineData("www.x.com", true)]
    [InlineData("hello there", false)]
    [InlineData("x.com", false)]
    [InlineData(null, false)]
    public void Knows_an_address_on_the_clipboard(string? text, bool expected)
    {
        Assert.Equal(expected, LinkText.LooksLikeAddress(text));
    }
}
