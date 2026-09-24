using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum Section { Triage, Actions }

/// <summary>
/// Owns startup, section switching and key routing. Keys are dispatched here
/// rather than bound in XAML so that modal state - palette, composer, inline
/// editor - can claim the keyboard cleanly.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly SnoozeScheduler _scheduler;
    private readonly ISnoozeRepository _snoozes;

    public KeyMap Keys { get; }
    public TriageViewModel Triage { get; }
    public ActionItemsViewModel Actions { get; }

    [ObservableProperty] private Section _section = Section.Triage;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string _connectionStatus = "Starting...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _fatalError = "";
    [ObservableProperty] private int _pendingSnoozeCount;

    public MainViewModel(
        IMailStore store,
        TriageViewModel triage,
        ActionItemsViewModel actions,
        SnoozeScheduler scheduler,
        ISnoozeRepository snoozes,
        KeyMap keys)
    {
        _store = store;
        _scheduler = scheduler;
        _snoozes = snoozes;

        Triage = triage;
        Actions = actions;
        Keys = keys;

        Triage.Composer.Sent += async (_, _) =>
        {
            Triage.Status = "Reply sent";
            await Triage.LoadAsync().ConfigureAwait(true);
        };
    }

    public string StatusText => Section == Section.Triage ? Triage.Status : Actions.Status;

    public async Task InitialiseAsync()
    {
        try
        {
            ConnectionStatus = "Connecting to Outlook...";
            await _store.ConnectAsync().ConfigureAwait(true);

            IsConnected = true;
            ConnectionStatus = "Connected";

            _store.NewMailArrived += async (_, _) =>
            {
                if (Section == Section.Triage && !Triage.Palette.IsOpen && !Triage.Composer.IsOpen)
                    await Triage.LoadAsync().ConfigureAwait(true);
            };

            _scheduler.Restored += async (_, e) =>
            {
                Triage.Status = $"Back in your inbox: {e.Entry.Subject}";
                await RefreshSnoozeCountAsync().ConfigureAwait(true);
                if (Section == Section.Triage) await Triage.LoadAsync().ConfigureAwait(true);
            };

            _scheduler.RestoreFailed += (_, e) =>
                Triage.Status = $"Could not return \"{e.Entry.Subject}\": {e.Error}";

            _scheduler.Start();

            await Triage.LoadAsync().ConfigureAwait(true);
            await Actions.LoadAsync().ConfigureAwait(true);
            await RefreshSnoozeCountAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = "Not connected";
            FatalError = ex.Message;
        }
    }

    private async Task RefreshSnoozeCountAsync()
    {
        try
        {
            var pending = await _snoozes.GetPendingAsync().ConfigureAwait(true);
            PendingSnoozeCount = pending.Count;
        }
        catch { /* the count is decoration; never surface a failure for it */ }
    }

    partial void OnSectionChanged(Section value)
    {
        OnPropertyChanged(nameof(StatusText));
        _ = value == Section.Actions ? Actions.LoadAsync() : Triage.LoadAsync();
    }

    /// <summary>
    /// Routes a keystroke. Returns true when it was consumed, so the view can
    /// stop it reaching the focused control.
    /// </summary>
    public async Task<bool> HandleKeyAsync(KeyStroke stroke, bool ctrlEnter)
    {
        if (stroke.IsEmpty) return false;

        var action = Keys.Resolve(stroke);

        // Modal surfaces claim the keyboard first: while one is open, ordinary
        // letters are text the user is typing, not commands.
        if (Triage.Composer.IsOpen) return await HandleComposerKeyAsync(action, ctrlEnter).ConfigureAwait(true);
        if (Triage.Palette.IsOpen) return await HandlePaletteKeyAsync(action, ctrlEnter).ConfigureAwait(true);
        if (Actions.Editor != EditorMode.None) return await HandleEditorKeyAsync(action, ctrlEnter).ConfigureAwait(true);

        if (IsHelpVisible)
        {
            IsHelpVisible = false;
            return true;
        }

        if (Triage.IsSearching && Section == Section.Triage)
        {
            if (action is TriageAction.Cancel) { Triage.IsSearching = false; Triage.SearchQuery = ""; return true; }
            if (action is TriageAction.Confirm) { Triage.IsSearching = false; return true; }
            if (action is TriageAction.NextMail or TriageAction.PrevMail)
            {
                Triage.Move(action == TriageAction.NextMail ? 1 : -1);
                return true;
            }
            return false; // let the search box receive the character
        }

        return await HandleGlobalKeyAsync(action).ConfigureAwait(true);
    }

    private async Task<bool> HandleComposerKeyAsync(TriageAction action, bool ctrlEnter)
    {
        if (ctrlEnter) { await Triage.Composer.SendAsync().ConfigureAwait(true); return true; }
        if (action == TriageAction.Cancel) { await Triage.Composer.DiscardAsync().ConfigureAwait(true); return true; }
        return false;
    }

    private async Task<bool> HandlePaletteKeyAsync(TriageAction action, bool ctrlEnter)
    {
        switch (action)
        {
            case TriageAction.Cancel:
                Triage.Palette.Close();
                return true;

            case TriageAction.Confirm:
                await Triage.ConfirmPaletteAsync(ctrlEnter).ConfigureAwait(true);
                return true;

            case TriageAction.NextMail:
                Triage.Palette.MoveSelection(1);
                return true;

            case TriageAction.PrevMail:
                Triage.Palette.MoveSelection(-1);
                return true;

            default:
                // Everything else is search text for the palette's own box.
                return false;
        }
    }

    private async Task<bool> HandleEditorKeyAsync(TriageAction action, bool ctrlEnter)
    {
        if (ctrlEnter) { await Actions.CommitEditorAsync().ConfigureAwait(true); return true; }
        if (action == TriageAction.Cancel) { Actions.CloseEditor(); return true; }
        return false;
    }

    private async Task<bool> HandleGlobalKeyAsync(TriageAction action)
    {
        switch (action)
        {
            case TriageAction.SwitchSection:
                Section = Section == Section.Triage ? Section.Actions : Section.Triage;
                return true;

            case TriageAction.ShowHelp:
                IsHelpVisible = true;
                return true;

            case TriageAction.Refresh:
                if (Section == Section.Triage) await Triage.LoadAsync().ConfigureAwait(true);
                else await Actions.LoadAsync().ConfigureAwait(true);
                await RefreshSnoozeCountAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Cancel:
                Triage.CancelOverlays();
                return true;
        }

        return Section == Section.Triage
            ? await HandleTriageKeyAsync(action).ConfigureAwait(true)
            : await HandleActionsKeyAsync(action).ConfigureAwait(true);
    }

    private async Task<bool> HandleTriageKeyAsync(TriageAction action)
    {
        switch (action)
        {
            case TriageAction.NextMail: Triage.Move(1); return true;
            case TriageAction.PrevMail: Triage.Move(-1); return true;
            case TriageAction.PageDown: Triage.Move(10); return true;
            case TriageAction.PageUp: Triage.Move(-10); return true;
            case TriageAction.FirstMail: Triage.MoveToEnd(false); return true;
            case TriageAction.LastMail: Triage.MoveToEnd(true); return true;

            case TriageAction.MarkActionRequired:
                await Triage.ToggleActionRequiredAsync(true).ConfigureAwait(true);
                await Actions.LoadAsync().ConfigureAwait(true);
                return true;

            case TriageAction.MarkNoAction:
                await Triage.ToggleActionRequiredAsync(false).ConfigureAwait(true);
                return true;

            case TriageAction.MoveToFolder:
                await Triage.OpenFolderPaletteAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Snooze:
                Triage.OpenSnoozePalette();
                return true;

            case TriageAction.ReplyAll:
                await Triage.StartReplyAsync(ReplyScope.All).ConfigureAwait(true);
                return true;

            case TriageAction.ReplySender:
                await Triage.StartReplyAsync(ReplyScope.SenderOnly).ConfigureAwait(true);
                return true;

            case TriageAction.ToggleRead:
                await Triage.ToggleReadAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Archive:
                await Triage.ArchiveAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Undo:
                await Triage.UndoAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Search:
                Triage.IsSearching = true;
                return true;

            default:
                return false;
        }
    }

    private async Task<bool> HandleActionsKeyAsync(TriageAction action)
    {
        switch (action)
        {
            case TriageAction.NextMail: Actions.Move(1); return true;
            case TriageAction.PrevMail: Actions.Move(-1); return true;

            case TriageAction.AddNote: Actions.OpenEditor(EditorMode.Note); return true;
            case TriageAction.AddBlocker: Actions.OpenEditor(EditorMode.Blocker); return true;
            case TriageAction.AddAssignment: Actions.OpenEditor(EditorMode.Assignment); return true;

            case TriageAction.ToggleComplete:
                await Actions.ToggleCompleteAsync().ConfigureAwait(true);
                return true;

            case TriageAction.CyclePriority:
                await Actions.CyclePriorityAsync().ConfigureAwait(true);
                return true;

            case TriageAction.OpenInOutlook:
                await Actions.OpenInOutlookAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Delete:
                await Actions.DeleteSelectedAsync().ConfigureAwait(true);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Rows for the help overlay, grouped for readability.</summary>
    public IReadOnlyList<(string Group, string Keys, string Description)> HelpRows => new[]
    {
        ("Move",    $"{Keys.Describe(TriageAction.NextMail)} / {Keys.Describe(TriageAction.PrevMail)}", "Next / previous message"),
        ("Move",    Keys.Describe(TriageAction.SwitchSection), "Switch between Triage and Action items"),
        ("Move",    Keys.Describe(TriageAction.Search), "Filter the list"),

        ("Triage",  Keys.Describe(TriageAction.MarkActionRequired), "Needs action - send to the action list"),
        ("Triage",  Keys.Describe(TriageAction.MarkNoAction), "No action needed"),
        ("Triage",  Keys.Describe(TriageAction.MoveToFolder), "Move to folder (type to search, Ctrl+Enter creates)"),
        ("Triage",  Keys.Describe(TriageAction.Snooze), "Come back to this later"),
        ("Triage",  Keys.Describe(TriageAction.Archive), "Archive"),
        ("Triage",  Keys.Describe(TriageAction.ToggleRead), "Toggle read / unread"),
        ("Triage",  Keys.Describe(TriageAction.Undo), "Undo the last move or snooze"),

        ("Reply",   Keys.Describe(TriageAction.ReplyAll), "Reply to everyone"),
        ("Reply",   Keys.Describe(TriageAction.ReplySender), "Reply to the sender only"),
        ("Reply",   "Ctrl+Enter", "Send"),

        ("Actions", Keys.Describe(TriageAction.AddNote), "Edit notes"),
        ("Actions", Keys.Describe(TriageAction.AddBlocker), "Add a blocker"),
        ("Actions", Keys.Describe(TriageAction.AddAssignment), "Assign someone a task"),
        ("Actions", Keys.Describe(TriageAction.ToggleComplete), "Mark done"),
        ("Actions", Keys.Describe(TriageAction.CyclePriority), "Cycle priority"),
        ("Actions", Keys.Describe(TriageAction.OpenInOutlook), "Open the original in Outlook"),

        ("General", Keys.Describe(TriageAction.Refresh), "Refresh"),
        ("General", Keys.Describe(TriageAction.ShowHelp), "This help"),
        ("General", Keys.Describe(TriageAction.Cancel), "Close / cancel"),
    };
}
