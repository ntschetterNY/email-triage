namespace EmailTriage.Core.Models;

/// <summary>
/// One of the signatures Outlook keeps (File › Options › Mail › Signatures),
/// as the file it saves it in. <see cref="None"/> stands for "no signature".
/// </summary>
public sealed record MailSignature(string Name, string HtmlPath)
{
    /// <summary>Take Outlook's signature off the message and put nothing in its place.</summary>
    public static MailSignature None { get; } = new("", "");

    public bool IsNone => HtmlPath.Length == 0;
}
