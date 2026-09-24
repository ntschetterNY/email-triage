namespace EmailTriage.Core.Models;

/// <summary>
/// A reply Outlook has already constructed (quoted history, headers and all),
/// held open so the user can type into it and send without leaving the app.
/// </summary>
public sealed record ReplyDraft
{
    public required DraftRef Ref { get; init; }
    public required ReplyScope Scope { get; init; }
    public required string Subject { get; init; }
    public required IReadOnlyList<Recipient> To { get; init; }
    public required IReadOnlyList<Recipient> Cc { get; init; }

    /// <summary>The source message, so the UI can show what is being answered.</summary>
    public required MailRef InReplyTo { get; init; }

    public string RecipientSummary
    {
        get
        {
            var to = string.Join(", ", To.Select(r => r.Display));
            return Cc.Count == 0 ? to : $"{to}  ·  cc {string.Join(", ", Cc.Select(r => r.Display))}";
        }
    }
}
