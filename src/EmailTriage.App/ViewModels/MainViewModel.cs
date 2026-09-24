using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum Section { Triage, Actions, Calendar }

/// <summary>
/// Owns startup, section switching and key routing. Keys are dispatched here
/// rather than bound in XAML so that modal state - palette, composer, inline
/// editor - can claim the keyboard cleanly.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly SnoozeScheduler _scheduler;
    private readonly ContactDirectory _contacts;
    private readonly ScheduledSender _sender;
    private readonly IScheduledSendRepository _scheduled;
    private readonly ISnoozeRepository _snoozes;

    public KeyMap Keys { get; }
    public TriageViewModel Triage { get; }
    public ActionItemsViewModel Actions { get; }
    public CalendarViewModel Calendar { get; }

    [ObservableProperty] private Section _section = Section.Triage;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string _connectionStatus = "Starting...";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _fatalError = "";
    [ObservableProperty] private int _pendingSnoozeCount;
    [ObservableProperty] private int _pendingScheduledCount;

    public MainViewModel(
        IMailStore store,
        TriageViewModel triage,
        ActionItemsViewModel actions,
        SnoozeScheduler scheduler,
        ISnoozeRepository snoozes,
        KeyMap keys,
        ContactDirectory contacts,
        ScheduledSender sender,
        IScheduledSendRepository scheduled,
        CalendarViewModel calendar)
    {
        _contacts = contacts;
        _sender = sender;
        _scheduled = scheduled;
        _store = store;
        _scheduler = scheduler;
        _snoozes = snoozes;

        Triage = triage;
        Actions = actions;
        Calendar = calendar;
        Keys = keys;

        // The status bar shows the tab on screen, and keeps up as that tab's line changes.
        Triage.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TriageViewModel.Status)) OnPropertyChanged(nameof(StatusText)); };
        Actions.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActionItemsViewModel.Status)) OnPropertyChanged(nameof(StatusText)); };
        Calendar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CalendarViewModel.Status)) OnPropertyChanged(nameof(StatusText)); };

        // Answering, blocking time or undoing a block: the agenda and strip reread.
        Triage.CalendarChanged += (_, _) => _ = Calendar.RefreshQuietlyAsync();

        Triage.Composer.Sent += async (_, sent) =>
        {
            // Sent from the action board: stay there and show the thread with it in.
            if (Section == Section.Actions)
            {
                Actions.Status = sent.Message;
                Actions.RefreshSelectedThread();
            }
            else if (Section == Section.Calendar)
            {
                Calendar.Status = sent.Message;
            }

            // Ctrl+Shift+Enter: send & mark done, archiving the conversation replied to.
            var status = sent.Message;
            if (sent.MarkDone)
            {
                await Triage.ArchiveConversationOfAsync(sent.InReplyTo).ConfigureAwait(true);
                status = $"{sent.Message} · {Triage.Status}";
            }

            await Triage.LoadAsync().ConfigureAwait(true);
            Triage.Status = status;
            await RefreshScheduledCountAsync().ConfigureAwait(true);
        };
    }

    public string StatusText => Section switch
    {
        Section.Actions => Actions.Status,
        Section.Calendar => Calendar.Status,
        _ => Triage.Status,
    };

    /// <summary>A new message, from any tab: Ctrl+N or the button in the top bar.</summary>
    public async Task ComposeAsync()
    {
        if (Triage.Composer.IsOpen) return;
        if (await Triage.StartNewMailAsync().ConfigureAwait(true) is { } problem) SetStatus(problem);
    }

    /// <summary>Reports on the status line of whichever tab is showing.</summary>
    private void SetStatus(string message)
    {
        switch (Section)
        {
            case Section.Actions: Actions.Status = message; break;
            case Section.Calendar: Calendar.Status = message; break;
            default: Triage.Status = message; break;
        }
    }

    public async Task InitialiseAsync()
    {
        _ui = SynchronizationContext.Current;

        try
        {
            ConnectionStatus = "Connecting to Outlook...";
            await _store.ConnectAsync().ConfigureAwait(true);

            IsConnected = true;
            ConnectionStatus = "Connected";

            // Autocomplete works from the cache at once, and fills out as
            // Outlook's contacts and directory are read in the background.
            _ = _contacts.StartLoading();

            // Both of these fire on background threads. The list is bound to
            // WPF, which rejects changes from any thread but its own, so every
            // reaction is posted back to the UI thread first.
            _store.InboxChanged += (_, _) => OnUi(RefreshTriageLiveAsync);

            Triage.Palette.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PaletteViewModel.IsOpen)) _ = CatchUpAsync();
            };
            Triage.Composer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ComposerViewModel.IsOpen)) _ = CatchUpAsync();
            };

            _scheduler.Restored += (_, e) => OnUi(async () =>
            {
                Triage.Status = $"Back in your inbox: {e.Entry.Subject}";
                await RefreshSnoozeCountAsync().ConfigureAwait(true);
                if (Section == Section.Triage) await Triage.RefreshQuietlyAsync().ConfigureAwait(true);
            });

            _scheduler.RestoreFailed += (_, e) => OnUi(() =>
            {
                Triage.Status = $"Could not return \"{e.Entry.Subject}\": {e.Error}";
                return Task.CompletedTask;
            });

            _scheduler.Start();

            _sender.Settled += (_, outcome) => OnUi(async () =>
            {
                var subject = outcome.Entry.Subject;
                Triage.Status = outcome.State switch
                {
                    ScheduledSendState.Sent when outcome.Note.Length == 0 => $"Scheduled send went out: {subject}",
                    ScheduledSendState.Held => $"Held for your review (opened in Outlook): {subject} - {outcome.Note}",
                    ScheduledSendState.Failed => $"Scheduled send failed, opened in Outlook: {subject} - {outcome.Note}",
                    _ => $"Scheduled send for \"{subject}\": {outcome.Note}",
                };
                await RefreshScheduledCountAsync().ConfigureAwait(true);
            });
            _sender.Start();

            await Triage.LoadAsync().ConfigureAwait(true);
            await Actions.LoadAsync().ConfigureAwait(true);

            // The strip in the top bar needs the calendar whichever tab is showing.
            await Calendar.RefreshQuietlyAsync().ConfigureAwait(true);
            Calendar.Start();

            await RefreshSnoozeCountAsync().ConfigureAwait(true);
            await RefreshScheduledCountAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = "Not connected";
            FatalError = ex.Message;
        }
    }

    private SynchronizationContext? _ui;

    /// <summary>Set when a live refresh was held back, so it can run once the way is clear.</summary>
    private bool _refreshHeld;

    private void OnUi(Func<Task> work)
    {
        if (_ui is null) { _ = work(); return; }
        _ui.Post(_ => _ = work(), null);
    }

    /// <summary>
    /// Keeps the triage list in step with Outlook. Held while a palette or the
    /// composer is open, because those act on the selected message and it must
    /// not change underneath them.
    /// </summary>
    private async Task RefreshTriageLiveAsync()
    {
        if (Section != Section.Triage || Triage.Palette.IsOpen || Triage.Composer.IsOpen)
        {
            _refreshHeld = true;
            return;
        }

        _refreshHeld = false;
        await Triage.RefreshQuietlyAsync().ConfigureAwait(true);
    }

    private async Task CatchUpAsync()
    {
        if (_refreshHeld) await RefreshTriageLiveAsync().ConfigureAwait(true);
    }

    private async Task RefreshScheduledCountAsync()
    {
        try { PendingScheduledCount = (await _scheduled.GetPendingAsync().ConfigureAwait(true)).Count; }
        catch { /* decoration only */ }
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
        if (value == Section.Triage) _refreshHeld = false;
        _ = value switch
        {
            Section.Actions => Actions.LoadAsync(),
            Section.Calendar => Calendar.LoadAsync(),
            _ => Triage.LoadAsync(),
        };
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
        if (Triage.Composer.IsOpen) return await HandleComposerKeyAsync(stroke, action, ctrlEnter).ConfigureAwait(true);
        if (Triage.Palette.IsOpen)
        {
            // "3 pm" must reach the box, not move the highlight via `p`.
            if (stroke.IsTyping) return false;
            return await HandlePaletteKeyAsync(action, ctrlEnter).ConfigureAwait(true);
        }
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
            if (action is TriageAction.NextMail or TriageAction.PrevMail && !stroke.IsTyping)
            {
                Triage.Move(action == TriageAction.NextMail ? 1 : -1);
                return true;
            }
            return false; // let the search box receive the character
        }

        return await HandleGlobalKeyAsync(action).ConfigureAwait(true);
    }

    private async Task<bool> HandleComposerKeyAsync(KeyStroke stroke, TriageAction action, bool ctrlEnter)
    {
        var composer = Triage.Composer;

        // Superhuman's compose keys. Ctrl+Shift+Enter is send & mark done;
        // checked before plain Ctrl+Enter, which it would otherwise also match.
        if (ctrlEnter && stroke.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            await composer.SendAsync(markDone: true).ConfigureAwait(true);
            return true;
        }

        if (ctrlEnter) { await composer.SendAsync().ConfigureAwait(true); return true; }

        if (stroke.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            switch (stroke.Key)
            {
                case System.Windows.Input.Key.L: composer.ToggleSchedule(); return true;
                case System.Windows.Input.Key.OemComma: await composer.DiscardAsync().ConfigureAwait(true); return true;
                case System.Windows.Input.Key.O: composer.RequestFocus(RecipientField.To); return true;
                case System.Windows.Input.Key.C: composer.RequestFocus(RecipientField.Cc); return true;
                case System.Windows.Input.Key.B: composer.RequestFocus(RecipientField.Bcc); return true;
                case System.Windows.Input.Key.M: composer.RequestFocus(RecipientField.Body); return true;
                case System.Windows.Input.Key.S when composer.IsNew: composer.RequestFocus(RecipientField.Subject); return true;
            }
        }

        if (action == TriageAction.Cancel && composer.IsScheduling && !composer.HasSuggestions)
        {
            composer.ToggleSchedule();
            return true;
        }

        // While contact suggestions are showing, the arrows pick one and
        // Enter or Tab takes it; Esc just closes the list.
        if (composer.HasSuggestions && stroke.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            switch (stroke.Key)
            {
                case System.Windows.Input.Key.Down: composer.MoveSuggestion(1); return true;
                case System.Windows.Input.Key.Up: composer.MoveSuggestion(-1); return true;
                case System.Windows.Input.Key.Return:
                case System.Windows.Input.Key.Tab: composer.AcceptSuggestion(); return true;
                case System.Windows.Input.Key.Escape: composer.CloseSuggestions(); return true;
            }
        }

        if (action == TriageAction.Cancel) { await Triage.Composer.DiscardAsync().ConfigureAwait(true); return true; }
        return false;
    }

    private async Task<bool> HandlePaletteKeyAsync(TriageAction action, bool ctrlEnter)
    {
        // Ctrl+Enter has no binding of its own - only plain Enter maps to
        // Confirm - so it must be caught before the action switch.
        if (ctrlEnter)
        {
            await ConfirmPaletteAsync(forceCreate: true).ConfigureAwait(true);
            return true;
        }

        switch (action)
        {
            case TriageAction.Cancel:
                Triage.Palette.Close();
                return true;

            case TriageAction.Confirm:
                await ConfirmPaletteAsync(forceCreate: false).ConfigureAwait(true);
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

    /// <summary>
    /// The answer and schedule palettes also open from the board and the
    /// Calendar tab; what they report belongs on the line of the tab showing.
    /// </summary>
    private async Task ConfirmPaletteAsync(bool forceCreate)
    {
        var before = Triage.Status;
        await Triage.ConfirmPaletteAsync(forceCreate).ConfigureAwait(true);
        if (Section != Section.Triage && Triage.Status != before) SetStatus(Triage.Status);
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
                Section = (Section)(((int)Section + 1) % SectionCount);
                return true;

            case TriageAction.PrevSection:
                Section = (Section)(((int)Section + SectionCount - 1) % SectionCount);
                return true;

            case TriageAction.ShowHelp:
                IsHelpVisible = true;
                return true;

            case TriageAction.JoinMeeting:
                SetStatus(await Calendar.JoinNowAsync().ConfigureAwait(true));
                return true;

            case TriageAction.Compose:
                await ComposeAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Refresh:
                switch (Section)
                {
                    case Section.Triage: await Triage.LoadAsync().ConfigureAwait(true); break;
                    case Section.Actions: await Actions.LoadAsync().ConfigureAwait(true); break;
                    case Section.Calendar: await Calendar.LoadAsync().ConfigureAwait(true); break;
                }
                await RefreshSnoozeCountAsync().ConfigureAwait(true);
                return true;

            case TriageAction.Cancel:
                // On the board, Esc drops the "waiting on" filter.
                if (Section == Section.Actions) await Actions.ClearFilterAsync().ConfigureAwait(true);
                else if (Section == Section.Triage) Triage.CancelOverlays();
                return true;
        }

        return Section switch
        {
            Section.Actions => await HandleActionsKeyAsync(action).ConfigureAwait(true),
            Section.Calendar => await HandleCalendarKeyAsync(action).ConfigureAwait(true),
            _ => await HandleTriageKeyAsync(action).ConfigureAwait(true),
        };
    }

    private static readonly int SectionCount = Enum.GetValues<Section>().Length;

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

            // Enter replies to everyone, as in Superhuman; it is Confirm in
            // palettes and search, which claim it before this.
            case TriageAction.Confirm:
            case TriageAction.ReplyAll:
                await Triage.StartReplyAsync(ReplyScope.All).ConfigureAwait(true);
                return true;

            case TriageAction.ReplySender:
                await Triage.StartReplyAsync(ReplyScope.SenderOnly).ConfigureAwait(true);
                return true;

            case TriageAction.Forward:
                await Triage.StartReplyAsync(ReplyScope.Forward).ConfigureAwait(true);
                return true;

            case TriageAction.OpenAttachment:
                await Triage.OpenAttachmentPaletteAsync().ConfigureAwait(true);
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

            case TriageAction.Rsvp:
                Triage.OpenRsvpForSelected();
                return true;

            case TriageAction.ScheduleTime:
                Triage.OpenScheduleForSelected();
                return true;

            default:
                return false;
        }
    }

    private async Task<bool> HandleCalendarKeyAsync(TriageAction action)
    {
        switch (action)
        {
            case TriageAction.NextMail: Calendar.Move(1); return true;
            case TriageAction.PrevMail: Calendar.Move(-1); return true;
            case TriageAction.PageDown: Calendar.Move(10); return true;
            case TriageAction.PageUp: Calendar.Move(-10); return true;
            case TriageAction.FirstMail: Calendar.MoveToEnd(false); return true;
            case TriageAction.LastMail: Calendar.MoveToEnd(true); return true;

            // Enter goes to the meeting: its Teams or Zoom link, else Outlook.
            case TriageAction.Confirm: await Calendar.ActivateAsync().ConfigureAwait(true); return true;
            case TriageAction.OpenInOutlook: await Calendar.OpenInOutlookAsync().ConfigureAwait(true); return true;

            case TriageAction.Rsvp:
                AnswerSelectedMeeting();
                return true;

            // Undoes a block made with s, like everywhere else.
            case TriageAction.Undo:
                await Triage.UndoAsync().ConfigureAwait(true);
                Calendar.Status = Triage.Status;
                return true;

            default:
                return false;
        }
    }

    /// <summary>y on the Calendar tab: answer the meeting straight from the calendar.</summary>
    public void AnswerSelectedMeeting()
    {
        if (Calendar.Selected?.Event is not { } ev) return;

        if (!ev.CanRespond)
        {
            Calendar.Status = ev.IsOrganizer
                ? "This is your own meeting - there is nobody to answer"
                : "Nothing to answer - this is an appointment, not an invitation";
            return;
        }

        Triage.OpenRsvpPalette(new RsvpTarget(
            ev.Ref,
            string.IsNullOrWhiteSpace(ev.Subject) ? "(no subject)" : ev.Subject,
            $"{ev.Start:ddd d MMM} {CalendarMath.TimeRange(ev.Start, ev.End, ev.IsAllDay)}",
            ev.Organizer,
            ev.Response,
            IsSeries: ev.IsRecurring));
    }

    /// <summary>s on the board: time on the calendar to get the task done.</summary>
    private async Task ScheduleFromBoardAsync()
    {
        if (Actions.Selected is not { } item) return;

        var mail = await Actions.ReplyTargetAsync().ConfigureAwait(true);
        Triage.OpenSchedulePalette(new ScheduleTarget(
            ConversationGrouper.StripPrefixes(item.Subject),
            mail,
            item.SenderAddress.Contains('@') ? new[] { item.SenderAddress } : Array.Empty<string>(),
            $"Action item from {item.SenderName}." + (mail is null ? "" : " The email is attached."),
            OfferUndo: false));
    }

    private async Task ReplyFromBoardAsync(ReplyScope scope)
    {
        if (!Actions.HasSelection) return;

        Actions.Status = "Opening the conversation...";
        var target = await Actions.ReplyTargetAsync().ConfigureAwait(true);
        if (target is null)
        {
            Actions.Status = "Could not find this task's email - it may have been deleted";
            return;
        }

        Actions.Status = await Triage.StartReplyToAsync(target.Value, scope).ConfigureAwait(true) ?? "";
    }

    private async Task<bool> HandleActionsKeyAsync(TriageAction action)
    {
        switch (action)
        {
            case TriageAction.NextMail: Actions.Move(1); return true;
            case TriageAction.PrevMail: Actions.Move(-1); return true;
            case TriageAction.PrevColumn: Actions.MoveColumn(-1); return true;
            case TriageAction.NextColumn: Actions.MoveColumn(1); return true;

            case TriageAction.StageBack: await Actions.StepStageAsync(-1).ConfigureAwait(true); return true;
            case TriageAction.StageForward: await Actions.StepStageAsync(1).ConfigureAwait(true); return true;
            case TriageAction.SetDue: Actions.RequestFocus(FormField.Due); return true;
            case TriageAction.ToggleBoardView: Actions.ToggleByPerson(); return true;
            case TriageAction.ClearWait: await Actions.ClearNextWaitAsync().ConfigureAwait(true); return true;
            case TriageAction.Chase: await Actions.ChaseAsync().ConfigureAwait(true); return true;
            case TriageAction.Cancel: await Actions.ClearFilterAsync().ConfigureAwait(true); return true;

            // Email the task's conversation without leaving the board.
            case TriageAction.Confirm:
            case TriageAction.ReplyAll:
                await ReplyFromBoardAsync(ReplyScope.All).ConfigureAwait(true);
                return true;
            case TriageAction.ReplySender:
                await ReplyFromBoardAsync(ReplyScope.SenderOnly).ConfigureAwait(true);
                return true;
            case TriageAction.Forward:
                await ReplyFromBoardAsync(ReplyScope.Forward).ConfigureAwait(true);
                return true;

            // The shortcuts put the cursor in the form beside the board, so the
            // keyboard and the mouse fill in the same fields.
            case TriageAction.ScheduleTime: await ScheduleFromBoardAsync().ConfigureAwait(true); return true;

            case TriageAction.AddNote: Actions.RequestFocus(FormField.Notes); return true;
            case TriageAction.AddBlocker: Actions.RequestFocus(FormField.Blocker); return true;
            case TriageAction.AddAssignment: Actions.RequestFocus(FormField.Assignment); return true;

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

    /// <summary>Reply all has no key of its own by default: Enter does it from the list.</summary>
    public string ReplyAllKey =>
        Keys.Describe(TriageAction.ReplyAll) is { Length: > 0 } own ? own : Keys.Describe(TriageAction.Confirm);

    /// <summary>The key for answering an invitation, for the card in the reading pane.</summary>
    public string RsvpKey => Keys.Describe(TriageAction.Rsvp);

    /// <summary>The key that joins the meeting on now, for the strip's tooltip.</summary>
    public string JoinKey => Keys.Describe(TriageAction.JoinMeeting);

    /// <summary>The new-message key, for the button's tooltip.</summary>
    public string ComposeKey => Keys.Describe(TriageAction.Compose);

    /// <summary>Rows for the help overlay, grouped for readability.</summary>
    public IReadOnlyList<(string Group, string Keys, string Description)> HelpRows => new[]
    {
        ("Move",    $"{Keys.Describe(TriageAction.NextMail)} / {Keys.Describe(TriageAction.PrevMail)}", "Next / previous message"),
        ("Move",    $"{Keys.Describe(TriageAction.SwitchSection)} / {Keys.Describe(TriageAction.PrevSection)}", "Next / previous tab: Triage, Action items, Calendar"),
        ("Move",    Keys.Describe(TriageAction.Search), "Filter the list"),

        ("Triage",  Keys.Describe(TriageAction.MarkActionRequired), "Needs action - send to the action list"),
        ("Triage",  Keys.Describe(TriageAction.MarkNoAction), "No action needed"),
        ("Triage",  Keys.Describe(TriageAction.MoveToFolder), "Move to folder (type to search, Ctrl+Enter creates)"),
        ("Triage",  Keys.Describe(TriageAction.Snooze), "Come back to this later"),
        ("Triage",  Keys.Describe(TriageAction.Archive), "Archive"),
        ("Triage",  Keys.Describe(TriageAction.ToggleRead), "Toggle read / unread"),
        ("Triage",  Keys.Describe(TriageAction.OpenAttachment), "Open an attachment (or click it in the header)"),
        ("Triage",  Keys.Describe(TriageAction.Undo), "Undo the last move or snooze"),

        ("Reply",   Keys.Describe(TriageAction.Compose), "New message, from any tab (Ctrl+Shift+S jumps to its subject)"),
        ("Reply",   ReplyAllKey, "Reply to everyone"),
        ("Reply",   Keys.Describe(TriageAction.ReplySender), "Reply to the sender only"),
        ("Reply",   Keys.Describe(TriageAction.Forward), "Forward (type the To line, Tab to the message)"),
        ("Reply",   "Ctrl+Enter", "Send"),
        ("Reply",   "Ctrl+Shift+Enter", "Send & mark done - archives the conversation"),
        ("Reply",   "Ctrl+Shift+L", "Send later - optionally held for review if they reply first"),
        ("Reply",   "Ctrl+Shift+O / C / B / M", "Jump to To / Cc / Bcc / the message"),
        ("Reply",   "Ctrl+Shift+,", "Discard the draft (Esc too)"),

        ("Board",   $"{Keys.Describe(TriageAction.PrevColumn)} {Keys.Describe(TriageAction.NextColumn)}  /  {Keys.Describe(TriageAction.NextMail)} {Keys.Describe(TriageAction.PrevMail)}", "Between columns  /  up and down a column"),
        ("Board",   $"{Keys.Describe(TriageAction.StageBack)} {Keys.Describe(TriageAction.StageForward)}", "Move the card back / forward a stage"),
        ("Board",   Keys.Describe(TriageAction.SetDue), "Set a due date"),
        ("Board",   $"{Keys.Describe(TriageAction.Confirm)} / {Keys.Describe(TriageAction.ReplySender)} / {Keys.Describe(TriageAction.Forward)}", "Reply all / reply / forward in the task's conversation"),
        ("Board",   "Mouse", "Drag a card to another column to move it"),
        ("Board",   Keys.Describe(TriageAction.ToggleBoardView), "Board / By person report (sort, export to Excel, email it)"),
        ("Board",   Keys.Describe(TriageAction.ClearWait), "Clear the next blocker or hand-off (back to Doing when none are left)"),
        ("Board",   Keys.Describe(TriageAction.Chase), "Draft a chase email to whoever has it"),
        ("Actions", Keys.Describe(TriageAction.AddNote), "Edit notes"),
        ("Actions", Keys.Describe(TriageAction.AddBlocker), "Blocked by - who or what it is waiting on"),
        ("Actions", Keys.Describe(TriageAction.AddAssignment), "Assign to someone else"),
        ("Actions", Keys.Describe(TriageAction.ToggleComplete), "Mark done"),
        ("Actions", Keys.Describe(TriageAction.CyclePriority), "Cycle priority"),
        ("Actions", Keys.Describe(TriageAction.OpenInOutlook), "Open the original in Outlook"),

        ("Calendar", Keys.Describe(TriageAction.Rsvp), "Answer an invitation - accept, maybe or decline, with a note if you type one"),
        ("Calendar", Keys.Describe(TriageAction.ScheduleTime), "Put the mail or task on your calendar (Ctrl+Enter invites its people instead)"),
        ("Calendar", Keys.Describe(TriageAction.JoinMeeting), "Join the meeting on now or about to start - from any tab"),
        ("Calendar", Keys.Describe(TriageAction.Confirm), "On the Calendar tab: join the meeting, or open it in Outlook"),
        ("Calendar", Keys.Describe(TriageAction.OpenInOutlook), "On the Calendar tab: open the meeting in Outlook"),

        ("General", Keys.Describe(TriageAction.Refresh), "Refresh"),
        ("General", Keys.Describe(TriageAction.ShowHelp), "This help"),
        ("General", Keys.Describe(TriageAction.Cancel), "Close / cancel"),
    };
}
