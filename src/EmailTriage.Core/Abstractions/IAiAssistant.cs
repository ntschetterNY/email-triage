namespace EmailTriage.Core.Abstractions;

/// <summary>
/// A model that answers one prompt with plain text. The shipped implementation
/// shells out to Claude Code, so the answer comes through the user's own
/// Claude sign-in rather than an API key held by this app.
/// </summary>
public interface IAiAssistant
{
    /// <summary>
    /// Asks with the given model, or the implementation's default when
    /// <paramref name="model"/> is null or empty - so each feature (drafting,
    /// follow-ups, search) can run on the model the user picked for it.
    /// </summary>
    Task<string> AskAsync(string prompt, string? model = null, CancellationToken ct = default);
}

/// <summary>
/// The assistant could not answer - not installed, not signed in, or it
/// returned an error. The message is written for the status line.
/// </summary>
public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }
    public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
}
