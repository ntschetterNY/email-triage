using System.Text;
using System.Text.Json;
using EmailTriage.Core.Abstractions;

namespace EmailTriage.Core.Services;

/// <summary>One conversation as the search prompt sees it.</summary>
public sealed record AiSearchRow(
    string Key,
    string Subject,
    string Sender,
    string Address,
    DateTimeOffset LastActivityUtc,
    bool Unread,
    int MessageCount,
    string Snippet);

/// <summary>What came back: matching conversation keys, best first, and a status line.</summary>
public sealed record AiSearchResult(IReadOnlyList<string> Keys, string Answer);

/// <summary>
/// Natural-language search over the triage list. The whole list (metadata plus
/// whatever body text is already cached) goes to Claude in one prompt, and
/// Claude picks the conversations that answer the question - so "what am I
/// still waiting on from the architect?" works where fuzzy matching cannot.
/// </summary>
public sealed class AiSearchService
{
    private readonly IAiAssistant _assistant;

    /// <summary>Body text per conversation in the prompt; enough to catch the topic.</summary>
    public const int MaxSnippetChars = 280;

    public AiSearchService(IAiAssistant assistant) => _assistant = assistant;

    public async Task<AiSearchResult> SearchAsync(
        string query, IReadOnlyList<AiSearchRow> rows, DateTimeOffset now,
        string? model = null, CancellationToken ct = default)
    {
        var answer = await _assistant.AskAsync(BuildPrompt(query, rows, now), model, ct).ConfigureAwait(false);
        var result = ParseResponse(answer);

        // Only keys that exist, in the model's order, each once - a misremembered
        // key must never make the list show the wrong conversation.
        var known = rows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = result.Keys.Where(k => known.Contains(k) && seen.Add(k)).ToList();

        return new AiSearchResult(keys, result.Answer);
    }

    /// <summary>Pure, so tests can see exactly what the model is asked.</summary>
    public static string BuildPrompt(string query, IReadOnlyList<AiSearchRow> rows, DateTimeOffset now)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are the search engine inside an email client. Answer directly; do not use any tools.");
        sb.AppendLine($"The local time now is {now.ToLocalTime():dddd d MMMM yyyy, HH:mm}.");
        sb.AppendLine();
        sb.AppendLine($"The user asked: {query}");
        sb.AppendLine();
        sb.AppendLine("Below is their inbox, one conversation per line as JSON:");
        sb.AppendLine("key, subject, from, address, when (last activity), unread, msgs (count), snippet (may be empty).");
        sb.AppendLine();
        sb.AppendLine("Pick every conversation that answers the question, best match first. Reply with only this JSON, no other text:");
        sb.AppendLine("{\"keys\": [\"...\"], \"answer\": \"one short sentence for the status line - what you took the question to mean and what you found\"}");
        sb.AppendLine("Use only key values from the list. If nothing matches, return an empty keys array and say so in answer.");
        sb.AppendLine();

        foreach (var row in rows)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                key = row.Key,
                subject = row.Subject,
                from = row.Sender,
                address = row.Address,
                when = row.LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm ddd"),
                unread = row.Unread,
                msgs = row.MessageCount,
                snippet = Squeeze(row.Snippet, MaxSnippetChars),
            }));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the model's JSON, tolerating fences or prose around it. Public and
    /// pure so the parsing is testable.
    /// </summary>
    public static AiSearchResult ParseResponse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new AiUnavailableException("Claude answered in a shape this app could not read - try again.");

        try
        {
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = doc.RootElement;

            var keys = new List<string>();
            if (root.TryGetProperty("keys", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } key)
                        keys.Add(key);
                }
            }

            var text = root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? ""
                : "";

            return new AiSearchResult(keys, text);
        }
        catch (JsonException ex)
        {
            throw new AiUnavailableException("Claude answered in a shape this app could not read - try again.", ex);
        }
    }

    private static string Squeeze(string text, int max)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max];
    }
}
