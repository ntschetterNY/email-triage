using System.Text;
using EmailTriage.Core.Abstractions;

namespace EmailTriage.Core.Services;

/// <summary>One message of the conversation being answered, ready for the prompt.</summary>
public sealed record DraftMessage(
    string Sender, string Address, DateTimeOffset SentUtc, string Text, bool IsFromUser);

/// <summary>Everything the draft prompt is built from.</summary>
public sealed record DraftContext
{
    public required string Subject { get; init; }

    /// <summary>"reply to everyone", "reply to the sender", "a forward note", "new message".</summary>
    public required string Kind { get; init; }

    /// <summary>Who the message goes to, for salutation and register.</summary>
    public string Recipients { get; init; } = "";

    /// <summary>Notes the user typed to steer the draft; empty means "answer the thread".</summary>
    public string Instructions { get; init; } = "";

    /// <summary>The conversation, newest first. Empty for a new message.</summary>
    public IReadOnlyList<DraftMessage> Messages { get; init; } = Array.Empty<DraftMessage>();

    /// <summary>
    /// A guide to the user's own writing voice, learned from their sent mail
    /// (see <see cref="WritingStyleService"/>). Empty means no guide yet.
    /// </summary>
    public string Style { get; init; } = "";
}

/// <summary>
/// Turns a conversation (and optionally the user's rough notes) into a reply
/// draft. The result is body text only - it goes above Outlook's quoted
/// history, and Outlook appends the signature - and nothing is ever sent
/// without the user pressing send themselves.
/// </summary>
public sealed class AiDraftService
{
    private readonly IAiAssistant _assistant;

    /// <summary>Longest single message fed to the prompt; older walls of text add little.</summary>
    public const int MaxMessageChars = 4000;

    public AiDraftService(IAiAssistant assistant) => _assistant = assistant;

    public async Task<string> DraftAsync(
        DraftContext context, string? model = null, CancellationToken ct = default)
    {
        var answer = await _assistant.AskAsync(BuildPrompt(context), model, ct).ConfigureAwait(false);
        var text = Clean(answer);

        return text.Length > 0
            ? text
            : throw new AiUnavailableException("Claude sent back an empty draft - try again.");
    }

    /// <summary>Pure, so tests can see exactly what the model is asked.</summary>
    public static string BuildPrompt(DraftContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are drafting an email inside the user's mail client. Answer directly; do not use any tools.");
        sb.AppendLine();
        sb.AppendLine($"Write the {context.Kind}" +
                      (context.Recipients.Length > 0 ? $" to {context.Recipients}." : "."));

        sb.AppendLine(context.Instructions.Length > 0
            ? "The user's notes on what it should say - expand them into the full message, and say only what they call for:\n" +
              context.Instructions
            : "Write what the conversation calls for: answer the questions asked of the user and address what is waiting on them.");

        if (context.Style.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Write it in the user's own voice. A guide to how they write, learned from their sent mail:");
            sb.AppendLine(context.Style);
        }

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Write only the body text, ready to send: no subject line, no quoted history, no markdown, no explanation around it.");
        sb.AppendLine("- No sign-off name or signature block; the mail client appends the user's signature.");
        sb.AppendLine("- Match the tone of the user's own messages in the conversation; with none to go on, write briefly and plainly.");
        sb.AppendLine("- Never invent facts, commitments, dates or numbers that are not in the conversation or the notes.");
        sb.AppendLine();
        sb.AppendLine($"Subject: {context.Subject}");

        if (context.Messages.Count > 0)
        {
            sb.AppendLine("Messages marked (you) are the user's own.");
            sb.AppendLine();
            sb.AppendLine("The conversation, newest first:");

            foreach (var m in context.Messages)
            {
                var who = m.IsFromUser ? $"{m.Sender} (you)" : $"{m.Sender} <{m.Address}>";
                sb.AppendLine($"--- From: {who} · {m.SentUtc.ToLocalTime():ddd d MMM yyyy HH:mm}");
                sb.AppendLine(Squeeze(m.Text, MaxMessageChars));
            }
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The model is told to answer with bare body text, but belts and braces:
    /// unwrap a fenced answer and drop a subject line it added anyway.
    /// </summary>
    public static string Clean(string answer)
    {
        var text = answer.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak >= 0 && lastFence > firstBreak)
                text = text[(firstBreak + 1)..lastFence].Trim();
        }

        if (text.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
        {
            var lineEnd = text.IndexOf('\n');
            text = lineEnd >= 0 ? text[(lineEnd + 1)..].TrimStart() : "";
        }

        return text;
    }

    /// <summary>Collapses repeated blank lines and cuts a message down to size.</summary>
    private static string Squeeze(string text, int max)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var blank = 0;
        var truncated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else blank = 0;

            sb.AppendLine(line);
            if (sb.Length >= max)
            {
                truncated = i < lines.Length - 1;
                break;
            }
        }

        var result = sb.ToString().Trim();
        if (result.Length > max)
        {
            result = result[..max];
            truncated = true;
        }

        return truncated ? result + "\n[older text of this message trimmed]" : result;
    }
}
