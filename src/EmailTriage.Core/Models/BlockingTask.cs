namespace EmailTriage.Core.Models;

/// <summary>Something that must happen before the parent action item can move.</summary>
public sealed class BlockingTask
{
    public long Id { get; set; }
    public long ActionItemId { get; set; }
    public required string Description { get; set; }

    /// <summary>Optional: who or what we are waiting on.</summary>
    public string WaitingOn { get; set; } = "";

    public DateTimeOffset? DueUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }

    public bool IsResolved => ResolvedUtc is not null;

    public bool IsOverdue =>
        !IsResolved && DueUtc is { } due && due < DateTimeOffset.UtcNow;
}
