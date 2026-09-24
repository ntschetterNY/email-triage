using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum EditorMode { None, Note, Blocker, Assignment, Due }

/// <summary>One column of the board.</summary>
public sealed partial class BoardColumn : ObservableObject
{
    public BoardColumn(ActionStage stage, string title)
    {
        Stage = stage;
        Title = title;
    }

    public ActionStage Stage { get; }
    public string Title { get; }
    public ObservableCollection<ActionItem> Items { get; } = new();

    [ObservableProperty] private bool _isActive;

    /// <summary>A card is being dragged over this column.</summary>
    [ObservableProperty] private bool _isDropTarget;

    public int Count => Items.Count;

    public void Fill(IEnumerable<ActionItem> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>Which form field a shortcut key should put the cursor in.</summary>
public enum FormField { Blocker, Assignment, Notes, Due }

/// <summary>A sort choice for the By person report.</summary>
public sealed record SortChoice(WaitingSort Sort, string Label);

/// <summary>A "waiting on" chip in the board's summary strip.</summary>
public sealed record WaitingChip(string Person, int Count, bool AnyOverdue)
{
    public string Label => $"{Person}  {Count}";
}

/// <summary>
/// The action board: every mail that needs work, in To do, Doing, Waiting and
/// Done columns, with the blockers and hand-offs that hold it up and the
/// original email underneath. Built for moving fast from the keyboard;
/// assignments stay local until the user explicitly drafts a chase email.
/// </summary>
public sealed partial class ActionItemsViewModel : ObservableObject
{
    /// <summary>How far back the Done column reaches.</summary>
    private static readonly TimeSpan DoneWindow = TimeSpan.FromDays(14);

    private readonly IActionItemRepository _repo;
    private readonly IMailStore _store;
    private readonly IClock _clock;
    private readonly AppSettings _settings;

    private readonly LruCache<string, Task<string>> _bodies = new(40, StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _bodyLoad;

    /// <summary>
    /// The newest message from someone else in each task's conversation, by
    /// Message-ID: what a reply from the board answers, so it picks up the
    /// latest in the thread rather than the mail that was first flagged.
    /// </summary>
    private readonly Dictionary<string, MailRef> _replyTargets = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<BoardColumn> Columns { get; } = new[]
    {
        new BoardColumn(ActionStage.ToDo, "TO DO"),
        new BoardColumn(ActionStage.Doing, "DOING"),
        new BoardColumn(ActionStage.Waiting, "WAITING"),
        new BoardColumn(ActionStage.Done, "DONE"),
    };

    public ObservableCollection<WaitingChip> WaitingOnPeople { get; } = new();

    [ObservableProperty] private ActionItem? _selected;
    [ObservableProperty] private int _activeColumn;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _personFilter = "";
    [ObservableProperty] private string _bodyHtml = "";

    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _waitingCount;
    [ObservableProperty] private int _overdueCount;

    // Inline editor state. One editor at a time keeps the key handling simple.
    [ObservableProperty] private EditorMode _editor = EditorMode.None;
    [ObservableProperty] private string _editorTitle = "";
    [ObservableProperty] private string _fieldPrimary = "";
    [ObservableProperty] private string _fieldSecondary = "";
    [ObservableProperty] private string _fieldDue = "";
    [ObservableProperty] private string _editorHint = "";

    public ObservableCollection<string> KnownAssignees { get; } = new();

    // ---- the form beside the board: click-and-type alternatives to the keys ----

    [ObservableProperty] private string _newBlockerWhat = "";
    [ObservableProperty] private string _newBlockerWho = "";
    [ObservableProperty] private string _newBlockerDue = "";
    [ObservableProperty] private string _newAssignWho = "";
    [ObservableProperty] private string _newAssignWhat = "";
    [ObservableProperty] private string _newAssignDue = "";
    [ObservableProperty] private string _notesDraft = "";
    [ObservableProperty] private string _dueDraft = "";

    /// <summary>
    /// Everyone tagged before, most used first, so tagging someone again
    /// reuses the exact same name and the By person report groups them.
    /// </summary>
    public ObservableCollection<string> KnownPeople { get; } = new();

    /// <summary>Raised when a shortcut asks for the cursor in a form field.</summary>
    public event EventHandler<FormField>? FocusRequested;

    public void RequestFocus(FormField field)
    {
        if (Selected is null) { Status = "Pick a card first"; return; }
        FocusRequested?.Invoke(this, field);
    }

    // ---- By person report ------------------------------------------------------

    [ObservableProperty] private bool _isByPerson;
    [ObservableProperty] private SortChoice _reportSort;

    public IReadOnlyList<SortChoice> SortChoices { get; } = new[]
    {
        new SortChoice(WaitingSort.MostOverdue, "Most overdue"),
        new SortChoice(WaitingSort.MostItems, "Most items"),
        new SortChoice(WaitingSort.LongestWait, "Longest waiting"),
        new SortChoice(WaitingSort.Person, "Name A-Z"),
    };

    public ObservableCollection<PersonWaits> Report { get; } = new();

    private IReadOnlyList<ActionItem> _all = Array.Empty<ActionItem>();

    public ActionItemsViewModel(
        IActionItemRepository repo, IMailStore store, IClock clock, AppSettings settings)
    {
        _repo = repo;
        _store = store;
        _clock = clock;
        _settings = settings;
        _reportSort = SortChoices[0];
        Columns[0].IsActive = true;
    }

    public bool HasSelection => Selected is not null;
    public bool HasFilter => PersonFilter.Length > 0;

    // ---- loading ----------------------------------------------------------

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var previous = Selected?.Id;

            var open = await _repo.GetOpenAsync(ct).ConfigureAwait(true);
            var recentDone = (await _repo.GetCompletedAsync(60, ct).ConfigureAwait(true))
                .Where(i => i.CompletedUtc is { } d && _clock.UtcNow - d < DoneWindow);

            var all = open.Concat(recentDone).ToList();
            _all = all;

            WaitingOnPeople.Clear();
            foreach (var (person, count, overdue) in ActionWorkflow.WaitingOn(all))
                WaitingOnPeople.Add(new WaitingChip(person, count, overdue));

            var shown = HasFilter ? all.Where(i => ActionWorkflow.Involves(i, PersonFilter)).ToList() : all;

            foreach (var column in Columns)
            {
                var stage = column.Stage;
                column.Fill(shown
                    .Where(i => (i.IsComplete ? ActionStage.Done : i.Stage == ActionStage.Done ? ActionStage.Doing : i.Stage) == stage)
                    .OrderByDescending(i => stage == ActionStage.Done ? i.CompletedUtc : null)
                    .ThenByDescending(i => i.IsOverdue)
                    .ThenByDescending(i => i.Priority)
                    .ThenBy(i => i.NextDueUtc ?? DateTimeOffset.MaxValue)
                    .ThenByDescending(i => i.ReceivedUtc));
            }

            OpenCount = open.Count;
            WaitingCount = open.Count(i => i.IsWaiting);
            OverdueCount = open.Count(i => i.IsOverdue);

            var assignees = await _repo.GetKnownAssigneesAsync(ct).ConfigureAwait(true);
            KnownAssignees.Clear();
            foreach (var (name, email) in assignees)
                KnownAssignees.Add(string.IsNullOrWhiteSpace(email) ? name : $"{name} <{email}>");

            KnownPeople.Clear();
            foreach (var person in WaitingReport.KnownPeople(all)) KnownPeople.Add(person);

            RebuildReport();

            // Keep the same card selected across a reload, wherever it moved.
            var again = previous is null ? null : Columns.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == previous);
            if (again is not null) Select(again);
            else SelectInColumn(ActiveColumn, 0);

            Status = $"{OpenCount} open  ·  {WaitingCount} waiting"
                   + (OverdueCount > 0 ? $"  ·  {OverdueCount} overdue" : "")
                   + (HasFilter ? $"  ·  showing {PersonFilter}" : "");
        }
        catch (Exception ex)
        {
            Status = $"Could not load action items: {ex.Message}";
        }
    }

    partial void OnPersonFilterChanged(string value) => OnPropertyChanged(nameof(HasFilter));

    /// <summary>Shows only the items waiting on one person; the same person again clears it.</summary>
    public Task FilterToAsync(string person)
    {
        PersonFilter = string.Equals(PersonFilter, person, StringComparison.OrdinalIgnoreCase) ? "" : person;
        return LoadAsync();
    }

    public Task ClearFilterAsync()
    {
        if (!HasFilter) return Task.CompletedTask;
        PersonFilter = "";
        return LoadAsync();
    }

    // ---- navigation -------------------------------------------------------

    public void Select(ActionItem item)
    {
        var column = Array.FindIndex(Columns.ToArray(), c => c.Items.Contains(item));
        if (column >= 0) SetActiveColumn(column);
        Selected = item;
    }

    public void Move(int delta)
    {
        var items = Columns[ActiveColumn].Items;
        if (items.Count == 0) return;

        var index = Selected is null ? 0 : items.IndexOf(Selected) + delta;
        Selected = items[Math.Clamp(index, 0, items.Count - 1)];
    }

    /// <summary>Hops to the neighbouring column, landing on the card at the same height.</summary>
    public void MoveColumn(int delta)
    {
        var row = Selected is null ? 0 : Math.Max(0, Columns[ActiveColumn].Items.IndexOf(Selected));
        SelectInColumn(Math.Clamp(ActiveColumn + delta, 0, Columns.Count - 1), row);
    }

    private void SelectInColumn(int column, int row)
    {
        SetActiveColumn(column);
        var items = Columns[column].Items;
        Selected = items.Count == 0 ? null : items[Math.Clamp(row, 0, items.Count - 1)];
    }

    private void SetActiveColumn(int column)
    {
        ActiveColumn = column;
        for (var i = 0; i < Columns.Count; i++) Columns[i].IsActive = i == column;
    }

    partial void OnSelectedChanged(ActionItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        // The form always shows the card in hand; half-typed new entries are
        // for the card they were started on, so they go.
        NotesDraft = value?.Notes ?? "";
        DueDraft = value?.DueUtc?.ToLocalTime().ToString("ddd d MMM HH:mm") ?? "";
        NewBlockerWhat = NewBlockerWho = NewBlockerDue = "";
        NewAssignWho = NewAssignWhat = NewAssignDue = "";

        _ = LoadBodyAsync(value);
    }

    // ---- form submits: the same saves the shortcut editors make ----------------

    public async Task AddBlockerFromFormAsync()
    {
        if (!await SubmitAsync(EditorMode.Blocker, NewBlockerWhat, NewBlockerWho, NewBlockerDue).ConfigureAwait(true)) return;
        NewBlockerWhat = NewBlockerWho = NewBlockerDue = "";
    }

    public async Task AddAssignmentFromFormAsync()
    {
        if (!await SubmitAsync(EditorMode.Assignment, NewAssignWho, NewAssignWhat, NewAssignDue).ConfigureAwait(true)) return;
        NewAssignWho = NewAssignWhat = NewAssignDue = "";
    }

    public Task SaveNotesFromFormAsync() => SubmitAsync(EditorMode.Note, NotesDraft, "", "");

    public Task SaveDueFromFormAsync() => SubmitAsync(EditorMode.Due, DueDraft, "", "");

    /// <summary>Runs a form through the editor's save; true when it saved.</summary>
    private async Task<bool> SubmitAsync(EditorMode mode, string primary, string secondary, string due)
    {
        if (Selected is null) { Status = "Pick a card first"; return false; }

        Editor = mode;
        FieldPrimary = primary;
        FieldSecondary = secondary;
        FieldDue = due;

        await CommitEditorAsync().ConfigureAwait(true);

        // The save closes the editor; if it is still open, it said why not.
        var saved = Editor == EditorMode.None;
        if (!saved) CloseEditor();
        return saved;
    }

    // ---- By person report --------------------------------------------------------

    public void ToggleByPerson() => IsByPerson = !IsByPerson;

    partial void OnReportSortChanged(SortChoice value) => RebuildReport();

    private void RebuildReport()
    {
        var shown = HasFilter ? _all.Where(i => ActionWorkflow.Involves(i, PersonFilter)) : _all;
        var groups = WaitingReport.ByPerson(WaitingReport.Rows(shown), ReportSort.Sort);

        Report.Clear();
        foreach (var g in groups) Report.Add(g);
    }

    /// <summary>From a report row back to its card on the board.</summary>
    public void OpenFromReport(ActionItem item)
    {
        var onBoard = Columns.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == item.Id);
        IsByPerson = false;
        if (onBoard is not null) Select(onBoard);
    }

    /// <summary>Writes the report as a CSV and opens it (in Excel, usually).</summary>
    public void ExportReport()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Email Triage Reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"Waiting on - {_clock.Now:yyyy-MM-dd HHmm}.csv");

            // A byte-order mark so Excel reads names with accents correctly.
            File.WriteAllText(path, WaitingReport.ToCsv(Report, _clock.UtcNow), new System.Text.UTF8Encoding(true));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Report saved to {path}";
        }
        catch (Exception ex)
        {
            Status = $"Could not export the report: {ex.Message}";
        }
    }

    /// <summary>Opens the report as an unsent email in Outlook, to address and send there.</summary>
    public async Task EmailReportAsync()
    {
        try
        {
            await _store.CreateAndShowDraftAsync(
                Array.Empty<string>(),
                $"Waiting on - {_clock.Now:ddd d MMM}",
                WaitingReport.ToHtml(Report, _clock.UtcNow)).ConfigureAwait(true);
            Status = "Report opened as a draft in Outlook - add recipients and send it there";
        }
        catch (Exception ex)
        {
            Status = $"Could not create the report email: {ex.Message}";
        }
    }

    // ---- the email behind the card ------------------------------------------

    private async Task LoadBodyAsync(ActionItem? item)
    {
        _bodyLoad?.Cancel();

        if (item is null) { BodyHtml = ""; return; }

        var cts = new CancellationTokenSource();
        _bodyLoad = cts;

        var page = _bodies.GetOrAdd(item.InternetMessageId, _ => RenderBodyAsync(item));
        try
        {
            var html = await page.ConfigureAwait(true);
            if (!cts.IsCancellationRequested) BodyHtml = html;
        }
        catch (Exception ex)
        {
            _bodies.Remove(item.InternetMessageId);
            if (!cts.IsCancellationRequested)
                BodyHtml = HtmlPresenter.Render(Placeholder($"Could not open the email: {ex.Message}"), true);
        }
    }

    /// <summary>
    /// Renders the task's whole conversation, newest first, re-finding the
    /// flagged mail by Message-ID if it has been filed since.
    /// </summary>
    private async Task<string> RenderBodyAsync(ActionItem item)
    {
        var mail = await ResolveAsync(item).ConfigureAwait(true);
        if (mail is null)
            return HtmlPresenter.Render(Placeholder("The email could not be found - it may have been deleted."), true);

        IReadOnlyList<MailSummary> thread;
        try { thread = await _store.GetConversationAsync(mail.Value, _settings.ThreadMessageLimit).ConfigureAwait(true); }
        catch { thread = Array.Empty<MailSummary>(); }

        _replyTargets[item.InternetMessageId] = thread.FirstOrDefault(m => !m.IsSent)?.Ref ?? mail.Value;

        var bodies = new List<MailBody>();
        foreach (var m in thread)
        {
            try { bodies.Add(await _store.GetBodyAsync(m.Ref).ConfigureAwait(true)); }
            catch { /* one unreadable message should not hide the rest */ }
        }
        if (bodies.Count == 0) bodies.Add(await _store.GetBodyAsync(mail.Value).ConfigureAwait(true));

        var blockRemote = _settings.BlockRemoteImages;
        return await Task.Run(() => HtmlPresenter.RenderThread(bodies, blockRemote)).ConfigureAwait(true);
    }

    /// <summary>Where the flagged mail is now, updating the stored location if it moved.</summary>
    private async Task<MailRef?> ResolveAsync(ActionItem item)
    {
        var known = new MailRef(item.EntryId, item.StoreId);
        if (!known.IsEmpty && await _store.GetSavedDraftStateAsync(new DraftRef(item.EntryId, item.StoreId)).ConfigureAwait(true)
                is not SavedDraftState.Missing)
            return known;

        var found = await _store.FindByMessageIdAsync(item.InternetMessageId, null).ConfigureAwait(true);
        if (found is null) return null;

        await _repo.UpdateLocationAsync(item.InternetMessageId, found.Value.EntryId, found.Value.StoreId).ConfigureAwait(true);
        item.EntryId = found.Value.EntryId;
        item.StoreId = found.Value.StoreId;
        return found;
    }

    /// <summary>
    /// The mail a reply from the board should answer: the newest message from
    /// someone else in the task's conversation.
    /// </summary>
    public async Task<MailRef?> ReplyTargetAsync()
    {
        if (Selected is not { } item) return null;

        // Opening the card loads the thread; wait for it if it is still coming.
        if (_bodies.TryGet(item.InternetMessageId, out var page))
        {
            try { await page.ConfigureAwait(true); } catch { }
        }

        return _replyTargets.TryGetValue(item.InternetMessageId, out var target)
            ? target
            : await ResolveAsync(item).ConfigureAwait(true);
    }

    /// <summary>After sending in a task's conversation, show the thread with the new message.</summary>
    public void RefreshSelectedThread()
    {
        if (Selected is not { } item) return;
        _bodies.Remove(item.InternetMessageId);
        _replyTargets.Remove(item.InternetMessageId);
        _ = LoadBodyAsync(item);
    }

    /// <summary>Moves a card straight to a stage - where a drag-and-drop lands it.</summary>
    public async Task MoveToStageAsync(ActionItem item, ActionStage to)
    {
        Select(item);

        var from = item.IsComplete ? ActionStage.Done : item.Stage;
        if (to == from) return;

        if (to == ActionStage.Done || from == ActionStage.Done)
        {
            if (to == ActionStage.Done || to == ActionStage.Doing)
            {
                await ToggleCompleteAsync().ConfigureAwait(true);
                return;
            }

            // Reopening straight into To do or Waiting.
            await _repo.UpdateStageAsync(item.Id, to).ConfigureAwait(true);
            Status = $"Reopened in {Title(to)}";
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            await _repo.UpdateStageAsync(item.Id, to).ConfigureAwait(true);
            Status = to == ActionStage.Waiting && !item.IsWaiting
                ? "Moved to Waiting · add who it is on with b (blocked by) or Shift+A (assign)"
                : $"Moved to {Title(to)}";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not move it: {ex.Message}";
        }
    }

    private static MailBody Placeholder(string text) => new()
    {
        Ref = default,
        Subject = "",
        SenderName = "",
        SenderAddress = "",
        ReceivedUtc = default,
        PlainText = text,
    };

    // ---- editors ----------------------------------------------------------

    public void OpenEditor(EditorMode mode)
    {
        if (Selected is null) return;

        Editor = mode;
        FieldPrimary = mode switch
        {
            EditorMode.Note => Selected.Notes,
            EditorMode.Due => Selected.DueUtc?.ToLocalTime().ToString("ddd d MMM HH:mm") ?? "",
            _ => "",
        };
        FieldSecondary = "";
        FieldDue = "";

        (EditorTitle, EditorHint) = mode switch
        {
            EditorMode.Note => ("Notes", "Ctrl+Enter save · Esc cancel"),
            EditorMode.Blocker => ("What is blocking this?", "Tab between fields · Ctrl+Enter save · Esc cancel · moves the card to Waiting"),
            EditorMode.Assignment => ("Assign to someone", "Tab between fields · Ctrl+Enter save · Esc cancel · moves the card to Waiting"),
            EditorMode.Due => ("When is this due?", "e.g. fri, 14 oct, 3d, tomorrow 5pm · empty clears · Ctrl+Enter save"),
            _ => ("", ""),
        };
    }

    public void CloseEditor()
    {
        Editor = EditorMode.None;
        FieldPrimary = FieldSecondary = FieldDue = "";
    }

    public async Task CommitEditorAsync()
    {
        if (Selected is not { } item || Editor == EditorMode.None) return;

        try
        {
            switch (Editor)
            {
                case EditorMode.Note:
                    await _repo.UpdateNotesAsync(item.Id, FieldPrimary).ConfigureAwait(true);
                    item.Notes = FieldPrimary;
                    Status = "Notes saved";
                    break;

                case EditorMode.Due:
                    DateTimeOffset? due = null;
                    if (FieldPrimary.Trim().Length > 0)
                    {
                        due = ParseDue(FieldPrimary);
                        if (due is null) { Status = "Not a date I understand - try \"fri\", \"14 oct\" or \"3d\""; return; }
                    }
                    await _repo.UpdateDueAsync(item.Id, due).ConfigureAwait(true);
                    Status = due is { } d ? $"Due {d.ToLocalTime():ddd d MMM}" : "Due date cleared";
                    break;

                case EditorMode.Blocker:
                    if (string.IsNullOrWhiteSpace(FieldPrimary)) { Status = "Describe the blocker first"; return; }

                    var blocker = await _repo.AddBlockerAsync(new BlockingTask
                    {
                        ActionItemId = item.Id,
                        Description = FieldPrimary.Trim(),
                        WaitingOn = FieldSecondary.Trim(),
                        DueUtc = ParseDue(FieldDue),
                        CreatedUtc = _clock.UtcNow,
                    }).ConfigureAwait(true);

                    item.Blockers.Add(blocker);
                    await ApplyAsync(item, ActionWorkflow.AfterWaitAdded(item)).ConfigureAwait(true);
                    Status = "Blocker added · moved to Waiting";
                    break;

                case EditorMode.Assignment:
                    if (string.IsNullOrWhiteSpace(FieldPrimary)) { Status = "Who is this for?"; return; }
                    if (string.IsNullOrWhiteSpace(FieldSecondary)) { Status = "Describe what they need to do"; return; }

                    var (name, email) = ParsePerson(FieldPrimary);

                    var assignment = await _repo.AddAssignmentAsync(new Assignment
                    {
                        ActionItemId = item.Id,
                        PersonName = name,
                        PersonEmail = email,
                        Task = FieldSecondary.Trim(),
                        DueUtc = ParseDue(FieldDue),
                        CreatedUtc = _clock.UtcNow,
                    }).ConfigureAwait(true);

                    item.Assignments.Add(assignment);
                    await ApplyAsync(item, ActionWorkflow.AfterWaitAdded(item)).ConfigureAwait(true);
                    Status = $"Assigned to {name} · moved to Waiting · nothing sent yet (c drafts a chase email)";
                    break;
            }

            CloseEditor();
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    /// <summary>Accepts "Alice Smith &lt;alice@corp.com&gt;" or a bare name.</summary>
    private static (string Name, string Email) ParsePerson(string text)
    {
        text = text.Trim();

        var open = text.IndexOf('<');
        var close = text.IndexOf('>');

        if (open > 0 && close > open)
            return (text[..open].Trim(), text[(open + 1)..close].Trim());

        return text.Contains('@') ? (text, text) : (text, "");
    }

    private DateTimeOffset? ParseDue(string text) =>
        NaturalDateParser.TryParse(text, _clock.Now, out var when, _settings.DayShape)
            ? when.ToUniversalTime()
            : null;

    // ---- workflow ---------------------------------------------------------

    /// <summary>Moves the selected card one column along (or back).</summary>
    public async Task StepStageAsync(int delta)
    {
        if (Selected is not { } item) return;

        var from = item.IsComplete ? ActionStage.Done : item.Stage;
        var to = ActionWorkflow.Step(from, delta);
        if (to == from) return;

        if (to == ActionStage.Done || from == ActionStage.Done)
        {
            await ToggleCompleteAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            await _repo.UpdateStageAsync(item.Id, to).ConfigureAwait(true);
            Status = to == ActionStage.Waiting && !item.IsWaiting
                ? "Moved to Waiting · add who it is on with b (blocked by) or Shift+A (assign)"
                : $"Moved to {Title(to)}";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not move it: {ex.Message}";
        }
    }

    private async Task ApplyAsync(ActionItem item, ActionStage? stage)
    {
        if (stage is not { } s) return;
        await _repo.UpdateStageAsync(item.Id, s).ConfigureAwait(true);
        item.Stage = s;
    }

    /// <summary>Clears the oldest open blocker, or failing that the oldest hand-off.</summary>
    public async Task ClearNextWaitAsync()
    {
        if (Selected is not { } item) return;

        var blocker = item.Blockers.FirstOrDefault(b => !b.IsResolved);
        if (blocker is not null) { await ToggleBlockerAsync(blocker).ConfigureAwait(true); return; }

        var assignment = item.Assignments.FirstOrDefault(a => !a.IsDone);
        if (assignment is not null) { await ToggleAssignmentAsync(assignment).ConfigureAwait(true); return; }

        Status = "Nothing is holding this up";
    }

    public async Task ToggleCompleteAsync()
    {
        if (Selected is not { } item) return;

        try
        {
            var target = !item.IsComplete;
            await _repo.SetCompletedAsync(item.Id, target).ConfigureAwait(true);

            if (target && !string.IsNullOrEmpty(item.EntryId))
            {
                // Clear the Outlook category too, so the two views agree.
                try
                {
                    await _store.SetCategoryAsync(
                        new MailRef(item.EntryId, item.StoreId),
                        _settings.ActionCategory, false).ConfigureAwait(true);
                }
                catch { /* the mail may have been filed or deleted since */ }
            }

            Status = target ? "Done" : "Reopened · back in Doing";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not update: {ex.Message}";
        }
    }

    public async Task CyclePriorityAsync()
    {
        if (Selected is not { } item) return;

        var next = item.Priority switch
        {
            ActionPriority.Low => ActionPriority.Normal,
            ActionPriority.Normal => ActionPriority.High,
            _ => ActionPriority.Low,
        };

        await _repo.UpdatePriorityAsync(item.Id, next).ConfigureAwait(true);
        item.Priority = next;
        Status = $"Priority: {next}";
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task ToggleBlockerAsync(BlockingTask blocker)
    {
        if (Selected is not { } item) return;

        await _repo.SetBlockerResolvedAsync(blocker.Id, !blocker.IsResolved).ConfigureAwait(true);
        blocker.ResolvedUtc = blocker.IsResolved ? null : _clock.UtcNow;

        await ApplyAsync(item, blocker.IsResolved
            ? ActionWorkflow.AfterWaitCleared(item)
            : ActionWorkflow.AfterWaitAdded(item)).ConfigureAwait(true);

        Status = blocker.IsResolved
            ? $"Cleared: {blocker.Description}" + (item.Stage == ActionStage.Doing && !item.IsWaiting ? " · back in Doing" : "")
            : $"Blocked again: {blocker.Description}";
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task ToggleAssignmentAsync(Assignment assignment)
    {
        if (Selected is not { } item) return;

        await _repo.SetAssignmentDoneAsync(assignment.Id, !assignment.IsDone).ConfigureAwait(true);
        assignment.DoneUtc = assignment.IsDone ? null : _clock.UtcNow;

        await ApplyAsync(item, assignment.IsDone
            ? ActionWorkflow.AfterWaitCleared(item)
            : ActionWorkflow.AfterWaitAdded(item)).ConfigureAwait(true);

        Status = assignment.IsDone
            ? $"{assignment.PersonName} delivered" + (item.Stage == ActionStage.Doing && !item.IsWaiting ? " · back in Doing" : "")
            : $"Back with {assignment.PersonName}";
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Drafts a chase email to the first person with an open hand-off.</summary>
    public async Task ChaseAsync()
    {
        if (Selected is not { } item) return;

        var assignment = item.Assignments.FirstOrDefault(a => !a.IsDone && a.PersonEmail.Length > 0)
                         ?? item.Assignments.FirstOrDefault(a => !a.IsDone);

        if (assignment is null)
        {
            Status = "No open hand-off to chase - assign it with Shift+A first";
            return;
        }

        await DraftAssignmentMailAsync(assignment).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds an unsent mail asking someone for what they owe, and shows it in
    /// Outlook. Explicitly does not send: the user reviews and presses send.
    /// </summary>
    public async Task DraftAssignmentMailAsync(Assignment assignment)
    {
        if (Selected is not { } item) return;

        if (string.IsNullOrWhiteSpace(assignment.PersonEmail))
        {
            Status = $"No email address on file for {assignment.PersonName}";
            return;
        }

        try
        {
            var due = assignment.DueUtc is { } d
                ? $"<p>Ideally by <strong>{d.ToLocalTime():dddd d MMM}</strong>.</p>"
                : "";

            var body = $"""
                <div style="font-family:Calibri,sans-serif;font-size:11pt">
                  <p>Hi {System.Net.WebUtility.HtmlEncode(assignment.PersonName.Split(' ')[0])},</p>
                  <p>{System.Net.WebUtility.HtmlEncode(assignment.Task)}</p>
                  {due}
                  <p>This came out of: <em>{System.Net.WebUtility.HtmlEncode(item.Subject)}</em></p>
                  <p>Thanks</p>
                </div>
                """;

            await _store.CreateAndShowDraftAsync(
                new[] { assignment.PersonEmail },
                $"Action needed: {item.Subject}",
                body).ConfigureAwait(true);

            await _repo.MarkAssignmentDraftedAsync(assignment.Id).ConfigureAwait(true);
            assignment.NotifiedUtc = _clock.UtcNow;

            Status = $"Draft opened in Outlook for {assignment.PersonName} - review and send it there";
            OnPropertyChanged(nameof(Selected));
        }
        catch (Exception ex)
        {
            Status = $"Could not create the draft: {ex.Message}";
        }
    }

    /// <summary>Brings the originating mail up in Outlook for the full context.</summary>
    public async Task OpenInOutlookAsync()
    {
        if (Selected is not { } item) return;

        try
        {
            var found = await _store
                .FindByMessageIdAsync(item.InternetMessageId, null).ConfigureAwait(true);

            if (found is null)
            {
                Status = "Could not find that message - it may have been filed elsewhere";
                return;
            }

            await _repo.UpdateLocationAsync(
                item.InternetMessageId, found.Value.EntryId, found.Value.StoreId).ConfigureAwait(true);

            await _store.ShowItemAsync(found.Value).ConfigureAwait(true);
            Status = "Opened in Outlook";
        }
        catch (Exception ex)
        {
            Status = $"Could not open it: {ex.Message}";
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } item) return;

        await _repo.DeleteAsync(item.Id).ConfigureAwait(true);
        Status = "Removed from the action list";
        await LoadAsync().ConfigureAwait(true);
    }

    private static string Title(ActionStage stage) => stage switch
    {
        ActionStage.ToDo => "To do",
        ActionStage.Doing => "Doing",
        ActionStage.Waiting => "Waiting",
        _ => "Done",
    };
}
