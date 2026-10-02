using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace EmailTriage.Core.Services;

/// <summary>
/// Turns PNG pictures into a PDF with one picture per page, each page sized
/// to its picture. Written by hand rather than pulling in a PDF library: a
/// PDF of images is only a few objects, and PNG decodes with the zlib the
/// runtime already has.
/// </summary>
public static class PngToPdf
{
    /// <summary>Decodes each PNG and writes them, in order, as the pages of one PDF.</summary>
    public static byte[] Convert(IEnumerable<byte[]> pngs) => Write(pngs.Select(PngImage.Decode).ToList());

    /// <summary>Writes already-decoded pictures as the pages of one PDF.</summary>
    public static byte[] Write(IReadOnlyList<PngImage> images)
    {
        if (images.Count == 0) throw new ArgumentException("No pictures to write.", nameof(images));

        var pdf = new PdfBuilder();
        const int catalog = 1, pages = 2;
        var pageIds = new List<int>();
        var next = 3;

        foreach (var image in images)
        {
            int page = next++, content = next++, picture = next++;
            int? mask = image.Alpha is null ? null : next++;
            pageIds.Add(page);

            var w = Num(image.Width * 72.0 / image.Dpi);
            var h = Num(image.Height * 72.0 / image.Dpi);

            pdf.Object(page,
                $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {w} {h}] " +
                $"/Resources << /XObject << /Im0 {picture} 0 R >> >> /Contents {content} 0 R >>");
            pdf.Stream(content, "", Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q"));
            pdf.Stream(picture,
                $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8" + (mask is { } m ? $" /SMask {m} 0 R" : ""),
                image.Rgb, compress: true);
            if (mask is { } id)
                pdf.Stream(id,
                    $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8",
                    image.Alpha!, compress: true);
        }

        pdf.Object(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Object(pages,
            $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>");

        return pdf.Finish(catalog);
    }

    private static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Collects numbered objects and writes the cross-reference table at the end.</summary>
    private sealed class PdfBuilder
    {
        private readonly SortedDictionary<int, byte[]> _objects = new();

        public void Object(int id, string body) => _objects[id] = Encoding.ASCII.GetBytes($"{id} 0 obj\n{body}\nendobj\n");

        public void Stream(int id, string dict, byte[] data, bool compress = false)
        {
            if (compress)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
                data = ms.ToArray();
                dict += " /Filter /FlateDecode";
            }

            using var o = new MemoryStream();
            o.Write(Encoding.ASCII.GetBytes($"{id} 0 obj\n<< {dict.Trim()} /Length {data.Length} >>\nstream\n"));
            o.Write(data);
            o.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
            _objects[id] = o.ToArray();
        }

        public byte[] Finish(int root)
        {
            using var o = new MemoryStream();
            // The binary comment marks the file as binary for tools that sniff it.
            o.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
            o.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            var offsets = new List<long>();
            foreach (var (_, bytes) in _objects)
            {
                offsets.Add(o.Position);
                o.Write(bytes);
            }

            var xref = o.Position;
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
            sb.Append("0000000000 65535 f \n");
            foreach (var off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root ").Append(root).Append(" 0 R >>\n");
            sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
            o.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            return o.ToArray();
        }
    }
}

/// <summary>
/// A PNG decoded to 8-bit RGB, plus an alpha plane when any pixel is not
/// fully opaque. Handles every bit depth, colour type and Adam7 interlacing.
/// </summary>
public sealed class PngImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Pixels per inch from the pHYs chunk, else 96 (what screenshots are taken at).</summary>
    public required double Dpi { get; init; }
    public required byte[] Rgb { get; init; }
    public byte[]? Alpha { get; init; }

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static PngImage Decode(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        double dpi = 96;
        using var idat = new MemoryStream();

        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var kind = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (length < 0 || pos + 12 + (long)length > data.Length) throw new InvalidDataException("Truncated PNG.");
            var chunk = data.AsSpan(pos + 8, length);
            pos += 12 + length;

            switch (kind)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                    depth = chunk[8];
                    type = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE": palette = chunk.ToArray(); break;
                case "tRNS": trns = chunk.ToArray(); break;
                case "pHYs" when length >= 9 && chunk[8] == 1: // unit: metre
                    var ppm = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    if (ppm * 0.0254 is >= 30 and <= 2400) dpi = ppm * 0.0254;
                    break;
                case "IDAT": idat.Write(chunk); break;
            }

            if (kind == "IEND") break;
        }

        var channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width <= 0 || height <= 0 || channels == 0 || depth is not (1 or 2 or 4 or 8 or 16) ||
            (type == 3 && palette is null) || (long)width * height > 100_000_000)
            throw new InvalidDataException("Unsupported PNG.");

        byte[] raw;
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            z.CopyTo(ms);
            raw = ms.ToArray();
        }

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var anyAlpha = false;
        var bitsPerPixel = channels * depth;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var max = (1 << depth) - 1;

        var passes = interlace == 1
            ? new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) }
            : new[] { (0, 0, 1, 1) };

        var at = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var pw = (width - x0 + dx - 1) / dx;
            var ph = (height - y0 + dy - 1) / dy;
            if (pw <= 0 || ph <= 0) continue;

            var stride = (pw * bitsPerPixel + 7) / 8;
            var prev = new byte[stride];
            var row = new byte[stride];

            for (var r = 0; r < ph; r++)
            {
                if (at + 1 + stride > raw.Length) throw new InvalidDataException("Truncated PNG image data.");
                var filter = raw[at];
                Array.Copy(raw, at + 1, row, 0, stride);
                at += 1 + stride;
                Unfilter(filter, row, prev, bpp);

                var y = y0 + r * dy;
                for (var c = 0; c < pw; c++)
                {
                    var i = y * width + x0 + c * dx;
                    int Sample(int n) => depth switch
                    {
                        16 => (row[(c * channels + n) * 2] << 8) | row[(c * channels + n) * 2 + 1],
                        8 => row[c * channels + n],
                        _ => (row[(c * bitsPerPixel) / 8] >> (8 - depth - (c * bitsPerPixel) % 8)) & max,
                    };
                    byte To8(int v) => depth == 16 ? (byte)(v >> 8) : (byte)(v * 255 / max);

                    byte R, G, B, A = 255;
                    switch (type)
                    {
                        case 0:
                            var g = Sample(0);
                            R = G = B = To8(g);
                            if (trns is { Length: >= 2 } && g == ((trns[0] << 8) | trns[1])) A = 0;
                            break;
                        case 2:
                            int sr = Sample(0), sg = Sample(1), sb = Sample(2);
                            (R, G, B) = (To8(sr), To8(sg), To8(sb));
                            if (trns is { Length: >= 6 } &&
                                sr == ((trns[0] << 8) | trns[1]) && sg == ((trns[2] << 8) | trns[3]) && sb == ((trns[4] << 8) | trns[5]))
                                A = 0;
                            break;
                        case 3:
                            var p = Sample(0);
                            if (p * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range.");
                            (R, G, B) = (palette[p * 3], palette[p * 3 + 1], palette[p * 3 + 2]);
                            if (trns is not null && p < trns.Length) A = trns[p];
                            break;
                        case 4:
                            R = G = B = To8(Sample(0));
                            A = To8(Sample(1));
                            break;
                        default:
                            (R, G, B, A) = (To8(Sample(0)), To8(Sample(1)), To8(Sample(2)), To8(Sample(3)));
                            break;
                    }

                    rgb[i * 3] = R; rgb[i * 3 + 1] = G; rgb[i * 3 + 2] = B;
                    alpha[i] = A;
                    if (A != 255) anyAlpha = true;
                }

                (prev, row) = (row, prev);
            }
        }

        return new PngImage { Width = width, Height = height, Dpi = dpi, Rgb = rgb, Alpha = anyAlpha ? alpha : null };
    }

    private static void Unfilter(byte filter, byte[] row, byte[] prev, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], corner = i >= bpp ? prev[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) >> 1)),
                4 => (byte)(row[i] + Paeth(left, up, corner)),
                _ => throw new InvalidDataException("Bad PNG filter."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
