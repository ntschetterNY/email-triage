using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class MailImagesTests
{
    private static readonly Dictionary<string, string> Saved = new()
    {
        ["image001.png@01db2f3a.5c9e8f10"] = "ABC/1.png",
        ["logo"] = "ABC/2.jpg",
    };

    [Theory]
    [InlineData("<img src=\"cid:image001.png@01DB2F3A.5C9E8F10\">", "<img src=\"https://inline-images.example/ABC/1.png\">")]
    [InlineData("<img src='cid:logo' width=200>", "<img src='https://inline-images.example/ABC/2.jpg' width=200>")]
    [InlineData("<img src=cid:logo>", "<img src=https://inline-images.example/ABC/2.jpg>")]
    [InlineData("<td background=\"cid:logo\">", "<td background=\"https://inline-images.example/ABC/2.jpg\">")]
    public void Embedded_images_point_at_their_saved_copies(string html, string expected)
    {
        Assert.Equal(expected, MailImages.ResolveInlineImages(html, Saved));
    }

    [Fact]
    public void Unknown_content_ids_are_left_alone()
    {
        const string html = "<img src=\"cid:missing\">";
        Assert.Equal(html, MailImages.ResolveInlineImages(html, Saved));
    }

    [Fact]
    public void Plain_text_mentioning_cid_is_not_rewritten()
    {
        const string html = "<p>the cid:logo reference</p>";
        Assert.Equal(html, MailImages.ResolveInlineImages(html, Saved));
    }

    [Theory]
    [InlineData("<img src=\"https://t.example/o.gif\" width=\"1\" height=\"1\">")]
    [InlineData("<img width=0 height=0 src=https://t.example/o.gif>")]
    [InlineData("<img src=\"https://t.example/o.gif\" style=\"width:1px;height:1px\" />")]
    public void Tracking_pixels_are_removed(string pixel)
    {
        Assert.Equal("<p>a</p><p>b</p>", MailImages.StripTrackingPixels($"<p>a</p>{pixel}<p>b</p>"));
    }

    [Theory]
    [InlineData("<img src=\"https://x.example/logo.png\" width=\"120\" height=\"40\">")]
    [InlineData("<img src=\"https://x.example/logo.png\" width=\"10\">")]
    [InlineData("<img src=\"https://x.example/logo.png\" style=\"width:100px\">")]
    [InlineData("<img src=\"https://x.example/logo.png\">")]
    public void Real_images_are_kept(string image)
    {
        Assert.Equal(image, MailImages.StripTrackingPixels(image));
    }
}
