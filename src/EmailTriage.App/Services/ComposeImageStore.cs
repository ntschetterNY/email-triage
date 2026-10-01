using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace EmailTriage.App.Services;

/// <summary>
/// Pictures pasted into a message, kept as PNG files until the message has
/// gone: Outlook attaches them by path. A picture with a drop shadow is
/// painted into a second file with the shadow in its pixels, since Outlook
/// ignores CSS shadows and that way every client shows the same thing.
/// </summary>
public static class ComposeImageStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmailTriage",
        "ComposeImages");

    /// <summary>Room left around a shadowed picture for the blur and offset, in pixels at 1x.</summary>
    public const double ShadowPadding = 14;

    /// <summary>Files from a message that was never sent are cleared after this.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);

    /// <summary>The soft shadow a pasted picture gets, in the editor and in the mail.</summary>
    public static DropShadowEffect Shadow() => new()
    {
        BlurRadius = 14,
        ShadowDepth = 3,
        Direction = 270,
        Opacity = 0.42,
        Color = Colors.Black,
    };

    /// <summary>Writes the picture as a PNG and returns its path.</summary>
    public static string Save(BitmapSource source)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var file = File.Create(path)) encoder.Save(file);
        return path;
    }

    /// <summary>Reads a picture fully into memory, so the file is free to be deleted later.</summary>
    public static BitmapImage Load(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Paints the picture at the size it shows at, with its shadow, into a new
    /// PNG. Rendered at the picture's own resolution when that is higher, so
    /// nothing is lost; the returned size is what the mail should show it at.
    /// </summary>
    public static (string Path, int Width, int Height) WithShadow(BitmapSource source, double width, double height)
    {
        var w = width + 2 * ShadowPadding;
        var h = height + 2 * ShadowPadding;
        var scale = Math.Clamp(source.PixelWidth / width, 1, 4);

        var image = new Image
        {
            Source = source,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Effect = Shadow(),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var host = new Border { Padding = new Thickness(ShadowPadding), Child = image, Background = Brushes.Transparent };
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(host);
        target.Freeze();

        return (Save(target), (int)Math.Round(w), (int)Math.Round(h));
    }

    /// <summary>Removes files a message no longer needs. Nothing is reported: they are only a cache.</summary>
    public static void Delete(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Clears pictures left behind by a crash or an unsent message.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.UtcNow - Lifetime;
            Delete(Directory.EnumerateFiles(Folder, "*.png").Where(f => File.GetLastWriteTimeUtc(f) < cutoff));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
