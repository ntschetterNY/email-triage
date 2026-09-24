namespace EmailTriage.Core.Models;

/// <summary>
/// A file attached to a message. <see cref="Index"/> is Outlook's 1-based
/// position, used to fetch the file when the user opens it.
/// </summary>
public sealed record MailAttachment(int Index, string Name, long Size)
{
    /// <summary>The message it belongs to - a thread shows attachments from several.</summary>
    public MailRef Source { get; init; }

    public string SizeDisplay => Size switch
    {
        <= 0 => "",
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size / (1024.0 * 1024):0.0} MB",
    };

    /// <summary>
    /// File types Outlook itself refuses to open, because opening one runs it.
    /// These are left for Outlook to handle rather than launched from here.
    /// </summary>
    public bool IsBlockedType => BlockedExtensions.Contains(Path.GetExtension(Name));

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ade", ".adp", ".app", ".application", ".appref-ms", ".bas", ".bat", ".cer", ".chm", ".cmd",
        ".cnt", ".com", ".cpl", ".crt", ".csh", ".der", ".diagcab", ".exe", ".fxp", ".gadget", ".grp",
        ".hlp", ".hpj", ".hta", ".htc", ".inf", ".ins", ".isp", ".its", ".jar", ".jnlp", ".js", ".jse",
        ".ksh", ".lnk", ".mad", ".maf", ".mag", ".mam", ".maq", ".mar", ".mas", ".mat", ".mau", ".mav",
        ".maw", ".mcf", ".mda", ".mdb", ".mde", ".mdt", ".mdw", ".mdz", ".msc", ".msh", ".msh1",
        ".msh2", ".mshxml", ".msh1xml", ".msh2xml", ".msi", ".msp", ".mst", ".msu", ".ops", ".osd",
        ".pcd", ".pif", ".pl", ".plg", ".prf", ".prg", ".printerexport", ".ps1", ".ps1xml", ".ps2",
        ".ps2xml", ".psc1", ".psc2", ".psd1", ".psdm1", ".pst", ".py", ".pyc", ".pyo", ".pyw", ".pyz",
        ".pyzw", ".reg", ".scf", ".scr", ".sct", ".settingcontent-ms", ".shb", ".shs", ".theme", ".tmp",
        ".udl", ".url", ".vb", ".vbe", ".vbp", ".vbs", ".vhd", ".vhdx", ".vsmacros", ".vsw", ".webpnp",
        ".website", ".ws", ".wsb", ".wsc", ".wsf", ".wsh", ".xbap", ".xll", ".xnk",
    };
}
