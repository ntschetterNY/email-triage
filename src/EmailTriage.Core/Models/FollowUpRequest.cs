namespace EmailTriage.Core.Models;

/// <summary>
/// A follow-up attached to a message as it is sent: who owes what, by when.
/// Filed as an <see cref="Assignment"/> on the answered mail's action item,
/// where the board's chase can pick it up.
/// </summary>
public sealed record FollowUpRequest
{
    public required Recipient Person { get; init; }
    public required string Task { get; init; }
    public required DateTimeOffset DueUtc { get; init; }

    /// <summary>When the person hears about it: now, or the scheduled send time.</summary>
    public required DateTimeOffset ToldUtc { get; init; }
}
