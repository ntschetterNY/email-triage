using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The signatures Outlook keeps on this PC. Outlook saves each one it is
/// given (File › Options › Mail › Signatures) as a set of files named after
/// it in <c>%APPDATA%\Microsoft\Signatures</c> - an .htm, an .rtf and a .txt,
/// plus a <c>Name_files</c> folder for its pictures - so the names on offer
/// are read straight from that folder, and a choice is kept as the name.
/// </summary>
public static class OutlookSignatures
{
    /// <summary>The setting value that leaves the draft as Outlook built it.</summary>
    public const string OutlookDefault = "";

    /// <summary>The setting value that takes Outlook's signature off and puts none on.</summary>
    public const string NoSignature = "(none)";

    /// <summary>Where Outlook keeps them on this PC.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Signatures");

    /// <summary>
    /// Every signature in the folder, by name, in alphabetical order. Empty
    /// when there is no folder - Outlook has not been given a signature yet.
    /// </summary>
    public static IReadOnlyList<MailSignature> Find(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return Array.Empty<MailSignature>();

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.htm", SearchOption.TopDirectoryOnly); }
        catch (IOException) { return Array.Empty<MailSignature>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<MailSignature>(); }

        return files
            // "*.htm" also matches .html on Windows; Outlook writes only .htm.
            .Where(f => string.Equals(Path.GetExtension(f), ".htm", StringComparison.OrdinalIgnoreCase))
            .Select(f => new MailSignature(Path.GetFileNameWithoutExtension(f), f))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// What a saved choice means for the draft about to go: null for
    /// <see cref="OutlookDefault"/>, <see cref="MailSignature.None"/> for
    /// <see cref="NoSignature"/>, or the named signature's file. A name
    /// Outlook no longer has throws, naming it, rather than sending the
    /// message with a signature the user did not choose.
    /// </summary>
    public static MailSignature? Resolve(string? choice, string? folder = null)
    {
        var name = (choice ?? "").Trim();
        if (name.Length == 0) return null;
        if (string.Equals(name, NoSignature, StringComparison.OrdinalIgnoreCase)) return MailSignature.None;

        var match = Find(folder).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"The signature \"{name}\" is no longer in Outlook. Pick another in Settings (Ctrl+,) › Signature.");
    }
}
