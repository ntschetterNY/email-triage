using EmailTriage.Core.Models;
using EmailTriage.Core.Services;
using Xunit;

namespace EmailTriage.Tests;

/// <summary>
/// The signatures on offer in Settings are read from the folder Outlook
/// keeps them in, and a saved choice resolves to a file - or refuses, by
/// name, when Outlook no longer has it.
/// </summary>
public class OutlookSignaturesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"triage-sig-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    /// <summary>Lays the files down the way Outlook does for one signature.</summary>
    private void Signature(string name, string html = "<p>Regards</p>")
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, $"{name}.htm"), html);
        File.WriteAllText(Path.Combine(_folder, $"{name}.txt"), "Regards");
        File.WriteAllText(Path.Combine(_folder, $"{name}.rtf"), "{\\rtf1 Regards}");
        Directory.CreateDirectory(Path.Combine(_folder, $"{name}_files"));
        File.WriteAllBytes(Path.Combine(_folder, $"{name}_files", "image001.png"), new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Lists_each_signature_once_by_name_in_order()
    {
        Signature("Short");
        Signature("Full (nate@corp.com)");
        Signature("brief");

        var found = OutlookSignatures.Find(_folder);

        Assert.Equal(new[] { "brief", "Full (nate@corp.com)", "Short" }, found.Select(s => s.Name));
        Assert.All(found, s => Assert.EndsWith(".htm", s.HtmlPath));
        Assert.All(found, s => Assert.False(s.IsNone));
    }

    [Fact]
    public void Only_the_htm_counts_so_the_txt_rtf_and_pictures_folder_add_nothing()
    {
        Signature("Short");
        File.WriteAllText(Path.Combine(_folder, "stray.html"), "<p>not Outlook's</p>");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "nothing");

        Assert.Equal(new[] { "Short" }, OutlookSignatures.Find(_folder).Select(s => s.Name));
    }

    [Fact]
    public void No_folder_means_no_signatures_not_an_error()
    {
        Assert.Empty(OutlookSignatures.Find(_folder));
    }

    [Fact]
    public void An_empty_choice_leaves_the_draft_to_outlook()
    {
        Signature("Short");

        Assert.Null(OutlookSignatures.Resolve(OutlookSignatures.OutlookDefault, _folder));
        Assert.Null(OutlookSignatures.Resolve(null, _folder));
        Assert.Null(OutlookSignatures.Resolve("   ", _folder));
    }

    [Fact]
    public void None_resolves_to_no_signature_whatever_its_case()
    {
        var none = OutlookSignatures.Resolve("(None)", _folder);

        Assert.NotNull(none);
        Assert.True(none!.IsNone);
        Assert.Same(MailSignature.None, OutlookSignatures.Resolve(OutlookSignatures.NoSignature, _folder));
    }

    [Fact]
    public void A_name_resolves_to_its_file_whatever_its_case()
    {
        Signature("Short");
        Signature("Full");

        var chosen = OutlookSignatures.Resolve("short", _folder);

        Assert.NotNull(chosen);
        Assert.Equal("Short", chosen!.Name);
        Assert.Equal(Path.Combine(_folder, "Short.htm"), chosen.HtmlPath);
        Assert.True(File.Exists(chosen.HtmlPath));
    }

    [Fact]
    public void A_signature_outlook_no_longer_has_is_refused_by_name_and_points_at_settings()
    {
        Signature("Short");

        var ex = Assert.Throws<InvalidOperationException>(() => OutlookSignatures.Resolve("Full", _folder));

        Assert.Contains("\"Full\"", ex.Message);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public void A_chosen_signature_is_something_to_write_onto_the_draft()
    {
        var untouched = new RecipientOverrides(null, null, null);
        var signed = new RecipientOverrides(null, null, null) { Signature = MailSignature.None };

        Assert.True(untouched.IsEmpty);
        Assert.False(signed.IsEmpty);
    }
}
