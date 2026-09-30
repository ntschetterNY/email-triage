namespace EmailTriage.Core.Models;

/// <summary>
/// What the user said about a mail as they flagged it: the one line of what
/// has to happen, who has the ball, when it is due and when to chase. Every
/// field is optional, so an empty request is exactly a plain flag.
/// </summary>
public sealed record CaptureRequest
{
    /// <summary>The card's title; empty keeps the mail's subject.</summary>
    public string Title { get; init; } = "";

    /// <summary>Who the item waits on; null means it is on the user.</summary>
    public Recipient? Who { get; init; }

    /// <summary>
    /// The wait is something they must do before the user can move (a
    /// blocker) rather than work handed to them. Both put the card in Waiting.
    /// </summary>
    public bool IsBlocker { get; init; }

    /// <summary>When the item itself has to be finished.</summary>
    public DateTimeOffset? DueUtc { get; init; }

    /// <summary>The day the nudge goes out: the wait's date, which fills the Follow up column.</summary>
    public DateTimeOffset? FollowUpUtc { get; init; }

    public ActionPriority Priority { get; init; } = ActionPriority.Normal;

    /// <summary>Working notes; empty leaves any existing notes alone.</summary>
    public string Notes { get; init; } = "";

    public bool HasWait => Who is { } who && who.Display.Trim().Length > 0;
}
