using System.Text.RegularExpressions;
using EmailTriage.Core.Services;
using Xunit;
using static EmailTriage.Core.Services.DarkMailColors;

namespace EmailTriage.Tests;

public class DarkMailColorsTests
{
    private static Rgba Parse(string css)
    {
        Assert.True(TryParse(css, out var c), $"could not parse {css}");
        return c;
    }

    private static double Luma(Rgba c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static string FirstColor(string html) =>
        Regex.Match(html, @"#[0-9a-f]{6}|transparent").Value;

    [Theory]
    [InlineData("black")]
    [InlineData("windowtext")]
    [InlineData("#000")]
    [InlineData("#595959")]
    [InlineData("#1F497D")]
    [InlineData("rgb(0, 0, 128)")]
    public void Dark_text_becomes_light(string color)
    {
        var mapped = Map(Parse(color), Role.Text);

        Assert.NotNull(mapped);
        Assert.True(Luma(mapped.Value) > 0.45, $"{color} still too dark");
    }

    [Fact]
    public void Black_text_lands_on_the_default_text_color()
    {
        var mapped = Map(Parse("black"), Role.Text)!.Value;

        // #D6D9E0 is the preview's own body text.
        Assert.InRange(mapped.R * 255, 205, 225);
    }

    [Fact]
    public void Grey_text_stays_dimmer_than_black_text()
    {
        var black = Map(Parse("#000000"), Role.Text)!.Value;
        var grey = Map(Parse("#7f7f7f"), Role.Text)!.Value;

        Assert.True(Luma(grey) < Luma(black));
    }

    [Fact]
    public void Hue_survives_the_mapping()
    {
        var navy = Map(Parse("#1F497D"), Role.Text)!.Value;
        Assert.True(navy.B > navy.R, "navy should stay blue");

        var red = Map(Parse("#C00000"), Role.Text)!.Value;
        Assert.True(red.R > red.G && red.R > red.B, "red should stay red");
    }

    [Fact]
    public void Light_text_is_left_alone()
    {
        var white = Parse("#ffffff");
        Assert.Equal(white, Map(white, Role.Text));
    }

    [Theory]
    [InlineData("white")]
    [InlineData("#FFFFFF")]
    [InlineData("#fafafa")]
    public void White_paper_becomes_transparent(string color)
        => Assert.Null(Map(Parse(color), Role.Background));

    [Fact]
    public void Tinted_fill_becomes_a_dark_wash_of_the_same_hue()
    {
        var yellow = Map(Parse("yellow"), Role.Background)!.Value;

        Assert.True(Luma(yellow) < 0.15, "highlight should be dark");
        Assert.True(yellow.R > yellow.B && yellow.G > yellow.B, "highlight should stay yellow");
    }

    [Fact]
    public void Dark_fills_are_kept()
    {
        var banner = Parse("#1F3864");
        Assert.Equal(banner, Map(banner, Role.Background));
    }

    [Fact]
    public void Dark_borders_become_visible_and_pale_ones_recede()
    {
        var black = Map(Parse("black"), Role.Border)!.Value;
        var pale = Map(Parse("#E1E1E1"), Role.Border)!.Value;

        Assert.True(Luma(black) > Luma(pale));
        Assert.True(Luma(pale) < 0.3);
    }

    [Fact]
    public void Accent_borders_keep_their_color()
    {
        var bar = Map(Parse("#F5C400"), Role.Border)!.Value;

        Assert.True(bar.R > 0.8 && bar.B < 0.3, "warning bar should stay yellow");
    }

    [Fact]
    public void Rewrites_inline_style_declarations()
    {
        var html = Adapt("<span style=\"font-size:11pt;color:#1F497D\">Hi</span>");

        Assert.StartsWith("<span style=\"font-size:11pt;color:#", html);
        Assert.DoesNotContain("#1F497D", html);
        Assert.EndsWith(">Hi</span>", html);
    }

    [Fact]
    public void Rewrites_style_blocks()
    {
        var html = Adapt("<style>p.MsoNormal{color:black} a:link{color:#0563C1}</style>");

        Assert.DoesNotContain("black", html);
        Assert.DoesNotContain("#0563C1", html);
    }

    [Fact]
    public void Rewrites_legacy_color_attributes()
    {
        var html = Adapt("<table bgcolor=white><tr><td><font color=\"#000000\">x</font></td></tr></table>");

        Assert.Contains("bgcolor=\"transparent\"", html);
        Assert.DoesNotContain("#000000", html);
    }

    [Fact]
    public void Keeps_important_and_other_tokens()
    {
        var html = Adapt("<p style='border-top:solid #E1E1E1 1.0pt !important'>x</p>");

        Assert.Matches(@"border-top:solid #[0-9a-f]{6} 1\.0pt !important", html);
    }

    [Fact]
    public void Preserves_alpha()
    {
        var html = Adapt("<div style=\"background-color:rgba(255,255,0,0.5)\">x</div>");
        Assert.Matches(@"rgba\(\d+,\d+,\d+,0\.5\)", html);
    }

    [Theory]
    [InlineData("<p>Set the text color: black on the form.</p>")]      // prose, not CSS
    [InlineData("<p style=\"mso-color-alt:windowtext\">x</p>")]           // vendor property
    [InlineData("<div style=\"background:url(black.png)\">x</div>")]      // url, not a color
    [InlineData("<p style=\"color:inherit\">x</p>")]
    public void Leaves_non_colors_untouched(string html)
        => Assert.Equal(html, Adapt(html));

    [Fact]
    public void Outlook_quoted_header_reads_on_dark()
    {
        var html = Adapt(
            "<div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0in 0in 0in\">" +
            "<p class=MsoNormal><b><span style='color:windowtext'>From:</span></b></p></div>");

        var text = Parse(Regex.Match(html, @"color:(#[0-9a-f]{6})'").Groups[1].Value);
        Assert.True(Luma(text) > 0.5);
        Assert.NotEqual("", FirstColor(html));
    }
}
