namespace EmailTriage.Core.Models;

/// <summary>
/// Work handed to another person. Purely local until the user explicitly asks
/// for a draft: nothing here reaches the assignee on its own.
/// </summary>
public sealed class Assignment
{
    public long Id { get; set; }
    public long ActionItemId { get; set; }

    public required string PersonName { get; set; }
    public string PersonEmail { get; set; } = "";
    public required string Task { get; set; }

    public DateTimeOffset? DueUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? DoneUtc { get; set; }

    /// <summary>Set once the user has actually generated a draft for this assignment.</summary>
    public DateTimeOffset? NotifiedUtc { get; set; }

    /// <summary>
    /// The ask went out in the email itself (a follow-up set while sending),
    /// so a chase belongs in that conversation rather than a fresh mail.
    /// </summary>
    public bool InThread { get; set; }

    public bool IsDone => DoneUtc is not null;
    public bool HasBeenDrafted => NotifiedUtc is not null;

    public bool IsOverdue =>
        !IsDone && DueUtc is { } due && due < DateTimeOffset.UtcNow;
}
