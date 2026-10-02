using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum EditorMode { None, Note, Blocker, Assignment, Due }

/// <summary>One column of the board.</summary>
public sealed partial class BoardColumn : ObservableObject
{
    public BoardColumn(ActionStage stage, string title, bool isFollowUps = false)
    {
        Stage = stage;
        Title = title;
        IsFollowUps = isFollowUps;
    }

    public ActionStage Stage { get; }
    public string Title { get; }

    /// <summary>
    /// The Follow up column: not a stage of its own but waiting cards whose
    /// follow-up day has come, so nothing can be dropped into it.
    /// </summary>
    public bool IsFollowUps { get; }
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

    /// <summary>Takes one card off the column; true if it was there.</summary>
    public bool Remove(ActionItem item)
    {
        if (!Items.Remove(item)) return false;
        OnPropertyChanged(nameof(Count));
        return true;
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
/// Follow up columns, with the blockers and hand-offs that hold it up and the
/// original email underneath. A finished card leaves the board at once; a
/// strip counts today's and a log (Shift+D) keeps the rest. Built for moving
/// fast from the keyboard; assignments stay local until the user explicitly
/// drafts a chase email.
/// </summary>
public sealed partial class ActionItemsViewModel : ObservableObject
{
    /// <summary>How many finished cards the done log shows.</summary>
    private const int DoneLogLimit = 200;

    private readonly IActionItemRepository _repo;
    private readonly IMailStore _store;
    private readonly IClock _clock;
    private readonly AppSettings _settings;

    // Conversation pages by Message-ID, and the message bodies they are built
    // from by EntryId. Tasks rather than values, so the pane and the
    // prefetcher share one fetch when both want the same mail. UI thread only.
    private readonly LruCache<string, Task<string>> _bodies = new(40, StringComparer.OrdinalIgnoreCase);
    private readonly LruCache<string, Task<MailBody>> _mail = new(120, StringComparer.Ordinal);
    private CancellationTokenSource? _bodyLoad;
    private CancellationTokenSource? _prefetch;

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
        new BoardColumn(ActionStage.Waiting, "FOLLOW UP", isFollowUps: true),
    };

    /// <summary>Cards finished today, for the strip under the board.</summary>
    public ObservableCollection<ActionItem> DoneToday { get; } = new();

    /// <summary>Everything finished, newest first, for the done log (Shift+D).</summary>
    public ObservableCollection<ActionItem> DoneLog { get; } = new();

    [ObservableProperty] private int _doneTodayCount;
    [ObservableProperty] private string _doneTodayLine = "";

    /// <summary>The done log is showing instead of the board.</summary>
    [ObservableProperty] private bool _isDoneLog;

    public bool IsBoardShown => !IsByPerson && !IsDoneLog;

    /// <summary>The card most recently marked done, as it stood, so `z` can put it back.</summary>
    private ActionItem? _lastDone;

    private BoardColumn FollowUps => Columns.First(c => c.IsFollowUps);

    /// <summary>Checks now and then whether the day has turned, so that day's follow-ups appear.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _dayWatch = new() { Interval = TimeSpan.FromMinutes(10) };
    private DateTime _loadedDay;

    // One pass at a time looking in Sent Items for new messages' copies.
    private bool _resolvingSent;

    public ObservableCollection<WaitingChip> WaitingOnPeople { get; } = new();

    [ObservableProperty] private ActionItem? _selected;
    [ObservableProperty] private int _activeColumn;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _personFilter = "";
    [ObservableProperty] private string _bodyHtml = "";

    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _waitingCount;
    [ObservableProperty] private int _overdueCount;

    /// <summary>Waits that have sat past the follow-up threshold, for the status line.</summary>
    [ObservableProperty] private int _followUpDueCount;

    /// <summary>Cards in the Follow up column: waits whose follow-up day has come.</summary>
    [ObservableProperty] private int _scheduledFollowUpCount;

    /// <summary>Open cards nobody has touched past the stale threshold.</summary>
    [ObservableProperty] private int _staleCount;

    // ---- the stale review (Shift+R): one neglected card at a time ----

    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private ActionItem? _reviewItem;
    [ObservableProperty] private string _reviewPosition = "";
    private List<ActionItem> _reviewQueue = new();
    private int _reviewIndex;
    private int _reviewed;

    public string ReviewHint =>
        $"{CompleteKey} done · {DeleteKey} drop · {ConfirmKey} keep (resets its clock) · {DueKey} give it a due date · Esc stop";

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
        IActionItemRepository repo, IMailStore store, IClock clock, AppSettings settings,
        AiDraftService aiDraft, WritingStyleService style, KeyMap keys)
    {
        _keys = keys;
        _repo = repo;
        _store = store;
        _clock = clock;
        _settings = settings;
        _aiDraft = aiDraft;
        _style = style;
        _reportSort = SortChoices[0];
        Columns[0].IsActive = true;
    }

    private readonly AiDraftService _aiDraft;
    private readonly WritingStyleService _style;
    private readonly KeyMap _keys;

    /// <summary>The chase key, for status lines that point to it.</summary>
    private string ChaseKey => _keys.Describe(TriageAction.Chase) is { Length: > 0 } key ? key : "Shift+C";

    public bool HasSelection => Selected is not null;
    public bool HasFilter => PersonFilter.Length > 0;

    // ---- loading ----------------------------------------------------------

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var previous = Selected?.Id;

            var open = await _repo.GetOpenAsync(ct).ConfigureAwait(true);
            var all = open.ToList();
            _all = all;
            _loadedDay = _clock.Now.Date;

            await RefreshDoneAsync(ct).ConfigureAwait(true);

            // Waits whose follow-up day has come go to the Follow up column.
            var now = _clock.Now;
            foreach (var item in all) item.ScheduledFollowUp = "";
            var scheduled = FollowUpPlanner.FindScheduled(open, now);
            foreach (var due in scheduled) due.Item.ScheduledFollowUp = FollowUpPlanner.Label(due, now);
            ScheduledFollowUpCount = scheduled.Count;

            // Flag the other waits that have gone stale, so the cards say so
            // and `c` has a queue to work through. Timestamp arithmetic only -
            // no AI runs until the user asks for the chase draft itself.
            foreach (var item in all) item.FollowUpDays = 0;
            var followUps = FollowUpPlanner.FindDue(open.Where(i => !i.IsInFollowUp), _settings.FollowUpAfterDays, _clock.UtcNow);
            foreach (var due in followUps) due.Item.FollowUpDays = due.DaysWaiting;
            FollowUpDueCount = followUps.Count;

            // Cards nobody has touched for weeks: a chip, the bottom of their
            // column, and a queue for the review walk.
            foreach (var item in all) item.StaleDays = 0;
            var stale = StaleItems.Find(open, _settings.StaleAfterDays, _clock.UtcNow);
            foreach (var item in stale) item.StaleDays = StaleItems.DaysIdle(item, _clock.UtcNow);
            StaleCount = stale.Count;

            WaitingOnPeople.Clear();
            foreach (var (person, count, overdue) in ActionWorkflow.WaitingOn(all))
                WaitingOnPeople.Add(new WaitingChip(person, count, overdue));

            var shown = HasFilter ? all.Where(i => ActionWorkflow.Involves(i, PersonFilter)).ToList() : all;

            FollowUps.Fill(scheduled.Select(d => d.Item).Where(shown.Contains));

            foreach (var column in Columns.Where(c => !c.IsFollowUps))
            {
                var stage = column.Stage;
                column.Fill(shown
                    .Where(i => !i.IsInFollowUp)
                    .Where(i => (i.Stage == ActionStage.Done ? ActionStage.Doing : i.Stage) == stage)
                    .OrderBy(i => i.IsStale)
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
            var again = previous is null ? null
                : (IsDoneLog ? DoneLog : Columns.SelectMany(c => c.Items)).FirstOrDefault(i => i.Id == previous);
            if (again is not null) Select(again);
            else if (IsDoneLog) Selected = DoneLog.FirstOrDefault();
            else SelectInColumn(ActiveColumn, 0);

            Status = $"{OpenCount} open  ·  {WaitingCount} waiting"
                   + (OverdueCount > 0 ? $"  ·  {OverdueCount} overdue" : "")
                   + (ScheduledFollowUpCount > 0
                       ? $"  ·  {ScheduledFollowUpCount} to follow up ({ChaseKey} drafts each one)"
                       : "")
                   + (FollowUpDueCount > 0
                       ? $"  ·  {FollowUpDueCount} follow-up{(FollowUpDueCount == 1 ? "" : "s")} due ({ChaseKey} drafts a chase)"
                       : "")
                   + (StaleCount > 0 ? $"  ·  {StaleCount} stale ({ReviewKey} reviews them)" : "")
                   + (HasFilter ? $"  ·  showing {PersonFilter}" : "");

            if (open.Any(i => i.IsAwaitingSentCopy)) _ = ResolveSentCopiesAsync();
        }
        catch (Exception ex)
        {
            Status = $"Could not load action items: {ex.Message}";
        }
    }

    /// <summary>Fills the done strip, and the log while it is showing.</summary>
    private async Task RefreshDoneAsync(CancellationToken ct = default)
    {
        var done = await _repo.GetCompletedAsync(IsDoneLog ? DoneLogLimit : 60, ct).ConfigureAwait(true);

        var today = _clock.Now.Date;
        var todays = done
            .Where(i => i.CompletedUtc is { } d && d.ToOffset(_clock.Now.Offset).Date == today)
            .ToList();

        DoneToday.Clear();
        foreach (var i in todays) DoneToday.Add(i);
        DoneTodayCount = todays.Count;
        DoneTodayLine = todays.Count == 0
            ? "Nothing marked done yet today"
            : string.Join("  ·  ", todays.Take(3).Select(i => i.DisplayTitle))
              + (todays.Count > 3 ? $"  ·  +{todays.Count - 3} more" : "");

        DoneLog.Clear();
        if (IsDoneLog) foreach (var i in done) DoneLog.Add(i);
    }

    /// <summary>Shift+D: the log of what is finished, newest first, or back to the board.</summary>
    public async Task ToggleDoneLogAsync() => await ShowDoneLogAsync(!IsDoneLog).ConfigureAwait(true);

    public async Task ShowDoneLogAsync(bool show)
    {
        if (IsDoneLog == show) return;

        IsDoneLog = show;
        if (show) IsByPerson = false;

        await LoadAsync().ConfigureAwait(true);

        if (show)
        {
            Selected = DoneLog.FirstOrDefault();
            Status = DoneLog.Count == 0
                ? "Nothing finished yet"
                : $"{DoneLog.Count} done · {CompleteKey} reopens one · {DoneLogKey} back to the board";
        }
    }

    private string CompleteKey => _keys.Describe(TriageAction.ToggleComplete) is { Length: > 0 } key ? key : "x";
    private string DeleteKey => _keys.Describe(TriageAction.Delete) is { Length: > 0 } key ? key : "#";
    private string ConfirmKey => _keys.Describe(TriageAction.Confirm) is { Length: > 0 } key ? key : "Enter";
    private string DueKey => _keys.Describe(TriageAction.SetDue) is { Length: > 0 } key ? key : "d";
    private string ReviewKey => _keys.Describe(TriageAction.ReviewStale) is { Length: > 0 } key ? key : "Shift+R";

    // ---- the stale review ------------------------------------------------------

    /// <summary>Shift+R: the stale cards, longest idle first, one at a time.</summary>
    public void StartReview()
    {
        _reviewQueue = _all.Where(i => i.IsStale).OrderByDescending(i => i.StaleDays).ToList();
        if (_reviewQueue.Count == 0)
        {
            Status = _settings.StaleAfterDays > 0
                ? $"Nothing is stale - no open card has sat untouched for {_settings.StaleAfterDays} days"
                : "Stale marking is off (StaleAfterDays in settings)";
            return;
        }

        _reviewIndex = 0;
        _reviewed = 0;
        IsReviewing = true;
        IsDoneLog = false;
        IsByPerson = false;
        ShowReviewItem();
    }

    private void ShowReviewItem()
    {
        var item = _reviewQueue[_reviewIndex];
        ReviewItem = item;
        ReviewPosition = $"{_reviewIndex + 1} of {_reviewQueue.Count}";

        // The card behind the panel, so its email shows beneath.
        var onBoard = Columns.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == item.Id);
        if (onBoard is not null) Select(onBoard); else Selected = item;

        Status = $"Reviewing stale cards · {ReviewPosition} · idle {item.StaleDays} days";
    }

    public void EndReview()
    {
        if (!IsReviewing) return;
        IsReviewing = false;
        ReviewItem = null;
        Status = _reviewed == 0 ? "Review stopped" : $"Reviewed {_reviewed} stale card{(_reviewed == 1 ? "" : "s")}";
    }

    /// <summary>Enter: it is still live - restart its clock and move on.</summary>
    public async Task ReviewKeepAsync()
    {
        if (ReviewItem is not { } item) return;
        await _repo.TouchAsync(item.Id).ConfigureAwait(true);
        await AdvanceReviewAsync().ConfigureAwait(true);
    }

    /// <summary>x: it happened somewhere else - done, with undo as usual.</summary>
    public async Task ReviewDoneAsync()
    {
        if (ReviewItem is not { } item) return;
        await _repo.SetCompletedAsync(item.Id, true).ConfigureAwait(true);
        _lastDone = item;
        // Outlook's category is cleared behind the walk, not ahead of the next card.
        _ = ClearCategoryAsync(item);
        await AdvanceReviewAsync().ConfigureAwait(true);
    }

    /// <summary>#: it never mattered - off the list for good.</summary>
    public async Task ReviewDropAsync()
    {
        if (ReviewItem is not { } item) return;
        await _repo.DeleteAsync(item.Id).ConfigureAwait(true);
        _ = ClearCategoryAsync(item);
        await AdvanceReviewAsync().ConfigureAwait(true);
    }

    /// <summary>d: give it a real date; saving the date moves the review on.</summary>
    public void ReviewDue()
    {
        if (ReviewItem is not { } item) return;
        Selected = item;
        OpenEditor(EditorMode.Due);
    }

    private async Task AdvanceReviewAsync()
    {
        _reviewed++;
        _reviewIndex++;
        await LoadAsync().ConfigureAwait(true);

        if (_reviewIndex >= _reviewQueue.Count) { EndReview(); return; }
        ShowReviewItem();
    }

    private string DoneLogKey => _keys.Describe(TriageAction.ToggleDoneLog) is { Length: > 0 } key ? key : "Shift+D";
    private string UndoKey => _keys.Describe(TriageAction.Undo) is { Length: > 0 } key ? key : "z";

    /// <summary>
    /// Marks every open card for one of these mails done - Send &amp; mark done
    /// finishing the card along with the conversation. Returns how many.
    /// </summary>
    public async Task<int> CompleteAnyAsync(IEnumerable<string> messageIds)
    {
        var ids = messageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = _all.Where(i => !i.IsComplete && ids.Contains(i.InternetMessageId)).ToList();

        foreach (var item in items)
        {
            await _repo.SetCompletedAsync(item.Id, true).ConfigureAwait(true);
            await ClearCategoryAsync(item).ConfigureAwait(true);
        }

        if (items.Count > 0) _lastDone = items[^1];
        return items.Count;
    }

    /// <summary>`z` on the board: the last card marked done comes back as it was.</summary>
    public async Task UndoAsync()
    {
        if (_lastDone is not { } item)
        {
            Status = "Nothing to undo on the board";
            return;
        }

        try
        {
            await _repo.RestoreAsync(item).ConfigureAwait(true);
            _lastDone = null;

            if (!string.IsNullOrEmpty(item.EntryId))
            {
                try
                {
                    await _store.SetCategoryAsync(
                        new MailRef(item.EntryId, item.StoreId), _settings.ActionCategory, true).ConfigureAwait(true);
                }
                catch { /* the mail may have been filed or deleted since */ }
            }

            if (IsDoneLog) await ShowDoneLogAsync(false).ConfigureAwait(true);
            else await LoadAsync().ConfigureAwait(true);

            var back = Columns.SelectMany(c => c.Items).FirstOrDefault(i => i.Id == item.Id);
            if (back is not null) Select(back);
            Status = $"Put back: {item.DisplayTitle}";
        }
        catch (Exception ex)
        {
            Status = $"Could not undo: {ex.Message}";
        }
    }

    /// <summary>Starts reloading the board when the day turns, so that day's follow-ups appear unasked.</summary>
    public void StartDayWatch()
    {
        _dayWatch.Tick += async (_, _) =>
        {
            if (_clock.Now.Date != _loadedDay) await LoadAsync().ConfigureAwait(true);
        };
        _dayWatch.Start();
    }

    // ---- new messages: find the sent copy a follow-up was filed against ----

    /// <summary>
    /// Repoints cards filed for a new message at its copy in Sent Items, once
    /// Outlook has put it there, so the card shows the email and a chase can
    /// reply in its thread. Returns true when any card was repointed.
    /// </summary>
    private async Task<bool> ResolveSentCopiesAsync()
    {
        if (_resolvingSent) return false;
        _resolvingSent = true;

        try
        {
            var pending = _all.Where(i => i.IsAwaitingSentCopy && !i.IsComplete).ToList();
            if (pending.Count == 0) return false;

            var sentFolder = await _store.GetSentItemsAsync().ConfigureAwait(true);
            var sent = await _store.GetMailAsync(sentFolder, 100).ConfigureAwait(true);

            var repointed = false;
            foreach (var item in pending)
            {
                var person = item.Assignments.FirstOrDefault();
                var copy = SentMailMatcher.Find(
                    sent, item.Subject, person?.PersonName ?? "", person?.PersonEmail ?? "", item.ReceivedUtc);
                if (copy is null) continue;

                try
                {
                    await _repo.ReplaceMessageIdAsync(
                        item.Id, copy.InternetMessageId, copy.Ref.EntryId, copy.Ref.StoreId).ConfigureAwait(true);
                    repointed = true;
                }
                catch { continue; /* that mail is already on the board under its own card */ }

                // Visible in Outlook too, as a flagged mail would be.
                try { await _store.SetCategoryAsync(copy.Ref, _settings.ActionCategory, true).ConfigureAwait(true); }
                catch { }
            }

            // Still guarded, so this reload does not set off another pass for
            // the ones not sent yet; the next load looks for those.
            if (repointed) await LoadAsync().ConfigureAwait(true);
            return repointed;
        }
        catch
        {
            return false; // Outlook busy or closed: the next load tries again
        }
        finally
        {
            _resolvingSent = false;
        }
    }

    /// <summary>
    /// Just after a new message goes, its copy can take a moment to reach Sent
    /// Items; look a few times rather than leave the card without its email.
    /// </summary>
    public async Task ResolveSentCopiesSoonAsync()
    {
        foreach (var wait in new[] { 5, 20, 60, 180 })
        {
            await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(true);
            if (!_all.Any(i => i.IsAwaitingSentCopy && !i.IsComplete)) return;
            if (await ResolveSentCopiesAsync().ConfigureAwait(true)) return;
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
        var items = IsDoneLog ? DoneLog : Columns[ActiveColumn].Items;
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

    public async Task<bool> AddBlockerFromFormAsync()
    {
        if (!await SubmitAsync(EditorMode.Blocker, NewBlockerWhat, NewBlockerWho, NewBlockerDue).ConfigureAwait(true)) return false;
        NewBlockerWhat = NewBlockerWho = NewBlockerDue = "";
        return true;
    }

    public async Task<bool> AddAssignmentFromFormAsync()
    {
        if (!await SubmitAsync(EditorMode.Assignment, NewAssignWho, NewAssignWhat, NewAssignDue).ConfigureAwait(true)) return false;
        NewAssignWho = NewAssignWhat = NewAssignDue = "";
        return true;
    }

    public Task<bool> SaveNotesFromFormAsync() => SubmitAsync(EditorMode.Note, NotesDraft, "", "");

    public Task<bool> SaveDueFromFormAsync() => SubmitAsync(EditorMode.Due, DueDraft, "", "");

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

    partial void OnIsByPersonChanged(bool value)
    {
        if (value) IsDoneLog = false;
        OnPropertyChanged(nameof(IsBoardShown));
    }

    partial void OnIsDoneLogChanged(bool value) => OnPropertyChanged(nameof(IsBoardShown));

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

        // A prefetch part way through this very card is left to finish: stopping
        // it would throw away the reads it has made and start the card over.
        var underway = item is not null
                       && _bodies.TryGet(item.InternetMessageId, out var pending)
                       && !pending.IsCompleted;
        if (!underway) _prefetch?.Cancel();

        if (item is null) { BodyHtml = ""; return; }

        var cts = new CancellationTokenSource();
        _bodyLoad = cts;

        try
        {
            var page = PageAsync(item);

            // A card nothing has read yet - typically one far down a column,
            // which prefetch never reached - costs an Outlook read per message
            // in its conversation. Show the flagged mail as soon as it is in
            // rather than leave the pane on the old card until all are.
            if (!page.IsCompleted) await ShowFlaggedAsync(item, page, cts.Token).ConfigureAwait(true);

            string html;
            try { html = await page.ConfigureAwait(true); }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                // A prefetch of this card that was stopped part way: read it for real.
                _bodies.Remove(item.InternetMessageId);
                html = await PageAsync(item).ConfigureAwait(true);
            }
            if (cts.IsCancellationRequested) return;

            BodyHtml = html;

            // With this one on screen, get its neighbours ready while the user reads.
            StartPrefetch(item);
        }
        catch (Exception ex)
        {
            _bodies.Remove(item.InternetMessageId);
            if (!cts.IsCancellationRequested)
                BodyHtml = HtmlPresenter.Render(Placeholder($"Could not open the email: {ex.Message}"), true);
        }
    }

    /// <summary>The task's conversation page, from cache or started now.</summary>
    private Task<string> PageAsync(ActionItem item, CancellationToken stop = default) =>
        _bodies.GetOrAdd(item.InternetMessageId, _ => RenderBodyAsync(item, stop));

    /// <summary>
    /// The flagged mail on its own, as a stand-in while the rest of the
    /// conversation is still being read. Shares the body read the whole
    /// page needs anyway, so it costs no extra trip to Outlook.
    /// </summary>
    private async Task ShowFlaggedAsync(ActionItem item, Task whole, CancellationToken ct)
    {
        if (item.IsAwaitingSentCopy || string.IsNullOrEmpty(item.EntryId)) return;

        try
        {
            var body = await MailBodyAsync(new MailRef(item.EntryId, item.StoreId)).ConfigureAwait(true);
            if (ct.IsCancellationRequested || whole.IsCompleted) return;

            var blockRemote = _settings.BlockRemoteImages;
            var html = await Task.Run(() => HtmlPresenter.Render(body, blockRemote)).ConfigureAwait(true);
            if (ct.IsCancellationRequested || whole.IsCompleted) return;

            BodyHtml = html;
        }
        catch { /* moved or gone: the whole page finds it again or says so */ }
    }

    /// <summary>
    /// One message's body, shared between the pane, the stand-in and the
    /// prefetcher so each is read from Outlook once. Fetched without a
    /// cancellation token, as another caller may be waiting on the same read.
    /// </summary>
    private async Task<MailBody> MailBodyAsync(MailRef mail)
    {
        var task = _mail.GetOrAdd(mail.EntryId, _ => _store.GetBodyAsync(mail));
        try { return await task.ConfigureAwait(true); }
        catch { _mail.Remove(mail.EntryId); throw; }
    }

    /// <summary>
    /// Renders the task's whole conversation, newest first, re-finding the
    /// flagged mail by Message-ID if it has been filed since. <paramref name="stop"/>
    /// is checked between reads, so a prefetch the user has moved on from
    /// stops queuing work on Outlook's single thread ahead of the card they want.
    /// </summary>
    private async Task<string> RenderBodyAsync(ActionItem item, CancellationToken stop = default)
    {
        if (item.IsAwaitingSentCopy)
            return HtmlPresenter.Render(Placeholder(
                "Your message has not reached Sent Items yet - it shows here once it has been sent."), true);

        var mail = await ResolveAsync(item).ConfigureAwait(true);
        if (mail is null)
            return HtmlPresenter.Render(Placeholder("The email could not be found - it may have been deleted."), true);

        stop.ThrowIfCancellationRequested();

        IReadOnlyList<MailSummary> thread;
        try { thread = await _store.GetConversationAsync(mail.Value, _settings.ThreadMessageLimit).ConfigureAwait(true); }
        catch { thread = Array.Empty<MailSummary>(); }

        _replyTargets[item.InternetMessageId] = thread.FirstOrDefault(m => !m.IsSent)?.Ref ?? mail.Value;

        var bodies = new List<MailBody>();
        foreach (var m in thread)
        {
            stop.ThrowIfCancellationRequested();
            try { bodies.Add(await MailBodyAsync(m.Ref).ConfigureAwait(true)); }
            catch { /* one unreadable message should not hide the rest */ }
        }
        if (bodies.Count == 0) bodies.Add(await MailBodyAsync(mail.Value).ConfigureAwait(true));

        var blockRemote = _settings.BlockRemoteImages;
        return await Task.Run(() => HtmlPresenter.RenderThread(bodies, blockRemote)).ConfigureAwait(true);
    }

    /// <summary>
    /// Where the flagged mail is now, updating the stored location if it moved.
    /// Reading its body is the check that it is still there: the pane needs
    /// that read anyway, so a card that has not moved costs no extra trip.
    /// </summary>
    private async Task<MailRef?> ResolveAsync(ActionItem item)
    {
        if (item.IsAwaitingSentCopy) return null;

        var known = new MailRef(item.EntryId, item.StoreId);
        if (!known.IsEmpty)
        {
            try
            {
                await MailBodyAsync(known).ConfigureAwait(true);
                return known;
            }
            catch { /* moved or deleted: look for it by Message-ID */ }
        }

        var found = await _store.FindByMessageIdAsync(item.InternetMessageId, null).ConfigureAwait(true);
        if (found is null) return null;

        await _repo.UpdateLocationAsync(item.InternetMessageId, found.Value.EntryId, found.Value.StoreId).ConfigureAwait(true);
        item.EntryId = found.Value.EntryId;
        item.StoreId = found.Value.StoreId;
        return found;
    }

    /// <summary>
    /// Loads the next few cards below the selected one in its column, and
    /// the one above, into the caches. One card at a time, so a click the
    /// user makes meanwhile waits for at most a single body read.
    /// </summary>
    private void StartPrefetch(ActionItem from)
    {
        _prefetch?.Cancel();
        if (_settings.PrefetchAhead <= 0) return;

        IList<ActionItem>? items = IsDoneLog ? DoneLog : Columns.FirstOrDefault(c => c.Items.Contains(from))?.Items;
        var index = items?.IndexOf(from) ?? -1;
        if (items is null || index < 0) return;

        var targets = items.Skip(index + 1).Take(_settings.PrefetchAhead).ToList();
        if (index > 0) targets.Add(items[index - 1]);

        var cts = new CancellationTokenSource();
        _prefetch = cts;
        _ = PrefetchAsync(targets, cts.Token);
    }

    private async Task PrefetchAsync(IReadOnlyList<ActionItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;

            var page = PageAsync(item, ct);
            try { await page.ConfigureAwait(true); }
            catch
            {
                // Only a head start; the real open will report any problem.
                // A stopped or failed page must not be served from the cache.
                if (_bodies.TryGet(item.InternetMessageId, out var cached) && cached == page)
                    _bodies.Remove(item.InternetMessageId);
            }
        }
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

    /// <summary>Where a drag-and-drop lands a card.</summary>
    public async Task MoveToColumnAsync(ActionItem item, BoardColumn column)
    {
        if (column.IsFollowUps)
        {
            Select(item);
            Status = "Cards come here on their follow-up day - give a hand-off a date (Shift+A), or set one when you send";
            return;
        }

        await MoveToStageAsync(item, column.Stage).ConfigureAwait(true);
        NoteIfStillInFollowUp(item);
    }

    /// <summary>A follow-up card keeps its place until it is chased or its wait is cleared; say so.</summary>
    private void NoteIfStillInFollowUp(ActionItem item)
    {
        if (FollowUps.Items.Any(i => i.Id == item.Id))
            Status += $"  ·  it stays in Follow up until chased ({ChaseKey}) or cleared ({ClearKey})";
    }

    private string ClearKey => _keys.Describe(TriageAction.ClearWait) is { Length: > 0 } key ? key : "w";

    /// <summary>Moves a card straight to a stage.</summary>
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
                    Status = $"Assigned to {name} · moved to Waiting · nothing sent yet ({ChaseKey} drafts a chase email)";
                    break;
            }

            CloseEditor();

            // A due date given in the stale review answers that card.
            if (IsReviewing && Editor == EditorMode.None && ReviewItem is { } reviewing && item.Id == reviewing.Id)
            {
                await AdvanceReviewAsync().ConfigureAwait(true);
                return;
            }

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
            NoteIfStillInFollowUp(item);
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

    /// <summary>
    /// `x`: a finished card leaves the board at once and the cursor lands on
    /// the next one - the strip below counts it and `z` brings it back. On a
    /// card in the done log, reopens it.
    /// </summary>
    public async Task ToggleCompleteAsync()
    {
        if (Selected is not { } item) return;

        if (item.IsComplete)
        {
            await ReopenAsync(item).ConfigureAwait(true);
            return;
        }

        // Where the cursor was, so it lands on the next card rather than the top.
        var column = ActiveColumn;
        var index = Columns[column].Items.IndexOf(item);

        try
        {
            await _repo.SetCompletedAsync(item.Id, true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not update: {ex.Message}";
            return;
        }

        // The board is changed in hand rather than reloaded: the card goes
        // and the next one is selected before anything else happens. The
        // done strip and Outlook's category catch up behind, so a run of
        // `x` presses never waits on Outlook's single thread.
        _lastDone = item;
        RemoveFromBoard(item);
        if (!IsDoneLog && index >= 0) SelectInColumn(column, index);

        Status = $"Done · {item.DisplayTitle} · {UndoKey} puts it back";

        _ = CatchUpAfterDoneAsync(item);
    }

    /// <summary>A card in the done log goes back to Doing, and the board is read again.</summary>
    private async Task ReopenAsync(ActionItem item)
    {
        try
        {
            await _repo.SetCompletedAsync(item.Id, false).ConfigureAwait(true);
            Status = "Reopened · back in Doing";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not update: {ex.Message}";
        }
    }

    /// <summary>
    /// Takes a finished card out of everything the board shows, and brings
    /// the counts and the report into line, without going back to the database.
    /// </summary>
    private void RemoveFromBoard(ActionItem item)
    {
        _all = _all.Where(i => i != item).ToList();
        foreach (var column in Columns) column.Remove(item);

        OpenCount = _all.Count;
        WaitingCount = _all.Count(i => i.IsWaiting);
        OverdueCount = _all.Count(i => i.IsOverdue);
        if (item.IsInFollowUp) ScheduledFollowUpCount = Math.Max(0, ScheduledFollowUpCount - 1);
        if (item.IsFollowUpDue) FollowUpDueCount = Math.Max(0, FollowUpDueCount - 1);
        if (item.IsStale) StaleCount = Math.Max(0, StaleCount - 1);

        WaitingOnPeople.Clear();
        foreach (var (person, count, overdue) in ActionWorkflow.WaitingOn(_all))
            WaitingOnPeople.Add(new WaitingChip(person, count, overdue));

        RebuildReport();
    }

    /// <summary>
    /// What a finished card still needs that the user need not wait for: the
    /// done strip counting it, and its Outlook category cleared so the two
    /// views agree. Each reports nothing on failure - the card is done
    /// either way, and the next load puts the strip right.
    /// </summary>
    private async Task CatchUpAfterDoneAsync(ActionItem item)
    {
        var strip = RefreshDoneAsync();
        await ClearCategoryAsync(item).ConfigureAwait(true);
        try { await strip.ConfigureAwait(true); }
        catch { /* the strip is only a summary; the next load fills it */ }
    }

    private async Task ClearCategoryAsync(ActionItem item)
    {
        if (string.IsNullOrEmpty(item.EntryId)) return;
        try
        {
            await _store.SetCategoryAsync(
                new MailRef(item.EntryId, item.StoreId), _settings.ActionCategory, false).ConfigureAwait(true);
        }
        catch { /* the mail may have been filed or deleted since */ }
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

    /// <summary>
    /// True when `c` on this card should mail the assignee directly rather
    /// than follow up in the conversation: there is a hand-off with an address.
    /// </summary>
    public bool SelectedChasesAssignee =>
        Selected?.Assignments.Any(a => !a.IsDone && a.PersonEmail.Length > 0) == true;

    /// <summary>
    /// Drafts a chase email to <paramref name="which"/> hand-off, or else the
    /// first person with an open one.
    /// </summary>
    public async Task ChaseAsync(Assignment? which = null)
    {
        if (Selected is not { } item) return;

        var assignment = which
                         ?? item.Assignments.FirstOrDefault(a => !a.IsDone && a.PersonEmail.Length > 0)
                         ?? item.Assignments.FirstOrDefault(a => !a.IsDone);

        if (assignment is null)
        {
            Status = "No open hand-off to chase - assign it with Shift+A first";
            return;
        }

        await DraftAssignmentMailAsync(assignment).ConfigureAwait(true);
    }

    /// <summary>
    /// Restarts the staleness clock once a chase exists. A card in Follow up
    /// has been dealt with, so the board reloads to put it back in Waiting.
    /// </summary>
    public async Task MarkFollowedUpAsync(ActionItem item)
    {
        await _repo.MarkFollowedUpAsync(item.Id).ConfigureAwait(true);
        item.LastFollowUpUtc = _clock.UtcNow;
        item.FollowUpDays = 0;
        if (FollowUpDueCount > 0) FollowUpDueCount--;

        if (FollowUps.Items.Any(i => i.Id == item.Id))
        {
            var status = Status;
            await LoadAsync().ConfigureAwait(true);
            Status = status;
        }
    }

    /// <summary>
    /// Builds an unsent mail asking someone for what they owe, and shows it in
    /// Outlook. Claude writes the body (in the user's voice, aware of how long
    /// the wait has been); without Claude it falls back to the plain template.
    /// Explicitly does not send: the user reviews and presses send.
    /// </summary>
    public async Task DraftAssignmentMailAsync(Assignment assignment)
    {
        if (Selected is { } item) await DraftAssignmentMailAsync(item, assignment).ConfigureAwait(true);
    }

    /// <summary>The same, for a card that is not the selected one - a hand-off just captured from the inbox.</summary>
    public async Task DraftAssignmentMailAsync(ActionItem item, Assignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.PersonEmail))
        {
            Status = $"No email address on file for {assignment.PersonName}";
            return;
        }

        try
        {
            var body = await BuildChaseBodyAsync(item, assignment).ConfigureAwait(true);

            await _store.CreateAndShowDraftAsync(
                new[] { assignment.PersonEmail },
                $"Action needed: {item.Subject}",
                body).ConfigureAwait(true);

            await _repo.MarkAssignmentDraftedAsync(assignment.Id).ConfigureAwait(true);
            assignment.NotifiedUtc = _clock.UtcNow;
            await MarkFollowedUpAsync(item).ConfigureAwait(true);

            Status = $"Draft opened in Outlook for {assignment.PersonName} - review and send it there";
            OnPropertyChanged(nameof(Selected));
        }
        catch (Exception ex)
        {
            Status = $"Could not create the draft: {ex.Message}";
        }
    }

    private async Task<string> BuildChaseBodyAsync(ActionItem item, Assignment assignment)
    {
        try
        {
            Status = $"Claude is drafting the chase to {assignment.PersonName}...";

            string style;
            try { style = await _style.GetAsync(_settings.ResolveStyleModel()).ConfigureAwait(true); }
            catch { style = ""; }

            var since = assignment.NotifiedUtc ?? assignment.CreatedUtc;
            var brief =
                $"Ask {assignment.PersonName} for: {assignment.Task}. " +
                (assignment.DueUtc is { } d ? $"It is needed by {d.ToLocalTime():dddd d MMM}. " : "") +
                (assignment.HasBeenDrafted
                    ? $"They were first asked around {since.ToLocalTime():ddd d MMM} " +
                      $"({Math.Max(0, (int)(_clock.UtcNow - since).TotalDays)} days ago), so this is a follow-up nudge - " +
                      "friendly, no guilt-tripping, ask for an update or a date."
                    : "This is the first ask, so give them the context they need.") +
                $" It relates to the email thread \"{item.Subject}\"" +
                (item.Notes.Length > 0 ? $". Background notes: {item.Notes}" : ".");

            var text = await _aiDraft.DraftAsync(new DraftContext
            {
                Subject = $"Action needed: {item.Subject}",
                Kind = "new message",
                Recipients = assignment.PersonName,
                Instructions = brief,
                Style = style,
            }, _settings.ResolveFollowUpModel()).ConfigureAwait(true);

            return HtmlPresenter.ComposeReplyFragment(text);
        }
        catch (Exception)
        {
            // No Claude, or it failed: the plain template still gets the ask out.
            var due = assignment.DueUtc is { } d
                ? $"<p>Ideally by <strong>{d.ToLocalTime():dddd d MMM}</strong>.</p>"
                : "";

            return $"""
                <div style="font-family:Calibri,sans-serif;font-size:11pt">
                  <p>Hi {System.Net.WebUtility.HtmlEncode(assignment.PersonName.Split(' ')[0])},</p>
                  <p>{System.Net.WebUtility.HtmlEncode(assignment.Task)}</p>
                  {due}
                  <p>This came out of: <em>{System.Net.WebUtility.HtmlEncode(item.Subject)}</em></p>
                  <p>Thanks</p>
                </div>
                """;
        }
    }

    /// <summary>Brings the originating mail up in Outlook for the full context.</summary>
    public async Task OpenInOutlookAsync()
    {
        if (Selected is not { } item) return;

        try
        {
            var found = await ResolveAsync(item).ConfigureAwait(true);

            if (found is null)
            {
                Status = "Could not find that message - it may have been deleted";
                return;
            }

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
