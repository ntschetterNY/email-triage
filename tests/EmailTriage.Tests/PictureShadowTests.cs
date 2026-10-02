using System.Text.RegularExpressions;
using EmailTriage.Core.Models;
using Xunit;

namespace EmailTriage.Tests;

public class PictureShadowTests
{
    [Fact]
    public void Every_style_has_its_own_name()
    {
        var names = PictureShadow.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void Styles_are_found_by_name_in_any_case()
    {
        Assert.Same(PictureShadow.Slate, PictureShadow.Find("slate drop shadow"));
        Assert.Same(PictureShadow.None, PictureShadow.Find("NO SHADOW"));
        Assert.Null(PictureShadow.Find("bevelled matte"));
        Assert.Null(PictureShadow.Find(null));
    }

    [Fact]
    public void A_pasted_picture_starts_with_the_soft_shadow()
    {
        Assert.Same(PictureShadow.Soft, PictureShadow.Default);
        Assert.Contains(PictureShadow.Default, PictureShadow.All);
        Assert.False(PictureShadow.Default.IsNone);
    }

    [Fact]
    public void Only_no_shadow_is_none_and_it_needs_no_room()
    {
        Assert.True(PictureShadow.None.IsNone);
        Assert.Equal(0, PictureShadow.None.Padding);
        Assert.All(PictureShadow.All.Where(s => s != PictureShadow.None), s => Assert.False(s.IsNone));
    }

    [Fact]
    public void Room_around_a_shadow_covers_its_blur_and_its_offset()
    {
        foreach (var style in PictureShadow.All.Where(s => !s.IsNone))
        {
            Assert.True(style.Padding >= style.Depth + style.Blur / 2, style.Name);
            Assert.Equal(Math.Ceiling(style.Padding), style.Padding);
        }
    }

    [Fact]
    public void The_slate_shadow_is_slate_coloured_and_cast_down_and_right()
    {
        var (r, g, b) = PictureShadow.Slate.Rgb;
        Assert.True(b > r, "slate leans blue");
        Assert.True(g > r, "slate leans blue-grey, not purple");
        Assert.InRange(r, 40, 120);
        Assert.Equal(315, PictureShadow.Slate.Direction);
        Assert.True(PictureShadow.Slate.Depth > 0);
    }

    [Fact]
    public void Outlook_s_drop_shadow_is_offset_and_its_centre_shadow_is_not()
    {
        Assert.Equal(315, PictureShadow.Drop.Direction);
        Assert.True(PictureShadow.Drop.Depth > 0);
        Assert.Equal(0, PictureShadow.Centre.Depth);
        Assert.True(PictureShadow.Centre.Blur > PictureShadow.Drop.Blur);
    }

    [Fact]
    public void Colours_are_six_hex_digits_and_opacities_are_fractions()
    {
        foreach (var style in PictureShadow.All)
        {
            Assert.Matches(new Regex("^#[0-9A-Fa-f]{6}$"), style.Color);
            Assert.InRange(style.Opacity, 0, 1);
            Assert.True(style.Blur >= 0 && style.Depth >= 0, style.Name);
            _ = style.Rgb;
        }
    }
}
