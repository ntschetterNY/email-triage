namespace EmailTriage.App.Input;

/// <summary>
/// Every keyboard-reachable operation. Named by intent rather than by key, so
/// rebinding never touches the code that performs the work.
/// </summary>
public enum TriageAction
{
    None = 0,

    NextMail,
    PrevMail,
    FirstMail,
    LastMail,
    PageDown,
    PageUp,

    /// <summary>Flag as needing action and send it to the action list.</summary>
    MarkActionRequired,

    /// <summary>Explicitly clear the action flag - "I looked, nothing to do".</summary>
    MarkNoAction,

    /// <summary>Open the folder palette. Bound to `k` per the original spec.</summary>
    MoveToFolder,

    /// <summary>Open the snooze palette. Bound to `g` per the original spec.</summary>
    Snooze,

    /// <summary>Reply to everyone. Bound to `r`.</summary>
    ReplyAll,

    /// <summary>Reply to the sender alone. Bound to Shift+R.</summary>
    ReplySender,

    /// <summary>Forward, typing the recipients in. Bound to `f`.</summary>
    Forward,

    /// <summary>Write a new message from scratch, from any tab. Bound to `c`, as in Superhuman.</summary>
    Compose,

    /// <summary>Pick one of the open message's attachments to open. Bound to `v`.</summary>
    OpenAttachment,

    ToggleRead,
    Archive,
    Delete,
    Search,
    Refresh,
    SwitchSection,
    ShowHelp,
    Cancel,
    Confirm,

    /// <summary>Reverses the last move, snooze or archive.</summary>
    Undo,

    // Action-list specific
    AddNote,
    AddBlocker,
    AddAssignment,
    ToggleComplete,
    CyclePriority,
    OpenInOutlook,

    // Action board
    PrevColumn,
    NextColumn,
    StageBack,
    StageForward,
    SetDue,

    /// <summary>Clear the oldest blocker or hand-off on the selected card.</summary>
    ClearWait,

    /// <summary>Draft a chase email to whoever holds the hand-off.</summary>
    Chase,

    /// <summary>Switch the action tab between the board and the By person report.</summary>
    ToggleBoardView,

    // Calendar

    /// <summary>Accept, tentatively accept or decline an invitation, optionally with a note.</summary>
    Rsvp,

    /// <summary>Put the mail or task on the calendar: time for yourself, or a meeting with the people on it.</summary>
    ScheduleTime,

    /// <summary>Join the meeting under way or about to start, from anywhere.</summary>
    JoinMeeting,

    /// <summary>The tab before this one; the reverse of SwitchSection.</summary>
    PrevSection,

    // AI, through the user's own Claude sign-in

    /// <summary>
    /// Have Claude draft the reply: from the list it opens reply-all and
    /// drafts from the conversation; in the composer it turns any notes
    /// already typed into the full message. Nothing is sent automatically.
    /// </summary>
    AiDraftReply,

    /// <summary>Ask the inbox a question in plain language; Claude picks the matches.</summary>
    AiSearch,
}
