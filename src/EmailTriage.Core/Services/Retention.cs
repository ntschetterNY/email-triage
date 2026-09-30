namespace EmailTriage.Core.Services;

/// <summary>
/// How long finished records stay in the database once nothing shows them:
/// done cards past the log's reach, snoozes that have come back, scheduled
/// sends that went. Zero or less keeps a kind forever.
/// </summary>
public sealed record RetentionPolicy(int DoneDays = 90, int SnoozeDays = 30, int ScheduledSendDays = 30)
{
    public static readonly RetentionPolicy Default = new();
}

public static class Retention
{
    /// <summary>The moment before which records of a kind go, or null to keep them all.</summary>
    public static DateTimeOffset? Cutoff(DateTimeOffset nowUtc, int days) =>
        days > 0 ? nowUtc.AddDays(-days) : null;
}

/// <summary>What one sweep removed.</summary>
public sealed record RetentionResult(int DoneRemoved, int SnoozesRemoved, int SendsRemoved, bool Vacuumed)
{
    public int TotalRemoved => DoneRemoved + SnoozesRemoved + SendsRemoved;
}
