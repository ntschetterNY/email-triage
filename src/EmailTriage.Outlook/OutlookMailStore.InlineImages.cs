using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using EmailTriage.Core.Models;

namespace EmailTriage.Outlook;

[SupportedOSPlatform("windows")]
public sealed partial class OutlookMailStore
{
    private const string PropAttachContentId = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";

    /// <summary>
    /// Where embedded images are written so the reading pane can show them.
    /// Mail HTML points at these as <c>cid:</c> links, which a browser cannot
    /// load; the pane serves this folder under a private host name instead.
    /// </summary>
    public static string DefaultInlineImageFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage",
            "InlineImages");

    public string InlineImageFolder { get; init; } = DefaultInlineImageFolder;

    /// <summary>Cached images older than this are cleared on connect.</summary>
    private static readonly TimeSpan InlineImageLifetime = TimeSpan.FromDays(3);

    /// <summary>
    /// Saves the attachments the HTML refers to by Content-ID and returns a map
    /// of Content-ID (lower case) to a path relative to <see cref="InlineImageFolder"/>.
    /// Files already on disk are reused, so reopening a message costs nothing.
    /// </summary>
    private IReadOnlyDictionary<string, string> SaveInlineImages(object mailObj, string entryId, string? html)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (html is null || html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0) return map;

        dynamic mail = mailObj;
        dynamic? attachments = null;

        try
        {
            var folderName = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(entryId)))[..16];
            var dir = Path.Combine(InlineImageFolder, folderName);

            attachments = mail.Attachments;
            int count = ComUtil.Int(() => attachments!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? a = null;
                try
                {
                    a = attachments![i];
                    var cid = ComUtil.MapiString((object)a!, PropAttachContentId).Trim().Trim('<', '>');
                    if (cid.Length == 0 || html.IndexOf(cid, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var ext = Path.GetExtension(ComUtil.Str(() => a!.FileName));
                    if (ext.Length is 0 or > 6) ext = ".img";

                    var file = $"{i}{ext}";
                    var full = Path.Combine(dir, file);
                    if (!File.Exists(full))
                    {
                        Directory.CreateDirectory(dir);
                        a!.SaveAsFile(full);
                    }

                    map[cid.ToLowerInvariant()] = $"{folderName}/{file}";
                }
                catch { /* one unreadable image should not cost the others */ }
                finally { ComUtil.Release(a); }
            }
        }
        catch { }
        finally { ComUtil.Release(attachments); }

        return map;
    }

    private const string PropAttachHidden = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";

    /// <summary>Where opened attachments are saved before Windows opens them.</summary>
    public static string DefaultAttachmentFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage",
            "Attachments");

    public string AttachmentFolder { get; init; } = DefaultAttachmentFolder;

    /// <summary>
    /// The attachments worth listing: real files, not the hidden parts or the
    /// images the HTML already shows inline (signature logos and the like).
    /// </summary>
    private static IReadOnlyList<MailAttachment> ReadAttachments(object mailObj, string? html)
    {
        dynamic mail = mailObj;
        var list = new List<MailAttachment>();
        dynamic? attachments = null;

        try
        {
            attachments = mail.Attachments;
            int count = ComUtil.Int(() => attachments!.Count);

            for (int i = 1; i <= count; i++)
            {
                dynamic? a = null;
                try
                {
                    a = attachments![i];
                    var name = ComUtil.Str(() => a!.FileName);
                    if (string.IsNullOrWhiteSpace(name)) name = ComUtil.Str(() => a!.DisplayName);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var hidden = ComUtil.Try(() => (bool)a!.PropertyAccessor.GetProperty(PropAttachHidden));
                    var cid = ComUtil.MapiString((object)a!, PropAttachContentId).Trim().Trim('<', '>');
                    var shownInline = cid.Length > 0 && html is not null
                        && html.IndexOf("cid:" + cid, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hidden || shownInline) continue;

                    list.Add(new MailAttachment(i, name, ComUtil.Try(() => Convert.ToInt64(a!.Size))));
                }
                finally { ComUtil.Release(a); }
            }
        }
        catch { }
        finally { ComUtil.Release(attachments); }

        return list;
    }

    public Task<string> SaveAttachmentAsync(MailRef mail, int index, CancellationToken ct = default) =>
        _sta.InvokeAsync(() =>
        {
            EnsureConnected();

            dynamic? item = null, attachments = null, a = null;
            try
            {
                item = GetItem(mail);
                attachments = item.Attachments;
                a = attachments![index];

                // Keep the real file name, so the opening program shows it; the
                // per-message folder stops two "invoice.pdf"s colliding.
                var name = ComUtil.Str(() => a!.FileName);
                if (string.IsNullOrWhiteSpace(name)) name = $"attachment-{index}";
                name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));

                var folder = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(mail.EntryId)))[..16];
                var dir = Path.Combine(AttachmentFolder, folder, index.ToString());
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, name);
                a!.SaveAsFile(path);
                return path;
            }
            finally { ComUtil.ReleaseAll(a, attachments, item); }
        }, ct);

    /// <summary>Clears out images and attachments cached for messages viewed days ago.</summary>
    private void PruneInlineImages()
    {
        foreach (var root in new[] { InlineImageFolder, AttachmentFolder })
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                var cutoff = DateTime.UtcNow - InlineImageLifetime;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true);
                }
            }
            catch { /* housekeeping only; a file still open in another program stays */ }
        }
    }
}
