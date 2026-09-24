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
}
