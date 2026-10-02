using System.IO;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

// Saving every attachment of the open conversation at once. PNGs become a
// single PDF with one picture per page (screenshots arrive as one document);
// everything else is copied as it is.
public sealed partial class TriageViewModel
{
    /// <summary>Saves every attachment in the reading pane into <paramref name="folder"/>.</summary>
    public async Task SaveAllAttachmentsAsync(string folder)
    {
        if (OpenBody is not { } body || ThreadAttachments.Count == 0) return;

        var attachments = ThreadAttachments;
        var blocked = attachments.Where(a => a.IsBlockedType).ToList();
        var pngs = attachments.Where(a => !a.IsBlockedType && AttachmentExport.IsPng(a.Name)).ToList();
        var others = attachments.Where(a => !a.IsBlockedType && !AttachmentExport.IsPng(a.Name)).ToList();

        var saved = 0;
        var failed = new List<string>();
        Status = $"Saving {attachments.Count - blocked.Count} attachment(s)...";

        try
        {
            foreach (var a in others)
            {
                try
                {
                    var cached = await _store.SaveAttachmentAsync(SourceOf(a, body), a.Index).ConfigureAwait(true);
                    await Task.Run(() => File.Copy(cached, AttachmentExport.UniquePath(folder, a.Name))).ConfigureAwait(true);
                    saved++;
                }
                catch { failed.Add(a.Name); }
            }

            if (pngs.Count > 0)
            {
                var pdf = await PicturesToPdfAsync(pngs, body, failed).ConfigureAwait(true);
                if (pdf is { } p)
                {
                    await Task.Run(() => File.WriteAllBytes(AttachmentExport.UniquePath(folder, p.Name), p.Bytes)).ConfigureAwait(true);
                    saved++;
                }
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not save the attachments: {ex.Message}";
            return;
        }

        var note = string.Concat(
            failed.Count > 0 ? $" · could not save {string.Join(", ", failed)}" : "",
            blocked.Count > 0 ? $" · left {string.Join(", ", blocked.Select(b => b.Name))} for Outlook (programs or scripts)" : "");
        Status = $"Saved {saved} file(s) to {folder}" + (pngs.Count > 0 ? " · pictures as one PDF" : "") + note;
    }

    /// <summary>
    /// Fetches the PNG attachments and lays them out one per page in a PDF.
    /// A picture that cannot be read is named in <paramref name="failed"/>
    /// and left out; null when none could be.
    /// </summary>
    private async Task<(string Name, byte[] Bytes)?> PicturesToPdfAsync(
        IReadOnlyList<MailAttachment> pngs, MailBody body, List<string> failed)
    {
        var images = new List<PngImage>();
        var names = new List<string>();
        foreach (var a in pngs)
        {
            try
            {
                var cached = await _store.SaveAttachmentAsync(SourceOf(a, body), a.Index).ConfigureAwait(true);
                images.Add(await Task.Run(() => PngImage.Decode(File.ReadAllBytes(cached))).ConfigureAwait(true));
                names.Add(a.Name);
            }
            catch { failed.Add(a.Name); }
        }

        if (images.Count == 0) return null;
        var bytes = await Task.Run(() => PngToPdf.Write(images)).ConfigureAwait(true);
        return (AttachmentExport.PdfName(names, body.Subject), bytes);
    }

    private static MailRef SourceOf(MailAttachment a, MailBody body) => a.Source.IsEmpty ? body.Ref : a.Source;
}
