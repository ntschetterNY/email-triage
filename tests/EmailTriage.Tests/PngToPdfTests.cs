using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

public class PngToPdfTests
{
    [Fact]
    public void One_page_per_picture()
    {
        var pdf = PngToPdf.Convert(new[] { Png.Rgb(4, 3), Png.Rgb(2, 2), Png.Rgb(5, 1) });
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Equal(3, Regex.Matches(text, @"/Type /Page\b").Count);
        Assert.Contains("/Count 3", text);
    }

    [Fact]
    public void Page_is_sized_to_the_picture_at_96_dpi()
    {
        var text = Encoding.Latin1.GetString(PngToPdf.Convert(new[] { Png.Rgb(96, 192) }));

        Assert.Contains("/MediaBox [0 0 72 144]", text);
    }

    [Fact]
    public void Cross_reference_offsets_point_at_their_objects()
    {
        var pdf = PngToPdf.Convert(new[] { Png.Rgb(3, 3), Png.Rgba(2, 2) });
        var text = Encoding.Latin1.GetString(pdf);

        var start = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[start..]);

        var offsets = Regex.Matches(text[start..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        for (var i = 0; i < offsets.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
    }

    [Fact]
    public void Transparency_becomes_a_soft_mask_and_opaque_pictures_get_none()
    {
        Assert.DoesNotContain("/SMask", Encoding.Latin1.GetString(PngToPdf.Convert(new[] { Png.Rgb(2, 2) })));
        Assert.Contains("/SMask", Encoding.Latin1.GetString(PngToPdf.Convert(new[] { Png.Rgba(2, 2) })));
    }

    [Fact]
    public void Decodes_rgb_pixels()
    {
        var image = PngImage.Decode(Png.Rgb(3, 2));

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(Png.Pixel(2, 1), (image.Rgb[15], image.Rgb[16], image.Rgb[17]));
        Assert.Null(image.Alpha);
    }

    [Fact]
    public void Decodes_every_filter_type()
    {
        foreach (var filter in new byte[] { 0, 1, 2, 3, 4 })
        {
            var image = PngImage.Decode(Png.Rgb(5, 4, filter));
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 5; x++)
                {
                    var i = (y * 5 + x) * 3;
                    Assert.Equal(Png.Pixel(x, y), (image.Rgb[i], image.Rgb[i + 1], image.Rgb[i + 2]));
                }
        }
    }

    [Fact]
    public void Decodes_interlaced_pictures()
    {
        var plain = PngImage.Decode(Png.Rgb(11, 9));
        var interlaced = PngImage.Decode(Png.Rgb(11, 9, interlace: true));

        Assert.Equal(plain.Rgb, interlaced.Rgb);
    }

    [Fact]
    public void Decodes_palette_pictures_with_transparency()
    {
        var image = PngImage.Decode(Png.Palette());

        Assert.Equal((byte)255, image.Rgb[0]);       // first pixel is index 0: red
        Assert.Equal((byte)255, image.Rgb[5]);       // second is index 1: blue
        Assert.Equal(new byte[] { 255, 0 }, image.Alpha);
    }

    [Fact]
    public void Reads_the_resolution_from_pHYs()
    {
        var image = PngImage.Decode(Png.Rgb(10, 10, dpi: 300));

        Assert.Equal(300, image.Dpi, 0);
    }

    [Fact]
    public void Rejects_files_that_are_not_png()
    {
        Assert.Throws<InvalidDataException>(() => PngImage.Decode(Encoding.ASCII.GetBytes("GIF89a......")));
    }

    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("SHOT.PNG", true)]
    [InlineData("shot.jpg", false)]
    [InlineData("png", false)]
    public void Spots_pngs_by_extension(string name, bool png) => Assert.Equal(png, AttachmentExport.IsPng(name));

    [Fact]
    public void A_lone_picture_keeps_its_name()
    {
        Assert.Equal("Screenshot 1.pdf", AttachmentExport.PdfName(new[] { "Screenshot 1.png" }, "RE: Site photos"));
    }

    [Fact]
    public void Several_pictures_are_named_after_the_email()
    {
        Assert.Equal("Site photos - pictures.pdf",
            AttachmentExport.PdfName(new[] { "a.png", "b.png" }, "RE: FW: Site photos"));
        Assert.Equal("Level 2 punch list - pictures.pdf",
            AttachmentExport.PdfName(new[] { "a.png", "b.png" }, "Level 2: punch list?"));
        Assert.Equal("Pictures.pdf", AttachmentExport.PdfName(new[] { "a.png", "b.png" }, "  "));
    }

    [Fact]
    public void Unique_path_never_overwrites()
    {
        var taken = new HashSet<string> { Path.Combine("dir", "invoice.pdf"), Path.Combine("dir", "invoice (2).pdf") };

        Assert.Equal(Path.Combine("dir", "invoice (3).pdf"), AttachmentExport.UniquePath("dir", "invoice.pdf", taken.Contains));
        Assert.Equal(Path.Combine("dir", "other.pdf"), AttachmentExport.UniquePath("dir", "other.pdf", taken.Contains));
    }

    [Fact]
    public void Unique_path_strips_characters_windows_refuses()
    {
        Assert.Equal(Path.Combine("dir", "ab.pdf"), AttachmentExport.UniquePath("dir", "a:b?.pdf", _ => false));
    }

    /// <summary>Builds small PNGs to decode, with a recognisable pattern.</summary>
    private static class Png
    {
        public static (byte, byte, byte) Pixel(int x, int y) => ((byte)(x * 40 + 7), (byte)(y * 50 + 3), (byte)((x + y) * 13));

        public static byte[] Rgb(int w, int h, byte filter = 0, bool interlace = false, double? dpi = null) =>
            Encode(w, h, 2, 3, (x, y) => { var (r, g, b) = Pixel(x, y); return new[] { r, g, b }; }, filter, interlace, dpi);

        public static byte[] Rgba(int w, int h) =>
            Encode(w, h, 6, 4, (x, y) => { var (r, g, b) = Pixel(x, y); return new[] { r, g, b, (byte)128 }; }, 0, false, null);

        public static byte[] Palette()
        {
            using var o = new MemoryStream();
            o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Chunk(o, "IHDR", Header(2, 1, 8, 3, false));
            Chunk(o, "PLTE", new byte[] { 255, 0, 0, 0, 0, 255 });
            Chunk(o, "tRNS", new byte[] { 255, 0 });
            Chunk(o, "IDAT", Deflate(new byte[] { 0, 0, 1 }));
            Chunk(o, "IEND", Array.Empty<byte>());
            return o.ToArray();
        }

        private static byte[] Encode(int w, int h, byte type, int channels, Func<int, int, byte[]> pixel,
            byte filter, bool interlace, double? dpi)
        {
            var passes = interlace
                ? new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) }
                : new[] { (0, 0, 1, 1) };

            using var raw = new MemoryStream();
            foreach (var (x0, y0, dx, dy) in passes)
            {
                var prev = new byte[Math.Max(0, (w - x0 + dx - 1) / dx) * channels];
                for (var y = y0; y < h; y += dy)
                {
                    var row = new List<byte>();
                    for (var x = x0; x < w; x += dx) row.AddRange(pixel(x, y));
                    if (row.Count == 0) continue;
                    var cur = row.ToArray();
                    raw.WriteByte(filter);
                    raw.Write(Filter(filter, cur, prev, channels));
                    prev = cur;
                }
            }

            using var o = new MemoryStream();
            o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Chunk(o, "IHDR", Header(w, h, 8, type, interlace));
            if (dpi is { } d)
            {
                var phys = new byte[9];
                var ppm = (uint)Math.Round(d / 0.0254);
                BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
                BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
                phys[8] = 1;
                Chunk(o, "pHYs", phys);
            }
            Chunk(o, "IDAT", Deflate(raw.ToArray()));
            Chunk(o, "IEND", Array.Empty<byte>());
            return o.ToArray();
        }

        private static byte[] Filter(byte filter, byte[] row, byte[] prev, int bpp)
        {
            var o = new byte[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], corner = i >= bpp ? prev[i - bpp] : 0;
                int predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, corner),
                    _ => 0,
                };
                o[i] = (byte)(row[i] - predictor);
            }
            return o;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static byte[] Header(int w, int h, byte depth, byte type, bool interlace)
        {
            var b = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)h);
            b[8] = depth; b[9] = type; b[12] = (byte)(interlace ? 1 : 0);
            return b;
        }

        private static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
            return ms.ToArray();
        }

        private static void Chunk(Stream o, string kind, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            o.Write(len);
            o.Write(Encoding.ASCII.GetBytes(kind));
            o.Write(data);
            o.Write(new byte[4]); // the decoder doesn't check CRCs
        }
    }
}
