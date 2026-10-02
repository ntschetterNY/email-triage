namespace EmailTriage.Core.Services;

/// <summary>
/// File naming for saving a conversation's attachments in one go. PNGs are
/// gathered into a single PDF, one picture per page, so a run of screenshots
/// arrives as one document; everything else keeps its own name.
/// </summary>
public static class AttachmentExport
{
    public static bool IsPng(string name) => Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The PDF's name: a lone picture keeps its own name; several are named
    /// after the email they came with.
    /// </summary>
    public static string PdfName(IReadOnlyList<string> pngNames, string subject)
    {
        if (pngNames.Count == 1)
        {
            var own = CleanFileName(Path.GetFileNameWithoutExtension(pngNames[0]));
            return (own.Length == 0 ? "Picture" : own) + ".pdf";
        }

        var title = CleanFileName(ConversationGrouper.StripPrefixes(subject ?? ""));
        if (title.Length > 80) title = title[..80].TrimEnd(' ', '.');
        return (title.Length == 0 ? "Pictures" : title + " - pictures") + ".pdf";
    }

    /// <summary>
    /// A path in <paramref name="folder"/> for <paramref name="name"/> that
    /// overwrites nothing: "invoice (2).pdf" when "invoice.pdf" is taken.
    /// </summary>
    public static string UniquePath(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        name = CleanFileName(name);
        if (name.Length == 0) name = "attachment";

        var path = Path.Combine(folder, name);
        if (!exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!exists(path)) return path;
        }
    }

    /// <summary>Drops characters Windows refuses in file names.</summary>
    public static string CleanFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        return new string(name.Where(c => !bad.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
    }
}
