namespace EmailTriage.Core.Models;

/// <summary>
/// A shadow a picture in a message can wear, after the picture styles
/// Outlook offers: none, the soft shadow every pasted picture starts with,
/// Outlook's drop shadow rectangle, the same in slate, and its centre
/// shadow. Angles are as WPF has them: 0 is to the right, 90 up, 270 down.
/// </summary>
public sealed record PictureShadow(
    string Name, string Hint, double Blur, double Depth, double Direction, double Opacity, string Color)
{
    public static readonly PictureShadow None =
        new("No shadow", "The picture as it is", 0, 0, 270, 0, "#000000");

    public static readonly PictureShadow Soft =
        new("Soft shadow", "A faint shadow beneath - what a pasted picture starts with", 14, 3, 270, 0.42, "#000000");

    public static readonly PictureShadow Drop =
        new("Drop shadow", "Outlook's drop shadow rectangle, cast down and to the right", 10, 6, 315, 0.5, "#000000");

    public static readonly PictureShadow Slate =
        new("Slate drop shadow", "The drop shadow in slate, a blue-grey that sits well on white", 12, 7, 315, 0.7, "#475569");

    public static readonly PictureShadow Centre =
        new("Centre shadow", "Outlook's centre shadow rectangle, even all the way round", 20, 0, 270, 0.5, "#000000");

    /// <summary>Every style offered, in the order a menu shows them.</summary>
    public static IReadOnlyList<PictureShadow> All { get; } = new[] { None, Soft, Drop, Slate, Centre };

    /// <summary>What a picture wears until the writer picks otherwise.</summary>
    public static PictureShadow Default => Soft;

    public bool IsNone => Opacity <= 0;

    /// <summary>
    /// Room to leave around the picture so the shadow is not cut off when it
    /// is painted into the file that goes out, in pixels at 1x.
    /// </summary>
    public double Padding => IsNone ? 0 : Math.Ceiling(Blur * 0.8 + Depth);

    /// <summary>The style called <paramref name="name"/>, in any case; null for one that is not offered.</summary>
    public static PictureShadow? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour as red, green and blue, 0-255.</summary>
    public (byte R, byte G, byte B) Rgb => (
        Convert.ToByte(Color.Substring(1, 2), 16),
        Convert.ToByte(Color.Substring(3, 2), 16),
        Convert.ToByte(Color.Substring(5, 2), 16));
}
