using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// Remembers one reversible triage action. A step is recorded the moment the
/// key is pressed, before any background Outlook work finishes, so undo always
/// meets the newest action first; <see cref="IsEmpty"/> reports that the work
/// then came to nothing and there is nothing to revert.
/// </summary>
internal sealed record UndoStep(string Description, Func<Task> Revert, Func<bool>? IsEmpty = null);

/// <summary>
/// The inbox triage surface: the list, the reading pane, and the commands that
/// move mail out of the way. Every operation here is meant to be reachable
/// without the mouse.
/// </summary>
public sealed partial class TriageViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly IActionItemRepository _actions;
    private readonly ISnoozeRepository _snoozes;
    private readonly FolderSearchService _folders;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly ICalendarStore _calendar;
    private readonly AiDraftService _aiDraft;
    private readonly AiSearchService _aiSearch;
    private readonly WritingStyleService _style;

    private FolderRef _inbox;
    private FolderRef? _sent;
    private readonly List<UndoStep> _undo = new();
    private CancellationTokenSource? _bodyLoad;
    private CancellationTokenSource? _prefetch;

    // The folders e and h move mail into, found once and kept: walking to
    // them through COM on every keystroke held the next email off the screen.
    private Task<FolderRef>? _archiveFolder;
    private Task<FolderRef>? _snoozeFolder;

    // Bodies by EntryId and finished thread pages by their message list. Tasks
    // rather than values, so the reading pane and the prefetcher share one
    // fetch when both want the same message. UI thread only.
    private readonly LruCache<string, Task<MailBody>> _bodies = new(120, StringComparer.Ordinal);
    private readonly LruCache<string, Task<string>> _pages = new(24, StringComparer.Ordinal);
    private IReadOnlyList<SnoozeOption> _snoozePresets = Array.Empty<SnoozeOption>();

    // Recipient addresses by EntryId, read on demand for "to:*@acme"-style
    // searches. A message's recipients never change, so this lives all session.
    private readonly Dictionary<string, MailRecipients> _recipients = new(StringComparer.Ordinal);
    private bool _recipientsLoading;

    public ObservableCollection<MailRowViewModel> Rows { get; } = new();
    public PaletteViewModel Palette { get; } = new();
    public ComposerViewModel Composer { get; }

    /// <summary>The popup behind `a`: what, who, when, before the flag is filed.</summary>
    public CaptureViewModel Capture { get; }

    [ObservableProperty] private MailRowViewModel? _selected;

    // Where the caret last sat in the list. The list box clears its selection
    // when rows shift under it (a live refresh, a move), and j or a refresh
    // must carry on from here rather than jump back to the top.
    private int _caret = -1;

    [ObservableProperty] private MailBody? _openBody;

    /// <summary>
    /// The one message picked under the expanded row, shown on its own with
    /// its attachments; null shows the whole conversation.
    /// </summary>
    [ObservableProperty] private ConversationMessageViewModel? _focusedMessage;

    /// <summary>The attachment file shown in the reading pane instead of the email, if any.</summary>
    [ObservableProperty] private string? _previewPath;
    [ObservableProperty] private string _previewName = "";

    /// <summary>Attachments from every message in the open conversation, newest first.</summary>
    [ObservableProperty] private IReadOnlyList<MailAttachment> _threadAttachments = Array.Empty<MailAttachment>();
    [ObservableProperty] private string _bodyHtml = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>What the empty list says: loading, stuck on Outlook, failed, or genuinely clear.</summary>
    [ObservableProperty] private string _emptyListText = "Inbox is clear";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;

    /// <summary>The search box is in ask-Claude mode: Enter runs the question.</summary>
    [ObservableProperty] private bool _isAiSearch;
    [ObservableProperty] private bool _isAiSearchRunning;

    /// <summary>The last AI answer's conversations, best first; null when no AI filter is on.</summary>
    private IReadOnlyList<string>? _aiFilterKeys;

    /// <summary>The list shows only unread conversations, on top of any search.</summary>
    [ObservableProperty] private bool _showUnreadOnly;

    private List<MailRowViewModel> _allRows = new();

    // Conversations a field search found in Outlook beyond the loaded Inbox
    // page - older mail, the Archive, filed folders - and the filter that
    // found them. Dropped when the search changes or closes.
    private List<MailRowViewModel> _searchRows = new();
    private string? _outlookFilter;
    private CancellationTokenSource? _outlookSearch;

    // Reloads are serialised: one that arrives mid-load is folded into a
    // single follow-up pass rather than racing the one in flight.
    private bool _loadRunning;
    private bool _reloadPending;
    private bool _pendingQuiet = true;

    // Triage actions update the list at once and do their Outlook work here,
    // one after another, so the next keystroke never waits on a move.
    private Task _writes = Task.CompletedTask;

    // Conversations on their way out of the Inbox, so a refresh that reads the
    // folder mid-move does not bring them back. The value is int.MaxValue while
    // the move runs, then the number of the last load started before it
    // finished: only a load started after that is trusted to show the truth.
    private readonly Dictionary<string, int> _leaving = new(StringComparer.OrdinalIgnoreCase);
    private int _loadNumber;

    /// <summary>Where a Shift+arrow selection started.</summary>
    private MailRowViewModel? _anchor;
    private bool _warmed;

    public TriageViewModel(
        IMailStore store,
        IActionItemRepository actions,
        ISnoozeRepository snoozes,
        FolderSearchService folders,
        AppSettings settings,
        IClock clock,
        ContactDirectory contacts,
        IScheduledSendRepository scheduled,
        ICalendarStore calendar,
        AiDraftService aiDraft,
        AiSearchService aiSearch,
        WritingStyleService style)
    {
        _style = style;
        _store = store;
        _calendar = calendar;
        _actions = actions;
        _snoozes = snoozes;
        _folders = folders;
        _settings = settings;
        _clock = clock;
        _aiDraft = aiDraft;
        _aiSearch = aiSearch;

        Composer = new ComposerViewModel(store, contacts, scheduled, clock, settings.DayShape);
        Capture = new CaptureViewModel(contacts, clock, settings);
        Palette.QueryChanged += (_, _) => RefreshPalette();
    }

    public bool CanUndo => _undo.Count > 0;

    /// <summary>
    /// The Archive beside the Inbox, created if missing, fetched once per
    /// session. A faulted fetch is retried on the next use rather than cached.
    /// </summary>
    private Task<FolderRef> GetArchiveFolderAsync()
    {
        var task = _archiveFolder;
        if (task is null || task.IsFaulted || task.IsCanceled)
            _archiveFolder = task = _store.EnsureFolderPathAsync("Archive");
        return task;
    }

    /// <summary>The snooze holding folder, likewise fetched once and kept.</summary>
    private Task<FolderRef> GetSnoozeFolderAsync()
    {
        var task = _snoozeFolder;
        if (task is null || task.IsFaulted || task.IsCanceled)
            _snoozeFolder = task = _store.EnsureFolderPathAsync(_settings.SnoozeFolder);
        return task;
    }

    public Task LoadAsync(CancellationToken ct = default) => LoadCoreAsync(quiet: false, ct);

    /// <summary>
    /// A refresh prompted by Outlook rather than by the user: it leaves the
    /// status line alone, so "Moved to Archive" is not wiped by the change
    /// notification that very move causes.
    /// </summary>
    public Task RefreshQuietlyAsync() => LoadCoreAsync(quiet: true, default);

    private async Task LoadCoreAsync(bool quiet, CancellationToken ct)
    {
        if (_loadRunning)
        {
            _reloadPending = true;
            _pendingQuiet &= quiet;
            return;
        }

        _loadRunning = true;
        try
        {
            do
            {
                _reloadPending = false;
                await LoadOnceAsync(quiet, ct).ConfigureAwait(true);

                quiet = _pendingQuiet;
                _pendingQuiet = true;
            }
            while (_reloadPending);
        }
        finally
        {
            _loadRunning = false;
        }
    }

    private async Task LoadOnceAsync(bool quiet, CancellationToken ct)
    {
        if (!quiet)
        {
            IsLoading = true;
            Status = "Loading inbox...";
            EmptyListText = "Loading inbox...";
        }
        var loadNumber = ++_loadNumber;

        try
        {
            var (mail, sent) = quiet
                ? await ReadFromOutlookAsync(ct).ConfigureAwait(true)
                : await ReadFromOutlookAsync(ct).WarnIfSlow(OutlookStallAfter, () =>
                {
                    Status = OutlookStallWarning;
                    EmptyListText = "Waiting on Outlook...";
                }).ConfigureAwait(true);

            var threads = ConversationGrouper.Group(mail, sent)
                .Where(t => !StillLeaving(t.Key, loadNumber))
                .ToList();

            var flagged = (await _actions.GetOpenAsync(ct).ConfigureAwait(true))
                .Select(a => a.InternetMessageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Reuse the row objects for conversations that are still there.
            // Rebuilding them all would drop the selection and reload the
            // reading pane on every change notification.
            var existing = new Dictionary<string, MailRowViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _allRows) existing.TryAdd(row.Key, row);

            // Warm the Archive ref so the first e press pays nothing extra.
            // (Most mailboxes already have Archive; this is a lookup, not a create.)
            _ = GetArchiveFolderAsync();

            _allRows = threads.Select(t =>
            {
                var isFlagged = t.InboxMessages.Any(m => flagged.Contains(m.InternetMessageId));
                if (existing.Remove(t.Key, out var row))
                {
                    row.Refresh(t);
                    row.IsActionRequired = isFlagged;
                    return row;
                }
                return new MailRowViewModel(t, isFlagged);
            }).ToList();

            var selected = Selected;
            var selectedIndex = selected is not null && Rows.IndexOf(selected) is var at and >= 0 ? at : _caret;

            ApplySearchFilter();

            if (selected is null || !Rows.Contains(selected) || Selected != selected)
            {
                // A conversation that came back (an undone move) is still the
                // same one; otherwise land on its neighbour.
                var key = selected?.Key;
                Selected = (string.IsNullOrEmpty(key)
                               ? null
                               : Rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)))
                           ?? (Rows.Count == 0 ? null : Rows[Math.Clamp(selectedIndex, 0, Rows.Count - 1)]);
            }

            EmptyListText = "Inbox is clear";
            if (!quiet)
            {
                Status = $"{Rows.Count} conversation{(Rows.Count == 1 ? "" : "s")}"
                       + $"  ·  {Rows.Count(r => r.IsUnread)} unread";
            }

            // Read the folder list now, so the first `v` opens straight away.
            if (!_warmed)
            {
                _warmed = true;
                _ = WarmFolderIndexAsync();
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not read the inbox: {ex.Message}";
            EmptyListText = "Could not read the inbox";
        }
        finally
        {
            if (!quiet) IsLoading = false;
        }
    }

    private async Task WarmFolderIndexAsync()
    {
        try { await _folders.EnsureIndexedAsync().ConfigureAwait(true); }
        catch { /* `v` will try again and report any problem */ }
    }

    private bool StillLeaving(string key, int loadNumber)
    {
        if (!_leaving.TryGetValue(key, out var until)) return false;
        if (loadNumber <= until) return true;

        _leaving.Remove(key);
        return false;
    }

    /// <summary>The Outlook work for these rows is over, whether or not it worked.</summary>
    private void Settle(IEnumerable<MailRowViewModel> rows)
    {
        foreach (var row in rows) _leaving[row.Key] = _loadNumber;
    }

    /// <summary>Queues Outlook work behind whatever triage work is already running.</summary>
    private Task RunInBackground(Func<Task> work)
    {
        var run = RunAfterAsync(_writes, work);
        _writes = run;
        return run;
    }

    private async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        try { await previous.ConfigureAwait(true); } catch { /* reported by its own work */ }
        try { await work().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"That did not work: {ex.Message}"; }
    }

    /// <summary>
    /// How long a load may take before the status line says Outlook is holding
    /// it up. A normal first read takes a few seconds even on a large inbox.
    /// </summary>
    private static readonly TimeSpan OutlookStallAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reading the list asks Outlook for sender addresses, which is exactly
    /// what its "A program is trying to access email address information"
    /// prompt guards. Outlook holds every call until someone answers it, and
    /// it often opens behind other windows.
    /// </summary>
    internal const string OutlookStallWarning =
        "Still waiting on Outlook - it is probably showing a dialog (often \"A program is trying to access " +
        "email address information\"). Switch to Outlook and click Allow; the list loads as soon as you do.";

    /// <summary>The inbox page, and your side of each conversation from Sent Items.</summary>
    private async Task<(IReadOnlyList<MailSummary> Mail, IReadOnlyList<MailSummary> Sent)> ReadFromOutlookAsync(
        CancellationToken ct)
    {
        _inbox = await _store.GetInboxAsync(ct).ConfigureAwait(true);
        _sent ??= await _store.GetSentItemsAsync(ct).ConfigureAwait(true);

        var mail = await _store
            .GetMailAsync(_inbox, _settings.InboxPageSize, ct).ConfigureAwait(true);

        // Only decoration for the list, so a failure here still shows the
        // Inbox rather than nothing.
        IReadOnlyList<MailSummary> sent;
        try { sent = await _store.GetMailAsync(_sent.Value, _settings.SentPageSize, ct).ConfigureAwait(true); }
        catch (Exception) when (!ct.IsCancellationRequested) { sent = Array.Empty<MailSummary>(); }

        return (mail, sent);
    }

    private void ApplySearchFilter()
    {
        // An AI answer pins the list to its conversations, in its order, until
        // Esc clears it - a live refresh must not silently widen the results.
        if (_aiFilterKeys is { } keys)
        {
            var byKey = new Dictionary<string, MailRowViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _allRows) byKey.TryAdd(row.Key, row);

            ShowRows(keys
                .Select(k => byKey.GetValueOrDefault(k))
                .Where(r => r is not null)
                .Select(r => r!));
            return;
        }

        // In ask mode the box holds a question, not a filter; the list stays
        // whole until Enter sends the question to Claude.
        var query = InboxQuery.Parse(IsAiSearch ? "" : SearchQuery);
        if (query.OutlookFilter != _outlookFilter) StartOutlookSearch(query.OutlookFilter);
        if (query.NeedsAddresses) _ = LoadRecipientsAsync();

        if (query.IsEmpty) { ShowRows(_allRows); return; }

        var matches = SearchableRows().Where(r => query.Matches(field => SearchValues(r, field)));
        ShowRows(_searchRows.Count == 0 ? matches : matches.OrderByDescending(r => r.Thread.LastActivityUtc));
    }

    /// <summary>
    /// Shows these rows, less the read ones when the unread filter is on. The
    /// selected row stays even once read, so opening a mail does not pull it
    /// out from under the caret; it drops out on the next refresh after the
    /// caret moves on.
    /// </summary>
    private void ShowRows(IEnumerable<MailRowViewModel> rows)
    {
        var selected = Selected;
        SyncRows((ShowUnreadOnly ? rows.Where(r => r.IsUnread || r == selected) : rows).ToList());
    }

    partial void OnShowUnreadOnlyChanged(bool value)
    {
        RefilterForSearch();
        Status = value
            ? $"{Rows.Count} unread conversation{(Rows.Count == 1 ? "" : "s")}"
            : $"{Rows.Count} conversation{(Rows.Count == 1 ? "" : "s")}";
    }

    /// <summary>The loaded Inbox, plus whatever a field search found beyond it.</summary>
    private IEnumerable<MailRowViewModel> SearchableRows()
    {
        if (_searchRows.Count == 0) return _allRows;

        var inbox = _allRows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _allRows.Concat(_searchRows.Where(r => !inbox.Contains(r.Key)));
    }

    /// <summary>
    /// The list only holds the newest Inbox page, so "from:*@acme" alone
    /// would miss older and archived mail. Field terms are also run as an
    /// Outlook filter over every mail folder, once typing pauses, and what
    /// comes back joins the results.
    /// </summary>
    private void StartOutlookSearch(string? filter)
    {
        _outlookSearch?.Cancel();
        _outlookSearch = null;
        _outlookFilter = filter;
        _searchRows = new();
        if (filter is null) return;

        var cts = _outlookSearch = new CancellationTokenSource();
        _ = SearchOutlookAsync(filter, cts.Token);
    }

    private async Task SearchOutlookAsync(string filter, CancellationToken ct)
    {
        try
        {
            // Each keystroke would otherwise walk every folder.
            await Task.Delay(400, ct).ConfigureAwait(true);

            Status = "Searching all mail folders...";
            var found = await _store.SearchMailAsync(filter, _settings.SearchResultLimit, ct).ConfigureAwait(true);
            var flagged = (await _actions.GetOpenAsync(ct).ConfigureAwait(true))
                .Select(a => a.InternetMessageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ct.IsCancellationRequested) return;

            _searchRows = ConversationGrouper.Group(found, Array.Empty<MailSummary>())
                .Select(t => new MailRowViewModel(t, t.InboxMessages.Any(m => flagged.Contains(m.InternetMessageId))))
                .ToList();

            RefilterForSearch();
            Status = $"{Rows.Count} match{(Rows.Count == 1 ? "" : "es")} across all mail folders";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Status = $"Could not search Outlook beyond the Inbox: {ex.Message}";
        }
    }

    /// <summary>The text each search field can match in one conversation.</summary>
    private IEnumerable<string> SearchValues(MailRowViewModel row, QueryField field)
    {
        switch (field)
        {
            case QueryField.Text:
                // Bare words search everything cheap to hand: subjects, people
                // on every message, and the preview line.
                yield return row.Subject;
                yield return row.Sender;
                foreach (var m in row.Thread.Messages)
                {
                    yield return m.Subject;
                    yield return m.SenderName;
                    yield return m.SenderAddress;
                    yield return m.DisplayTo;
                    yield return m.DisplayCc;
                    yield return m.Preview;
                    if (_recipients.TryGetValue(m.Ref.EntryId, out var people))
                        foreach (var r in people.To.Concat(people.Cc)) { yield return r.Name; yield return r.Address; }
                }
                yield break;

            case QueryField.Subject:
                foreach (var m in row.Thread.Messages) yield return m.Subject;
                yield break;
        }

        // from: is the sender or anyone copied; to: is the To line. Display
        // names come with every row; addresses once LoadRecipientsAsync has them.
        foreach (var m in row.Thread.Messages)
        {
            _recipients.TryGetValue(m.Ref.EntryId, out var known);

            if (field == QueryField.From)
            {
                yield return m.SenderName;
                yield return m.SenderAddress;
                yield return m.DisplayCc;
                if (known is not null)
                    foreach (var r in known.Cc) { yield return r.Name; yield return r.Address; }
            }
            else
            {
                yield return m.DisplayTo;
                if (known is not null)
                    foreach (var r in known.To) { yield return r.Name; yield return r.Address; }
            }
        }
    }

    /// <summary>
    /// Reads recipient addresses for every message in the list that does not
    /// have them yet - a few at a time, so Outlook stays free for the reading
    /// pane - and re-filters as each batch lands.
    /// </summary>
    private async Task LoadRecipientsAsync()
    {
        if (_recipientsLoading) return;
        _recipientsLoading = true;

        try
        {
            while (true)
            {
                var missing = SearchableRows()
                    .SelectMany(r => r.Thread.Messages)
                    .Where(m => !_recipients.ContainsKey(m.Ref.EntryId))
                    .ToList();
                if (missing.Count == 0) break;

                // Bodies already fetched carry their recipients; no need to ask Outlook again.
                var ask = new List<MailRef>();
                foreach (var m in missing)
                {
                    if (_bodies.TryGet(m.Ref.EntryId, out var body) && body.IsCompletedSuccessfully)
                        _recipients[m.Ref.EntryId] = new MailRecipients(body.Result.To, body.Result.Cc);
                    else if (ask.Count < 25)
                        ask.Add(m.Ref);
                }

                if (ask.Count > 0)
                {
                    Status = $"Reading addresses for search... {missing.Count} left";
                    var found = await _store.GetRecipientsAsync(ask).ConfigureAwait(true);

                    // Unreadable messages get an empty entry so the loop moves past them.
                    foreach (var mail in ask)
                        _recipients[mail.EntryId] = found.GetValueOrDefault(mail.EntryId)
                            ?? new MailRecipients(Array.Empty<Recipient>(), Array.Empty<Recipient>());
                }

                if (!IsSearching || !InboxQuery.Parse(SearchQuery).NeedsAddresses) break;
                RefilterForSearch();
            }

            if (IsSearching) Status = $"{Rows.Count} match{(Rows.Count == 1 ? "" : "es")}";
        }
        catch (Exception ex)
        {
            Status = $"Could not read addresses for search: {ex.Message}";
        }
        finally
        {
            _recipientsLoading = false;
        }
    }

    private void RefilterForSearch()
    {
        ApplySearchFilter();
        if (Selected is null || !Rows.Contains(Selected)) Selected = Rows.FirstOrDefault();
    }

    /// <summary>
    /// Brings <see cref="Rows"/> in line with <paramref name="target"/> using the
    /// fewest edits, so the list keeps its scroll position and selection
    /// instead of being cleared and refilled.
    /// </summary>
    private void SyncRows(IReadOnlyList<MailRowViewModel> target)
    {
        var selected = Selected;
        var keep = new HashSet<MailRowViewModel>(target);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i])) continue;

            var at = Rows.IndexOf(target[i]);
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, target[i]);
        }

        // Moving the selected row can make the list box drop its selection.
        if (selected is not null && Selected != selected && Rows.Contains(selected)) Selected = selected;
    }

    /// <summary>The row where the caret last sat, for when the selection has gone missing.</summary>
    private MailRowViewModel? RowAtCaret() =>
        Rows.Count == 0 ? null : Rows[Math.Clamp(_caret, 0, Rows.Count - 1)];

    partial void OnSearchQueryChanged(string value) => RefilterForSearch();

    // ---- AI search (Ctrl+/) and AI drafting (Ctrl+G) -----------------------
    //
    // Both go out through the user's own Claude Code sign-in, and only when
    // the key is pressed: the one deliberate exception to "nothing leaves the
    // machine". The status line says what is happening while Claude works.

    /// <summary>Opens the plain filter box, dropping any AI question or answer first.</summary>
    public void OpenSearch()
    {
        if (HasAiFilter || IsAiSearch)
        {
            _aiFilterKeys = null;
            IsAiSearch = false;
            SearchQuery = "";
            ApplySearchFilter();
        }
        IsSearching = true;
    }

    /// <summary>Opens the search box in ask mode; Enter sends the question to Claude.</summary>
    public void OpenAiSearch()
    {
        _aiFilterKeys = null;
        IsAiSearch = true;
        IsSearching = true;
        SearchQuery = "";
        Status = "Ask your inbox anything - Enter asks Claude, Esc cancels";
    }

    /// <summary>Closes the search box and drops every filter, typed or AI.</summary>
    public void CloseSearch()
    {
        IsSearching = false;
        IsAiSearch = false;
        _aiFilterKeys = null;
        SearchQuery = "";
        ApplySearchFilter();
        if (Selected is null || !Rows.Contains(Selected)) Selected = Rows.FirstOrDefault();
    }

    /// <summary>
    /// A click in the list hands the keyboard back to it, so `f`, `r` and the
    /// rest act on what was clicked instead of typing into the box. A typed
    /// filter stays, as Enter leaves it; an unasked question has nothing to keep.
    /// </summary>
    public void LeaveSearchBox()
    {
        if (!IsSearching || IsAiSearchRunning) return;
        if (IsAiSearch) { IsSearching = false; IsAiSearch = false; SearchQuery = ""; ApplySearchFilter(); }
        else IsSearching = false;
    }

    /// <summary>True while an AI answer is filtering the list, so Esc knows to clear it.</summary>
    public bool HasAiFilter => _aiFilterKeys is not null;

    public async Task RunAiSearchAsync()
    {
        var question = SearchQuery.Trim();
        if (question.Length == 0 || IsAiSearchRunning) return;

        IsAiSearchRunning = true;
        Status = "Asking Claude...";

        try
        {
            var rows = _allRows.Select(r => new AiSearchRow(
                r.Key,
                r.Subject,
                r.Summary.DisplaySender,
                r.Summary.SenderAddress,
                r.Thread.LastActivityUtc,
                r.IsUnread,
                r.Count,
                CachedSnippet(r))).ToList();

            var result = await _aiSearch
                .SearchAsync(question, rows, _clock.Now, _settings.ResolveSearchModel())
                .ConfigureAwait(true);

            _aiFilterKeys = result.Keys;
            IsSearching = false; // focus returns to the list; the filter stays
            IsAiSearch = false;
            ApplySearchFilter();
            Selected = Rows.FirstOrDefault();

            var count = $"{Rows.Count} match{(Rows.Count == 1 ? "" : "es")}";
            Status = result.Answer.Length > 0
                ? $"{result.Answer}  ·  {count} · Esc shows everything again"
                : $"{count} · Esc shows everything again";
        }
        catch (AiUnavailableException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = $"AI search failed: {ex.Message}";
        }
        finally
        {
            IsAiSearchRunning = false;
        }
    }

    /// <summary>
    /// Body text for the search prompt, from the reading pane's cache only -
    /// search must stay instant to start, not fetch 250 bodies from Outlook.
    /// </summary>
    private string CachedSnippet(MailRowViewModel row)
    {
        return _bodies.TryGet(row.Summary.Ref.EntryId, out var task)
               && task.IsCompletedSuccessfully
            ? task.Result.PlainText
            : "";
    }

    private bool _aiDrafting;

    /// <summary>
    /// Ctrl+G. With the composer closed it opens a reply-all first; then Claude
    /// drafts from the conversation - or from the notes already typed in the
    /// box, which it expands into the full message. The draft only ever lands
    /// in the composer; sending stays a human keystroke.
    ///
    /// <paramref name="instructions"/> replaces the typed notes (how follow-up
    /// chases steer the draft) and <paramref name="model"/> overrides the
    /// draft model. Returns true once a draft has landed in the composer.
    /// </summary>
    public async Task<bool> AiDraftAsync(string? instructions = null, string? model = null)
    {
        if (_aiDrafting) return false;

        if (!Composer.IsOpen)
        {
            if (Selected is null)
            {
                Status = "Select a conversation to reply to first";
                return false;
            }

            await StartReplyAsync(ReplyScope.All).ConfigureAwait(true);
            if (!Composer.IsOpen) return false; // StartReplyAsync already said why
        }

        if (Composer.Draft is not { } draft) return false;

        _aiDrafting = true;
        Composer.Status = "Claude is drafting...";

        try
        {
            var style = await GetStyleQuietlyAsync().ConfigureAwait(true);
            if (!Composer.IsOpen || !ReferenceEquals(Composer.Draft, draft)) return false;

            Composer.Status = "Claude is drafting...";
            var context = await BuildDraftContextAsync(draft, instructions ?? Composer.BodyText.Trim())
                .ConfigureAwait(true);
            var (availability, calendarRead) = await GetAvailabilityQuietlyAsync().ConfigureAwait(true);
            context = context with { Style = style, Availability = availability };

            var text = await _aiDraft
                .DraftAsync(context, model ?? _settings.ResolveDraftModel())
                .ConfigureAwait(true);

            // The user may have discarded or sent while Claude wrote.
            if (!Composer.IsOpen || !ReferenceEquals(Composer.Draft, draft)) return false;

            Composer.BodyText = text;
            Composer.CloseSuggestions();
            Composer.Status = calendarRead
                ? "Drafted - read it before sending. Ctrl+G redoes it."
                : "Drafted without your calendar - Outlook did not return it in time, so no times were offered. Read it before sending.";
            return true;
        }
        catch (AiUnavailableException ex)
        {
            if (ReferenceEquals(Composer.Draft, draft)) Composer.Status = ex.Message;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Composer.Draft, draft)) Composer.Status = $"Could not draft: {ex.Message}";
        }
        finally
        {
            _aiDrafting = false;
        }

        return false;
    }

    /// <summary>
    /// The user's writing-style guide, learned from sent mail on the first
    /// draft. A missing or unlearnable style never stops a draft.
    /// </summary>
    private async Task<string> GetStyleQuietlyAsync()
    {
        try
        {
            if (!_style.HasProfile)
                Composer.Status = "Reading your sent mail to learn your writing style (first time only)...";

            return await _style.GetAsync(_settings.ResolveStyleModel()).ConfigureAwait(true);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// When the user is free over the next couple of working weeks, for the
    /// draft to offer times from when the email is about meeting. An
    /// unreadable calendar never stops a draft: Claude is told not to guess.
    /// </summary>
    private async Task<(string Text, bool Read)> GetAvailabilityQuietlyAsync()
    {
        var now = _clock.Now;
        var rules = _settings.Availability;
        try
        {
            // Two weeks of weekdays span at most three calendar weeks, plus the weekend either side.
            var from = new DateTimeOffset(now.Date, now.Offset).AddDays(1);
            var to = from.AddDays(rules.WorkingDays / 5 * 7 + 7);

            // Outlook's one thread has no timeout of its own; a stalled Outlook
            // must not hold the draft at "Claude is drafting..." for ever.
            var read = _calendar.GetEventsAsync(from, to);
            if (await Task.WhenAny(read, Task.Delay(AvailabilityTimeout)).ConfigureAwait(true) != read)
            {
                _ = read.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe a late failure
                return (Availability.Unreadable, false);
            }
            var events = await read.ConfigureAwait(true);

            // The zone's own name ("(UTC-05:00) Eastern Time (US & Canada)") rather
            // than today's offset, which would be wrong for days past a clock change.
            var zone = TimeZoneInfo.Local;
            var days = Availability.FreeWindows(events, now, rules, zone);
            return (Availability.Describe(days, now, rules, zone.DisplayName), true);
        }
        catch
        {
            return (Availability.Unreadable, false);
        }
    }

    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The conversation behind the open draft, as prompt-ready messages. Bodies
    /// come from the reading pane's cache when the thread is the selected one,
    /// and from Outlook when the reply was opened elsewhere (the action board).
    /// </summary>
    private async Task<DraftContext> BuildDraftContextAsync(ReplyDraft draft, string instructions)
    {
        var kind = draft.Scope switch
        {
            ReplyScope.All => "reply to everyone on the conversation below",
            ReplyScope.SenderOnly => "reply to the sender of the conversation below",
            ReplyScope.Forward => "note to send above the forwarded conversation below",
            _ => "new message",
        };

        var messages = new List<DraftMessage>();

        if (draft.Scope != ReplyScope.New && draft.InReplyTo.EntryId.Length > 0)
        {
            var thread = _allRows
                .FirstOrDefault(r => r.Thread.Messages.Any(m => m.Ref.EntryId == draft.InReplyTo.EntryId))
                ?.Thread.Messages;

            var summaries = thread
                ?? await _store.GetConversationAsync(draft.InReplyTo, _settings.ThreadMessageLimit)
                    .ConfigureAwait(true);

            foreach (var summary in summaries.Take(Math.Max(1, _settings.ThreadMessageLimit)))
            {
                var task = _bodies.GetOrAdd(summary.Ref.EntryId, _ => _store.GetBodyAsync(summary.Ref));
                try
                {
                    var body = await task.ConfigureAwait(true);
                    messages.Add(new DraftMessage(
                        summary.DisplaySender, summary.SenderAddress,
                        summary.ReceivedUtc, body.PlainText, summary.IsSent));
                }
                catch
                {
                    // A message that moved or vanished still leaves the rest
                    // of the thread to draft from.
                    _bodies.Remove(summary.Ref.EntryId);
                }
            }
        }

        return new DraftContext
        {
            Subject = Composer.Subject,
            Kind = kind,
            Recipients = draft.RecipientSummary,
            Instructions = instructions,
            Messages = messages,
        };
    }

    public bool IsPreviewing => PreviewPath is not null;

    partial void OnPreviewPathChanged(string? value) => OnPropertyChanged(nameof(IsPreviewing));

    public void ClosePreview()
    {
        PreviewPath = null;
        PreviewName = "";
    }

    /// <summary>Hands the previewed attachment to its usual program.</summary>
    public void OpenPreviewExternally()
    {
        if (PreviewPath is not { } path) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Opened {PreviewName}";
        }
        catch (Exception ex)
        {
            Status = $"Could not open {PreviewName}: {ex.Message}";
        }
    }

    partial void OnSelectedChanged(MailRowViewModel? oldValue, MailRowViewModel? newValue)
    {
        // As in Outlook, a conversation folds back up once you move off it.
        if (oldValue is not null && oldValue != newValue) CollapseRow(oldValue);
        if (newValue is not null && Rows.IndexOf(newValue) is var at and >= 0) _caret = at;
        SetFocusedMessage(null);

        ClosePreview();
        _ = LoadBodyAsync(newValue);
        _ = LoadInviteAsync(newValue);
    }

    // ---- conversation view: expand a row, read its messages one by one ----

    /// <summary>How many messages an expanded row lists - Outlook's own scan limit.</summary>
    private const int ExpandedMessageLimit = 300;

    /// <summary>Changes the focused message without loading anything.</summary>
    private void SetFocusedMessage(ConversationMessageViewModel? message)
    {
        if (FocusedMessage == message) return;
        if (FocusedMessage is { } old) old.IsFocused = false;
        if (message is not null) message.IsFocused = true;

#pragma warning disable MVVMTK0034 // the field, so the change handler does not load the body a second time
        _focusedMessage = message;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(FocusedMessage));
    }

    public Task ToggleExpandAsync(MailRowViewModel row)
    {
        if (!row.IsExpanded) return ExpandAsync(row);

        if (Selected != row) Selected = row;
        else CollapseRow(row);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lists every message in the row's conversation under it - including the
    /// ones already archived or filed, which the Inbox view leaves out.
    /// </summary>
    public async Task ExpandAsync(MailRowViewModel? row = null)
    {
        row ??= Selected;
        if (row is null || row.IsExpanded) return;
        if (Selected != row) Selected = row;

        row.IsExpanded = true;
        FillMessages(row, row.Thread.Messages);

        row.IsLoadingMessages = true;
        try
        {
            var all = await _store.GetConversationAsync(row.Summary.Ref, ExpandedMessageLimit).ConfigureAwait(true);
            if (row.IsExpanded) FillMessages(row, row.Thread.Messages.Concat(all));
        }
        catch (Exception ex)
        {
            Status = $"Could not read the rest of the conversation: {ex.Message}";
        }
        finally
        {
            row.IsLoadingMessages = false;
        }
    }

    /// <summary>Newest first, one entry per message, keeping the entries already shown.</summary>
    private static void FillMessages(MailRowViewModel row, IEnumerable<MailSummary> messages)
    {
        var existing = new Dictionary<string, ConversationMessageViewModel>(StringComparer.Ordinal);
        foreach (var m in row.Messages) existing.TryAdd(m.Summary.Ref.EntryId, m);

        var ordered = messages
            .DistinctBy(m => string.IsNullOrEmpty(m.InternetMessageId) ? m.Ref.EntryId : m.InternetMessageId,
                        StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m.ReceivedUtc)
            .Select(m => existing.GetValueOrDefault(m.Ref.EntryId) ?? new ConversationMessageViewModel(row, m))
            .ToList();

        row.Messages.Clear();
        foreach (var m in ordered) row.Messages.Add(m);
    }

    private static void CollapseRow(MailRowViewModel row)
    {
        row.IsExpanded = false;
        row.Messages.Clear();
    }

    /// <summary>
    /// Left in the list: from a single message back to the whole
    /// conversation, and from there folds the row up.
    /// </summary>
    public void CollapseSelected()
    {
        if (Selected is not { } row) return;

        if (FocusedMessage is not null) FocusedMessage = null;
        else if (row.IsExpanded) CollapseRow(row);
    }

    /// <summary>Shows one message from the expanded row, as clicking it in Outlook does.</summary>
    public void FocusMessage(ConversationMessageViewModel message)
    {
        if (Selected != message.Row) Selected = message.Row;
        FocusedMessage = message;
    }

    partial void OnFocusedMessageChanged(ConversationMessageViewModel? oldValue, ConversationMessageViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;

        ClosePreview();
        _ = newValue is null ? LoadBodyAsync(Selected) : LoadMessageAsync(newValue);
    }

    /// <summary>Just the one message in the reading pane, with its own attachments.</summary>
    private async Task LoadMessageAsync(ConversationMessageViewModel message)
    {
        _bodyLoad?.Cancel();

        var cts = new CancellationTokenSource();
        _bodyLoad = cts;

        var entryId = message.Summary.Ref.EntryId;
        try
        {
            MailBody body;
            try { body = await _bodies.GetOrAdd(entryId, _ => _store.GetBodyAsync(message.Summary.Ref)).ConfigureAwait(true); }
            catch { _bodies.Remove(entryId); throw; }
            if (cts.IsCancellationRequested) return;

            var html = await RenderSingleAsync(body).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            OpenBody = body;
            ThreadAttachments = body.Attachments;
            BodyHtml = html;

            if (!message.IsUnread || _settings.MarkReadAfterMs <= 0) return;

            await Task.Delay(_settings.MarkReadAfterMs, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || FocusedMessage != message) return;

            await _store.SetReadAsync(message.Summary.Ref, true, cts.Token).ConfigureAwait(true);
            message.IsUnread = false;

            var row = message.Row;
            row.Refresh(row.Thread with
            {
                Messages = row.Thread.Messages
                    .Select(m => m.Ref.EntryId == entryId ? m with { IsUnread = false } : m)
                    .ToList(),
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Status = $"Could not open that message: {ex.Message}";
        }
    }

    private async Task LoadBodyAsync(MailRowViewModel? row)
    {
        // Cancel the previous load but do not dispose it: an in-flight
        // Task.Delay still holds the token, and disposing under it raises
        // ObjectDisposedException instead of the cancellation we want.
        _bodyLoad?.Cancel();
        _prefetch?.Cancel();

        if (row is null)
        {
            OpenBody = null;
            ThreadAttachments = Array.Empty<MailAttachment>();
            BodyHtml = "";
            return;
        }

        var cts = new CancellationTokenSource();
        _bodyLoad = cts;

        try
        {
            var load = LoadThreadAsync(row.Thread, cts.Token);

            // A conversation nothing has read yet - typically a search result
            // from far down the list, which prefetch never reached - costs one
            // Outlook read per message. Show the newest as soon as it is in
            // rather than leave the pane on the old mail until all are.
            if (!load.IsCompleted && row.Thread.Messages.Count > 1)
                await ShowNewestAsync(row.Thread, load, cts.Token).ConfigureAwait(true);

            var (bodies, html) = await load.ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            OpenBody = bodies[0];
            ThreadAttachments = bodies
                .SelectMany(b => b.Attachments)
                .DistinctBy(a => (a.Name.ToLowerInvariant(), a.Size))
                .ToList();
            BodyHtml = html;

            // With this one on screen, get the next few ready while the user reads.
            StartPrefetch(row);

            await MarkReadAfterDwellAsync(row, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Status = $"Could not open that message: {ex.Message}";
        }
    }

    /// <summary>
    /// The newest message on its own, as a stand-in while the rest of the
    /// conversation is still being read. Shares the body read the whole
    /// load already started, so it costs no extra trip to Outlook.
    /// </summary>
    private async Task ShowNewestAsync(ConversationThread thread, Task whole, CancellationToken ct)
    {
        var newest = thread.Messages[0];
        try
        {
            var body = await _bodies.GetOrAdd(newest.Ref.EntryId, _ => _store.GetBodyAsync(newest.Ref)).ConfigureAwait(true);
            if (ct.IsCancellationRequested || whole.IsCompleted) return;

            var html = await RenderSingleAsync(body).ConfigureAwait(true);
            if (ct.IsCancellationRequested || whole.IsCompleted) return;

            OpenBody = body;
            ThreadAttachments = body.Attachments;
            BodyHtml = html;
        }
        catch { /* the whole-conversation load reports any failure */ }
    }

    /// <summary>One message's page, rendered off the UI thread and cached.</summary>
    private async Task<string> RenderSingleAsync(MailBody body)
    {
        var blockRemote = _settings.BlockRemoteImages;
        var pageKey = "one:" + body.Ref.EntryId;
        try { return await _pages.GetOrAdd(pageKey, _ => Task.Run(() => HtmlPresenter.Render(body, blockRemote))).ConfigureAwait(true); }
        catch { _pages.Remove(pageKey); throw; }
    }

    /// <summary>
    /// Every message in the conversation (yours included, newest first) and
    /// the rendered page, from cache where possible. Bodies are fetched without
    /// a cancellation token: a fetch is shared through the cache, so one
    /// caller moving on must not cancel it for another. <paramref name="stop"/>
    /// is checked between reads instead, so a load the user has moved on from
    /// stops queuing work on Outlook's single thread ahead of the one they want.
    /// </summary>
    private async Task<(List<MailBody> Bodies, string Html)> LoadThreadAsync(
        ConversationThread thread, CancellationToken stop = default)
    {
        var messages = thread.Messages.Take(Math.Max(1, _settings.ThreadMessageLimit)).ToList();

        var bodies = new List<MailBody>(messages.Count);
        foreach (var message in messages)
        {
            stop.ThrowIfCancellationRequested();

            var task = _bodies.GetOrAdd(message.Ref.EntryId, _ => _store.GetBodyAsync(message.Ref));
            try { bodies.Add(await task.ConfigureAwait(true)); }
            catch (Exception) when (bodies.Count > 0 || message != messages[^1])
            {
                // A message that moved or vanished: forget the failure and
                // carry on with the rest of the thread.
                _bodies.Remove(message.Ref.EntryId);
            }
            catch
            {
                _bodies.Remove(message.Ref.EntryId);
                throw;
            }
        }

        if (bodies.Count == 0) throw new InvalidOperationException("None of this conversation's messages could be read.");

        // The page is keyed by exactly which bodies it holds, so a new reply
        // in the thread gives a new page rather than a stale one.
        var pageKey = string.Join('|', bodies.Select(b => b.Ref.EntryId)) + "#" + thread.Count;
        var hiddenOlder = thread.Count - bodies.Count;
        var blockRemote = _settings.BlockRemoteImages;

        // Rendering (colour adaptation of big HTML mail) runs off the UI thread.
        var page = _pages.GetOrAdd(pageKey, _ => Task.Run(() =>
            HtmlPresenter.RenderThread(bodies, blockRemote, hiddenOlder)));

        try { return (bodies, await page.ConfigureAwait(true)); }
        catch { _pages.Remove(pageKey); throw; }
    }

    /// <summary>
    /// The selected conversation as a page to print; null when nothing is selected.
    /// </summary>
    public async Task<string?> RenderSelectedForPrintAsync()
    {
        if (Selected is not { } row) return null;

        var (bodies, _) = await LoadThreadAsync(row.Thread).ConfigureAwait(true);
        var subject = ConversationGrouper.StripPrefixes(row.Subject);
        var hiddenOlder = row.Thread.Count - bodies.Count;
        var blockRemote = _settings.BlockRemoteImages;

        return await Task.Run(() => HtmlPresenter.RenderThreadForPrint(subject, bodies, blockRemote, hiddenOlder)).ConfigureAwait(true);
    }

    /// <summary>
    /// Loads the next few conversations below the selected one, and the one
    /// above, into the caches. One message at a time, so a move or reply the
    /// user makes meanwhile waits for at most a single body read.
    /// </summary>
    private void StartPrefetch(MailRowViewModel from)
    {
        _prefetch?.Cancel();
        if (_settings.PrefetchAhead <= 0) return;

        var cts = new CancellationTokenSource();
        _prefetch = cts;

        var index = Rows.IndexOf(from);
        if (index < 0) return;

        var targets = Rows.Skip(index + 1).Take(_settings.PrefetchAhead).ToList();
        if (index > 0) targets.Add(Rows[index - 1]);
        targets.RemoveAll(r => _leaving.ContainsKey(r.Key));

        _ = PrefetchAsync(targets.Select(r => r.Thread).ToList(), cts.Token);
    }

    private async Task PrefetchAsync(IReadOnlyList<ConversationThread> threads, CancellationToken ct)
    {
        foreach (var thread in threads)
        {
            if (ct.IsCancellationRequested) return;
            try { await LoadThreadAsync(thread, ct).ConfigureAwait(true); }
            catch { /* only a head start; the real open will report any problem */ }
        }
    }

    /// <summary>
    /// Marks read only after the message has stayed on screen briefly, so
    /// arrowing past a mail does not silently clear its unread state.
    /// </summary>
    private async Task MarkReadAfterDwellAsync(MailRowViewModel row, CancellationToken ct)
    {
        if (!row.IsUnread || _settings.MarkReadAfterMs <= 0) return;

        await Task.Delay(_settings.MarkReadAfterMs, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested || Selected != row) return;

        foreach (var m in row.InboxMessages.Where(m => m.IsUnread))
            await _store.SetReadAsync(m.Ref, true, ct).ConfigureAwait(true);
        row.Refresh(row.Thread.WithInboxUnread(false));
    }

    // ---- navigation -------------------------------------------------------

    public void Move(int delta)
    {
        ClearMarks();
        if (Rows.Count == 0) return;

        // Inside an expanded conversation, j/k step through its messages
        // first: down from the row enters them, up from the first leaves them.
        if (Selected is { IsExpanded: true } open && Math.Abs(delta) == 1 && open.Messages.Count > 0)
        {
            var at = FocusedMessage is null ? -1 : open.Messages.IndexOf(FocusedMessage);
            var next = at + delta;
            if (at >= 0 || delta > 0)
            {
                if (next < open.Messages.Count)
                {
                    FocusedMessage = next < 0 ? null : open.Messages[next];
                    return;
                }
            }
        }

        // A selection lost to a refresh resumes where the caret was, not at the top.
        if (Selected is null || !Rows.Contains(Selected))
        {
            Selected = RowAtCaret();
            return;
        }

        var index = Rows.IndexOf(Selected) + delta;
        Selected = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    public void MoveToEnd(bool last)
    {
        ClearMarks();
        Selected = last ? Rows.LastOrDefault() : Rows.FirstOrDefault();
    }

    /// <summary>
    /// Shift+arrow: moves the caret and marks every row between it and where
    /// the selection started, so the next triage action takes them all.
    /// </summary>
    public void ExtendSelection(int delta)
    {
        if (Rows.Count == 0) return;
        if (Selected is null || !Rows.Contains(Selected)) { Move(delta); return; }

        if (_anchor is null || !Rows.Contains(_anchor)) _anchor = Selected;

        var to = Math.Clamp(Rows.IndexOf(Selected) + delta, 0, Rows.Count - 1);
        var from = Rows.IndexOf(_anchor);
        var (lo, hi) = from <= to ? (from, to) : (to, from);

        for (var i = 0; i < Rows.Count; i++) Rows[i].IsMarked = i >= lo && i <= hi;
        Selected = Rows[to];

        var count = hi - lo + 1;
        Status = count == 1 ? "" : $"{count} selected · e, v, h, a or n acts on all · Esc clears";
    }

    public bool HasMarks => _allRows.Any(r => r.IsMarked);

    public void ClearMarks()
    {
        _anchor = null;
        foreach (var row in _allRows) row.IsMarked = false;
    }

    /// <summary>The marked rows in list order, or else just the selected one.</summary>
    private List<MailRowViewModel> Targets()
    {
        var marked = Rows.Where(r => r.IsMarked).ToList();
        if (marked.Count == 0 && Selected is { } row) marked.Add(row);
        return marked;
    }

    /// <summary>The row the caret should land on once <paramref name="rows"/> are dealt with.</summary>
    private MailRowViewModel? RowAfter(IReadOnlyCollection<MailRowViewModel> rows)
    {
        var gone = rows.ToHashSet();
        var last = rows.Select(r => Rows.IndexOf(r)).DefaultIfEmpty(-1).Max();
        if (last < 0) return Selected;

        return Rows.Skip(last + 1).FirstOrDefault(r => !gone.Contains(r))
            ?? Rows.Take(last).LastOrDefault(r => !gone.Contains(r));
    }

    // ---- triage actions ---------------------------------------------------

    /// <summary>
    /// `a`: opens the capture popup over the selection, or flags at once when
    /// the popup is switched off in settings. Returns true when the flag was
    /// applied here and now, so the board can reload.
    /// </summary>
    public async Task<bool> OpenCaptureAsync()
    {
        var rows = Targets();
        if (rows.Count == 0) return false;

        if (!_settings.AskDetailsOnFlag)
        {
            await ToggleActionRequiredAsync(true).ConfigureAwait(true);
            return true;
        }

        // Flagging again edits the card, so the popup starts from what it holds.
        ActionItem? existing = null;
        if (rows.Count == 1)
        {
            try
            {
                var mail = await FlaggedMailAsync(rows[0]).ConfigureAwait(true);
                existing = await _actions.GetByMessageIdAsync(mail.InternetMessageId).ConfigureAwait(true);
            }
            catch { /* the popup works without it */ }
        }

        IReadOnlyList<(string Name, string Email)> known;
        try { known = await _actions.GetKnownAssigneesAsync().ConfigureAwait(true); }
        catch { known = Array.Empty<(string, string)>(); }

        // People on the thread come first in Who, so a first name is enough.
        var onThread = rows.SelectMany(r => r.InboxMessages)
            .Select(m => new Recipient(m.SenderName, m.SenderAddress))
            .Concat(rows.Count == 1 && OpenBody is { } body ? body.To.Concat(body.Cc) : Enumerable.Empty<Recipient>())
            .Where(r => r.Display.Length > 0)
            .Distinct()
            .ToList();

        var single = rows.Count == 1 ? rows[0] : null;
        Capture.Open(
            contextLine: single is not null
                ? $"{single.Subject} · {single.Summary.SenderName} · {single.Summary.ReceivedUtc.ToLocalTime():ddd d MMM}"
                : $"{rows.Count} conversations · leave What empty to keep each subject",
            title: single is not null ? ActionCapture.CleanSubject(single.Subject) : "",
            multiple: rows.Count > 1,
            existing,
            onThread,
            known.Select(k => new Recipient(k.Name, k.Email)).ToList());
        return false;
    }

    public void CloseCapture() => Capture.Close();

    /// <summary>Shift+Enter in the popup: the plain flag, no questions asked.</summary>
    public async Task FlagWithoutDetailsAsync()
    {
        Capture.Close();
        await ToggleActionRequiredAsync(true).ConfigureAwait(true);
    }

    /// <summary>
    /// Enter in the popup. Files the answers on every targeted mail. Returns
    /// the hand-offs to tell by email when that was asked for; the popup stays
    /// open, and nothing is filed, when a date could not be read.
    /// </summary>
    public async Task<IReadOnlyList<(ActionItem Item, Assignment Handoff)>> CommitCaptureAsync()
    {
        if (Capture.Build() is not { } request) return Array.Empty<(ActionItem, Assignment)>();

        var tell = Capture.TellThem && request.HasWait && !request.IsBlocker;
        Capture.Close();

        var items = await FlagAsync(true, request).ConfigureAwait(true);
        if (!tell) return Array.Empty<(ActionItem, Assignment)>();

        var who = request.Who!.Value.Display;
        return items
            .Select(i => (Item: i, Handoff: i.Assignments.LastOrDefault(a => !a.IsDone && a.PersonName == who)))
            .Where(t => t.Handoff is not null)
            .Select(t => (t.Item, t.Handoff!))
            .ToList();
    }

    /// <summary>
    /// Flags or clears the selection and moves on at once. The returned task is
    /// the Outlook and database work, which finishes in the background.
    /// </summary>
    public Task ToggleActionRequiredAsync(bool required) => FlagAsync(required, null);

    /// <summary>
    /// The flag itself, with the popup's answers when there were any. Returns
    /// the cards as they now stand.
    /// </summary>
    private async Task<IReadOnlyList<ActionItem>> FlagAsync(bool required, CaptureRequest? capture)
    {
        var rows = Targets();
        if (rows.Count == 0) return Array.Empty<ActionItem>();

        // A date on the card takes the mail out of the Inbox until that day.
        var park = required && capture is not null && _settings.SnoozeUntilActionDate
            ? ActionCapture.ReturnTime(capture, _clock.UtcNow)
            : null;
        if (park is not null) _ = GetSnoozeFolderAsync();

        var before = rows.ToDictionary(r => r, r => r.IsActionRequired);
        foreach (var row in rows) row.IsActionRequired = required;

        if (park is not null) TakeOut(rows);
        else if (rows.Count == 1) Move(1);
        else
        {
            var next = RowAfter(rows);
            ClearMarks();
            if (next is not null) Selected = next;
        }

        var what = rows.Count == 1 ? rows[0].Subject : $"{rows.Count} conversations";
        Status = !required ? $"Marked as needing no action · {what}"
            : capture is null ? $"Flagged for action · {what}"
            : $"Flagged · {what}"
              + (capture.HasWait ? $" · waiting on {capture.Who!.Value.Display}" : "")
              + (ActionCapture.Details(capture, _clock.Now) is { Length: > 0 } details ? $" · {details}" : "")
              + (park is { } back ? $" · back in your inbox {back.ToLocalTime():ddd d MMM HH:mm}" : "");

        string? error = null;
        var results = new List<ActionItem>();
        var changed = new List<(MailRowViewModel Row, IReadOnlyList<ActionItem> Removed, ActionItem? Previous)>();
        var filed = new List<Filed>();
        foreach (var row in rows)
        {
            try
            {
                var mail = required ? await FlaggedMailAsync(row).ConfigureAwait(true) : row.Summary;
                var outcome = await SetActionRequiredAsync(mail, required, row, before[row], capture).ConfigureAwait(true);
                if (outcome.Result is not null) results.Add(outcome.Result);
                filed.Add(new Filed(row, mail, before[row], outcome));

                if (before[row] != required) changed.Add((row, outcome.Removed, null));
                // Already flagged: the popup edited the card, so undo puts it back as it was.
                else if (outcome.Previous is not null) changed.Add((row, Array.Empty<ActionItem>(), outcome.Previous));
            }
            catch (Exception ex)
            {
                row.IsActionRequired = before[row];
                error ??= ex.Message;
            }
        }

        if (park is { } when)
        {
            if (error is not null) Status = $"Could not update that message: {error}";
            _ = ParkFlagged(rows, when, filed);
            return results;
        }

        if (changed.Count > 0)
        {
            PushUndo(required ? "flag for action" : "no action", async () =>
            {
                foreach (var (row, removed, previous) in changed)
                {
                    if (previous is not null)
                    {
                        await _actions.RestoreAsync(previous).ConfigureAwait(true);
                        continue;
                    }

                    row.IsActionRequired = !required;
                    if (removed.Count > 0)
                    {
                        // Put the tasks back as they were, notes and priority included.
                        foreach (var card in removed)
                        {
                            await _actions.UpsertAsync(card).ConfigureAwait(true);
                            _ = SetCategoryInBackgroundAsync(row, MailOf(row, card).Ref, true, false);
                        }
                    }
                    else
                    {
                        await SetActionRequiredAsync(row.Summary, !required, row, required).ConfigureAwait(true);
                    }
                }
            });
        }

        if (error is not null) Status = $"Could not update that message: {error}";
        return results;
    }

    /// <summary>
    /// What flagging changed: the tasks that clearing the flag removed, the
    /// card as it stood before the popup edited it, and the card as it is now.
    /// </summary>
    /// <param name="Written">The Outlook category write, which runs on behind the keystroke.</param>
    private sealed record FlagOutcome(
        IReadOnlyList<ActionItem> Removed, ActionItem? Previous, ActionItem? Result, Task Written);

    /// <summary>One conversation flagged: the mail its card hangs on, and whether it was flagged before.</summary>
    private sealed record Filed(MailRowViewModel Row, MailSummary Mail, bool WasFlagged, FlagOutcome Outcome);

    /// <summary>
    /// Snoozes conversations just flagged with a date until
    /// <paramref name="when"/>. Waits for their category writes first, since
    /// moving a mail mid-write loses the write. One undo step puts back both
    /// the mail and the card; the rows were already taken out of the list.
    /// </summary>
    private Task ParkFlagged(IReadOnlyList<MailRowViewModel> rows, DateTimeOffset when, IReadOnlyList<Filed> filed)
    {
        var origin = _inbox;
        var parked = new List<Parked>();

        PushUndo("flag and snooze", async () =>
        {
            var back = new Dictionary<string, MailRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parked)
            {
                back[p.MessageId] = await _store.MoveAsync(p.Ref, origin).ConfigureAwait(true);
                await _snoozes.CancelAsync(p.SnoozeId).ConfigureAwait(true);
            }

            foreach (var f in filed)
            {
                if (!f.WasFlagged && f.Outcome.Result is { } card)
                {
                    await _actions.DeleteAsync(card.Id).ConfigureAwait(true);
                    var mail = back.TryGetValue(f.Mail.InternetMessageId, out var moved) ? moved : f.Mail.Ref;
                    await _store.SetCategoryAsync(mail, _settings.ActionCategory, false).ConfigureAwait(true);
                }
                else if (f.Outcome.Previous is { } previous)
                {
                    await _actions.RestoreAsync(previous).ConfigureAwait(true);
                }
            }

            await LoadAsync().ConfigureAwait(true);
        });

        return RunInBackground(async () =>
        {
            string? error;
            try
            {
                await Task.WhenAll(filed.Select(f => f.Outcome.Written)).ConfigureAwait(true);
                error = await ParkAsync(filed.Select(f => f.Row), when, origin, parked).ConfigureAwait(true);

                // The cards follow their mail, so the board opens it where it now is.
                var cards = filed.Select(f => f.Mail.InternetMessageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var p in parked.Where(p => cards.Contains(p.MessageId)))
                    await _actions.UpdateLocationAsync(p.MessageId, p.Ref.EntryId, p.Ref.StoreId).ConfigureAwait(true);
            }
            finally { Settle(rows); }

            // A conversation that could not be flagged or moved comes back into the list.
            if (error is not null || filed.Count < rows.Count)
            {
                if (error is not null) Status = $"Could not snooze that conversation: {error}";
                await RefreshQuietlyAsync().ConfigureAwait(true);
            }
        });
    }

    /// <summary>
    /// The mail in the conversation the row's card hangs on. A reply after
    /// flagging makes the newest mail a different one while the card stays on
    /// the mail that was flagged; with no open card it is the newest mail.
    /// </summary>
    private async Task<MailSummary> FlaggedMailAsync(MailRowViewModel row)
    {
        foreach (var mail in row.InboxMessages)
        {
            if (mail.InternetMessageId.Length == 0) continue;
            if (await _actions.GetByMessageIdAsync(mail.InternetMessageId).ConfigureAwait(true) is { IsComplete: false })
                return mail;
        }
        return row.Summary;
    }

    /// <summary>The mail in the row a card was filed on, or the newest one.</summary>
    private static MailSummary MailOf(MailRowViewModel row, ActionItem card) =>
        row.InboxMessages.FirstOrDefault(m =>
            string.Equals(m.InternetMessageId, card.InternetMessageId, StringComparison.OrdinalIgnoreCase))
        ?? row.Summary;

    private async Task<FlagOutcome> SetActionRequiredAsync(
        MailSummary summary, bool required, MailRowViewModel? row = null, bool wasActionRequired = false,
        CaptureRequest? capture = null)
    {
        var removed = new List<ActionItem>();
        ActionItem? previous = null;
        ActionItem? result = null;

        // The local record is awaited (it is what the action board reads
        // the moment this returns); the Outlook category write is not.
        if (required)
        {
            var item = new ActionItem
            {
                InternetMessageId = summary.InternetMessageId,
                EntryId = summary.Ref.EntryId,
                StoreId = summary.Ref.StoreId,
                Subject = summary.Subject,
                SenderName = summary.SenderName,
                SenderAddress = summary.SenderAddress,
                ReceivedUtc = summary.ReceivedUtc,
                CreatedUtc = _clock.UtcNow,
            };

            if (capture is null)
            {
                result = await _actions.UpsertAsync(item).ConfigureAwait(true);
            }
            else
            {
                previous = await _actions.GetByMessageIdAsync(summary.InternetMessageId).ConfigureAwait(true);
                result = await _actions.CaptureAsync(item, capture).ConfigureAwait(true);
            }
        }
        else
        {
            var existing = await _actions
                .GetByMessageIdAsync(summary.InternetMessageId).ConfigureAwait(true);

            if (existing is not null)
            {
                await _actions.DeleteAsync(existing.Id).ConfigureAwait(true);
                removed.Add(existing);
            }

            // The row is flagged by an open card on any of its mails, not only
            // the newest, so clearing it has to clear those too.
            foreach (var mail in row?.InboxMessages ?? Array.Empty<MailSummary>())
            {
                if (mail.InternetMessageId.Length == 0
                    || string.Equals(mail.InternetMessageId, summary.InternetMessageId, StringComparison.OrdinalIgnoreCase)) continue;
                if (await _actions.GetByMessageIdAsync(mail.InternetMessageId).ConfigureAwait(true) is not { IsComplete: false } card)
                    continue;

                await _actions.DeleteAsync(card.Id).ConfigureAwait(true);
                removed.Add(card);
                _ = SetCategoryInBackgroundAsync(row, mail.Ref, false, wasActionRequired);
            }
        }

        var written = SetCategoryInBackgroundAsync(row, summary.Ref, required, wasActionRequired);
        return new FlagOutcome(removed, previous, result, written);
    }

    /// <summary>
    /// The Outlook category makes the flag visible inside Outlook itself, so
    /// the state is not trapped in this app. Written behind the keystroke; a
    /// failure (the message moved or vanished) rolls the flag back and says so.
    /// </summary>
    private async Task SetCategoryInBackgroundAsync(MailRowViewModel? row, MailRef mail, bool on, bool wasActionRequired)
    {
        try
        {
            await _store.SetCategoryAsync(mail, _settings.ActionCategory, on).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (row is not null) row.IsActionRequired = wasActionRequired;
            Status = $"Could not update that message: {ex.Message}";
        }
    }

    public async Task ToggleReadAsync()
    {
        if (Selected is not { } row) return;

        try
        {
            var previous = row.Thread;
            var read = row.IsUnread;
            var changed = new List<MailRef>();

            if (read)
            {
                // Reading the conversation clears all of it...
                foreach (var m in row.InboxMessages.Where(m => m.IsUnread))
                {
                    await _store.SetReadAsync(m.Ref, true).ConfigureAwait(true);
                    changed.Add(m.Ref);
                }
                row.Refresh(row.Thread.WithInboxUnread(false));
            }
            else
            {
                // ...but "come back to this" only needs the newest message bold.
                await _store.SetReadAsync(row.Summary.Ref, false).ConfigureAwait(true);
                changed.Add(row.Summary.Ref);
                row.Refresh(row.Thread.WithLatestInboxUnread());
            }

            PushUndo(read ? "mark read" : "mark unread", async () =>
            {
                foreach (var m in changed) await _store.SetReadAsync(m, !read).ConfigureAwait(true);
                row.Refresh(previous);
            });
        }
        catch (Exception ex)
        {
            Status = $"Could not change the read state: {ex.Message}";
        }
    }

    // ---- move palette (v) -------------------------------------------------

    public async Task OpenFolderPaletteAsync()
    {
        if (Selected is null) return;

        Palette.Open(
            PaletteMode.Folder,
            "Move to folder",
            "Enter move · Ctrl+Enter create and move · Esc cancel",
            TargetsLabel());

        if (!_folders.IsIndexed)
        {
            Palette.SetEntries(new[]
            {
                new PaletteEntry("Reading folder list...", "", new object(), Array.Empty<int>())
            });
        }

        await _folders.EnsureIndexedAsync().ConfigureAwait(true);
        RefreshPalette();
    }

    /// <summary>
    /// Shift+V: pick any folder and show it in Outlook's own window. Needs no
    /// message selected, so it works from an empty inbox too.
    /// </summary>
    public async Task OpenFolderInOutlookPaletteAsync()
    {
        Palette.Open(
            PaletteMode.OpenFolder,
            "Open folder in Outlook",
            "Enter open in Outlook · Esc cancel",
            "Type part of a folder's name or path");

        if (!_folders.IsIndexed)
        {
            Palette.SetEntries(new[]
            {
                new PaletteEntry("Reading folder list...", "", new object(), Array.Empty<int>())
            });
        }

        await _folders.EnsureIndexedAsync().ConfigureAwait(true);
        RefreshPalette();
    }

    private async Task ConfirmOpenFolderAsync()
    {
        var picked = Palette.Selected?.Payload as FolderNode;
        Palette.Close();
        if (picked is null) return;

        try
        {
            await _store.ShowFolderAsync(picked.Ref).ConfigureAwait(true);
            Status = $"Opened {picked.Name} in Outlook";
        }
        catch (Exception ex)
        {
            Status = $"Could not open {picked.Name} in Outlook: {ex.Message}";
        }
    }

    /// <summary>What a palette is about to act on: one subject, or a count.</summary>
    private string TargetsLabel()
    {
        var rows = Targets();
        return rows.Count == 1 ? rows[0].Subject : $"{rows.Count} conversations";
    }

    private void RefreshPalette()
    {
        if (!Palette.IsOpen) return;

        switch (Palette.Mode)
        {
            case PaletteMode.Folder: RefreshFolderPalette(); break;
            case PaletteMode.OpenFolder: RefreshFolderPalette(); break;
            case PaletteMode.Attachment: RefreshAttachmentPalette(); break;
            case PaletteMode.Rsvp: RefreshRsvpPalette(); break;
            case PaletteMode.Schedule: RefreshSchedulePalette(); break;
            default: RefreshSnoozePalette(); break;
        }
    }

    private void RefreshFolderPalette()
    {
        var typed = Palette.Query.Trim();
        var within = TypedParent(typed);
        List<FolderMatch> matches;

        if (within is { } w)
        {
            // "Elara - Procurement - " lists what is already filed under
            // Elara\Procurement, narrowing as the next part is typed.
            matches = _folders.SearchWithin(w.Parent, w.Partial).ToList();
        }
        else
        {
            matches = _folders.Search(Palette.Query).ToList();

            // A name typed the way the user names folders ("Elara - Field
            // Reports - Rimkus") finds the nested folder it stands for.
            if (NestedLevels(typed) is { } levels && _folders.FindByLevels(levels) is { } nested)
            {
                matches.RemoveAll(m => m.Folder.Ref.EntryId == nested.Ref.EntryId);
                matches.Insert(0, new FolderMatch(nested, 0, Array.Empty<int>()));
            }
        }

        // The breadcrumb leaves out the mailbox, so name it only when the
        // results span more than one.
        var manyStores = matches.Select(m => m.Folder.Ref.StoreId).Distinct().Count() > 1;

        Palette.SetEntries(matches.Select(m => new PaletteEntry(
            // A subfolder listed under its parent needs only its own name.
            m.Indent > 0 ? m.Folder.Name : m.Folder.Breadcrumb,
            manyStores && m.Indent == 0 ? m.Folder.StoreName : "",
            m.Folder,
            m.NameHighlights,
            m.Indent)));

        // When nothing matches, offer to create what was typed rather than
        // making the user leave and go build the folder in Outlook. A new
        // next part is offered beside the folders already there.
        var newPart = within is { Partial.Length: > 0 } p
            && !matches.Any(m => m.Folder.Path.Equals($"{p.Parent.Path}\\{p.Partial}", StringComparison.OrdinalIgnoreCase));

        Palette.CreatePrompt = Palette.Mode != PaletteMode.Folder || typed.Length == 0 ? null
            : matches.Count == 0 ? BuildCreatePrompt(typed)
            : newPart ? $"Ctrl+Enter: {BuildCreatePrompt(typed)}"
            : null;
    }

    /// <summary>
    /// The existing folder a scheme name is typed up to, and the start of the
    /// part after it: "Elara - Procurement - Ri" gives Elara\Procurement and
    /// "Ri". Null when the text is not in the scheme or that folder is not there.
    /// </summary>
    private (FolderNode Parent, string Partial)? TypedParent(string typed)
    {
        if (!_settings.NestNewFolders || typed.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
        if (_settings.GetFolderScheme().ToTypingLevels(typed) is not { } t) return null;

        // Under the scheme's home folder first, then wherever that chain sits.
        var home = FolderOrganizer.NormalisePath(_settings.FolderHome)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var parent = _folders.FindByLevels(home.Concat(t.Parent).ToList()) ?? _folders.FindByLevels(t.Parent);

        return parent is null ? null : (parent, t.Partial);
    }

    /// <summary>
    /// Where a typed name nests under the folder scheme on the Settings page,
    /// from the top of the mailbox; null when it is not in the scheme or
    /// nesting new folders is off.
    /// </summary>
    private IReadOnlyList<string>? NestedLevels(string typed)
    {
        if (!_settings.NestNewFolders || typed.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
        var scheme = _settings.GetFolderScheme();
        return FolderOrganizer.NewFolderLevels(scheme.WithoutTrailingSeparator(typed), scheme, _settings.FolderHome);
    }

    /// <summary>
    /// What creating the typed name makes: the folders to add, outermost
    /// first, and the existing folder they go under (null for the top of the
    /// mailbox).
    /// </summary>
    private (FolderNode? Under, IReadOnlyList<string> Levels) CreationTarget(string typed)
    {
        if (TypedParent(typed) is { Partial.Length: > 0 } w) return (w.Parent, new[] { w.Partial });
        if (NestedLevels(typed) is { } levels) return (null, levels);

        var (parent, name) = _folders.ResolveCreationTarget(_settings.GetFolderScheme().WithoutTrailingSeparator(typed));
        return (parent, new[] { name });
    }

    private string BuildCreatePrompt(string typed)
    {
        var (under, levels) = CreationTarget(typed);
        if (under is null && levels.Count > 1)
            return $"Create {string.Join(" › ", levels)} and move here";

        var name = string.Join(" › ", levels);
        return under is null
            ? $"Create \"{name}\" at the top level and move here"
            : $"Create \"{name}\" under {under.Path} and move here";
    }

    public async Task ConfirmPaletteAsync(bool forceCreate)
    {
        if (!Palette.IsOpen) return;

        switch (Palette.Mode)
        {
            case PaletteMode.Folder: await ConfirmFolderAsync(forceCreate).ConfigureAwait(true); break;
            case PaletteMode.OpenFolder: await ConfirmOpenFolderAsync().ConfigureAwait(true); break;
            case PaletteMode.Attachment:
                var pick = Palette.Selected?.Payload as MailAttachment;
                Palette.Close();
                if (pick is not null) await ShowAttachmentAsync(pick).ConfigureAwait(true);
                break;
            case PaletteMode.Rsvp: await ConfirmRsvpAsync().ConfigureAwait(true); break;

            // Ctrl+Enter - "create" in the folder palette - invites people instead of blocking time.
            case PaletteMode.Schedule: await ConfirmScheduleAsync(invite: forceCreate).ConfigureAwait(true); break;
            default: await ConfirmSnoozeAsync().ConfigureAwait(true); break;
        }
    }

    // ---- attachments (v, or a click in the header) ------------------------

    public Task OpenAttachmentPaletteAsync()
    {
        if (OpenBody is not { } body) return Task.CompletedTask;

        if (ThreadAttachments.Count == 0)
        {
            Status = "No attachments in this conversation";
            return Task.CompletedTask;
        }

        // One attachment: skip the picker and just show it.
        if (ThreadAttachments.Count == 1) return ShowAttachmentAsync(ThreadAttachments[0]);

        Palette.Open(
            PaletteMode.Attachment,
            "Open attachment",
            "Enter opens · type to filter · Esc cancels",
            body.Subject);
        RefreshPalette();
        return Task.CompletedTask;
    }

    private void RefreshAttachmentPalette()
    {
        var query = Palette.Query.Trim();
        var attachments = ThreadAttachments;

        Palette.SetEntries(attachments
            .Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(a => new PaletteEntry(a.Name, a.SizeDisplay, a, Array.Empty<int>())));
    }

    /// <summary>Saves the attachment locally and opens it in its usual program.</summary>
    /// <summary>
    /// Shows the attachment in the reading pane when it can (PDFs, images,
    /// text), and otherwise opens it in its usual program.
    /// </summary>
    public async Task ShowAttachmentAsync(MailAttachment attachment)
    {
        if (!attachment.CanPreview || attachment.IsBlockedType)
        {
            await OpenAttachmentAsync(attachment).ConfigureAwait(true);
            return;
        }

        if (OpenBody is not { } body) return;
        var source = attachment.Source.IsEmpty ? body.Ref : attachment.Source;

        try
        {
            PreviewPath = await _store.SaveAttachmentAsync(source, attachment.Index).ConfigureAwait(true);
            PreviewName = attachment.Name;
            Status = $"Previewing {attachment.Name} · Esc goes back to the email";
        }
        catch (Exception ex)
        {
            Status = $"Could not preview {attachment.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Saves the attachment to the local cache and returns its path, for
    /// dragging out to a folder. Null (with a status) for blocked types.
    /// </summary>
    public async Task<string?> SaveAttachmentForDragAsync(MailAttachment attachment)
    {
        if (OpenBody is not { } body) return null;

        if (attachment.IsBlockedType)
        {
            Status = $"{attachment.Name} is a program or script - save it from Outlook if you trust it";
            return null;
        }

        try
        {
            var source = attachment.Source.IsEmpty ? body.Ref : attachment.Source;
            return await _store.SaveAttachmentAsync(source, attachment.Index).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not get {attachment.Name}: {ex.Message}";
            return null;
        }
    }

    public async Task OpenAttachmentAsync(MailAttachment attachment)
    {
        if (OpenBody is not { } body) return;
        var source = attachment.Source.IsEmpty ? body.Ref : attachment.Source;

        if (attachment.IsBlockedType)
        {
            Status = $"{attachment.Name} is a program or script - open it from Outlook if you trust it (o)";
            return;
        }

        try
        {
            Status = $"Opening {attachment.Name}...";
            var path = await _store.SaveAttachmentAsync(source, attachment.Index).ConfigureAwait(true);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Opened {attachment.Name}";
        }
        catch (Exception ex)
        {
            Status = $"Could not open {attachment.Name}: {ex.Message}";
        }
    }

    private async Task ConfirmFolderAsync(bool forceCreate)
    {
        var typed = Palette.Query.Trim();
        var shouldCreate = forceCreate || (Palette.Selected is null && typed.Length > 0);

        try
        {
            FolderNode target;

            if (shouldCreate)
            {
                if (typed.Length == 0) return;

                var (under, levels) = CreationTarget(typed);
                if (levels.Count == 0 || levels[0].Length == 0) return;
                target = await CreateNestedAsync(under, levels).ConfigureAwait(true);

                Status = $"Created {target.Path}";
            }
            else if (Palette.Selected?.Payload is FolderNode picked)
            {
                target = picked;
            }
            else
            {
                return;
            }

            Palette.Close();
            await MoveSelectedAsync(target).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Palette.Close();
            Status = $"Move failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Makes each level from <paramref name="under"/> (the top of the mailbox
    /// when null) down, reusing any that already exist, and returns the innermost.
    /// </summary>
    private async Task<FolderNode> CreateNestedAsync(FolderNode? under, IReadOnlyList<string> levels)
    {
        var node = under;
        foreach (var level in levels)
        {
            node = await _store.CreateFolderAsync(node?.Ref ?? default, level).ConfigureAwait(true);
            _folders.AddToIndex(node);
        }
        return node!;
    }

    private Task MoveSelectedAsync(FolderNode target) =>
        MoveInBackground(target.Name, () => Task.FromResult(target));

    /// <summary>
    /// Takes the selected conversations out of the list at once and moves them
    /// in Outlook afterwards. <paramref name="resolve"/> finds the folder as
    /// part of that background work, so even a first Archive does not wait.
    /// A failure puts the conversations back and says so.
    /// </summary>
    private Task MoveInBackground(string targetName, Func<Task<FolderNode>> resolve)
    {
        var rows = Targets();
        if (rows.Count == 0) return Task.CompletedTask;

        var origin = _inbox;
        var moved = new List<MailRef>();
        TakeOut(rows);
        Status = rows.Count == 1 ? $"Moved to {targetName}" : $"Moved {rows.Count} conversations to {targetName}";

        // Recorded now, not when Outlook finishes, so anything done in the
        // meantime cannot slip underneath it in the undo order.
        PushUndo($"move to {targetName}", async () =>
        {
            foreach (var m in moved) await _store.MoveAsync(m, origin).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }, () => moved.Count == 0);

        return RunInBackground(async () =>
        {
            string? error = null;
            FolderNode? target = null;

            try
            {
                target = await resolve().ConfigureAwait(true);

                // The whole conversation goes, so the thread leaves the list in
                // one keystroke. Your sent copies stay in Sent Items.
                foreach (var m in rows.SelectMany(r => r.InboxMessages))
                {
                    try
                    {
                        var to = await _store.MoveAsync(m.Ref, target.Ref).ConfigureAwait(true);
                        moved.Add(to);
                        await _actions.UpdateLocationAsync(m.InternetMessageId, to.EntryId, to.StoreId).ConfigureAwait(true);
                    }
                    catch (Exception ex) { error ??= ex.Message; }
                }

                if (moved.Count > 0) await _folders.RecordUseAsync(target).ConfigureAwait(true);
            }
            catch (Exception ex) { error ??= ex.Message; }
            finally { Settle(rows); }

            if (error is not null)
            {
                var total = rows.Sum(r => r.InboxMessages.Count);
                Status = moved.Count == 0
                    ? $"Move failed: {error}"
                    : $"Moved {moved.Count} of {total} messages, then failed: {error}";

                // Whatever is still in the Inbox comes back into the list.
                await RefreshQuietlyAsync().ConfigureAwait(true);
            }
        });
    }

    // ---- snooze palette (h) ----------------------------------------------

    public void OpenSnoozePalette()
    {
        if (Selected is null) return;

        _snoozePresets = SnoozePresets.For(_clock.Now, _settings.DayShape);

        // Find (or make) the holding folder while the user is still picking a
        // time, so confirming costs no folder walk.
        _ = GetSnoozeFolderAsync();

        Palette.Open(
            PaletteMode.Snooze,
            "Come back to this",
            "Enter confirm · type e.g. \"tomorrow 9am\", \"fri\", \"3d\" · Esc cancel",
            TargetsLabel());

        RefreshSnoozePalette();
    }

    private void RefreshSnoozePalette()
    {
        var query = Palette.Query.Trim();
        var entries = new List<PaletteEntry>();

        // A typed expression that parses always wins the top slot.
        if (query.Length > 0 &&
            NaturalDateParser.TryParse(query, _clock.Now, out var parsed, _settings.DayShape))
        {
            entries.Add(new PaletteEntry(
                parsed.ToString("dddd d MMM, HH:mm"),
                Humanise(parsed - _clock.Now),
                parsed,
                Array.Empty<int>()));
        }

        foreach (var preset in _snoozePresets)
        {
            if (query.Length > 0 && FuzzyMatcher.Score(query, preset.Label) is null) continue;
            entries.Add(new PaletteEntry(preset.Label, preset.Hint, preset.When, Array.Empty<int>()));
        }

        Palette.SetEntries(entries);
        Palette.CreatePrompt = entries.Count == 0 && query.Length > 0
            ? $"\"{query}\" is not a time I understand - try \"tomorrow 9am\", \"fri\" or \"3d\""
            : null;
    }

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 60) return $"in {(int)span.TotalMinutes} minutes";
        if (span.TotalHours < 24) return $"in {(int)span.TotalHours} hours";
        return $"in {(int)span.TotalDays} days";
    }

    private Task ConfirmSnoozeAsync()
    {
        if (Palette.Selected?.Payload is not DateTimeOffset when) return Task.CompletedTask;

        var rows = Targets();
        Palette.Close();
        if (rows.Count == 0) return Task.CompletedTask;

        Status = (rows.Count == 1 ? "" : $"{rows.Count} conversations · ")
               + $"Back in your inbox {Humanise(when - _clock.Now)} · {when:ddd d MMM HH:mm}";
        return SnoozeRows(rows, when);
    }

    /// <summary>
    /// Takes conversations out of the list and parks them until
    /// <paramref name="when"/>, with an undo step that brings them back.
    /// </summary>
    private Task SnoozeRows(IReadOnlyList<MailRowViewModel> rows, DateTimeOffset when)
    {
        var origin = _inbox;
        var parked = new List<Parked>();
        TakeOut(rows);

        // Recorded now, like a move, so it keeps its place in the undo order.
        PushUndo("snooze", async () =>
        {
            foreach (var p in parked)
            {
                await _store.MoveAsync(p.Ref, origin).ConfigureAwait(true);
                await _snoozes.CancelAsync(p.SnoozeId).ConfigureAwait(true);
            }
            await LoadAsync().ConfigureAwait(true);
        }, () => parked.Count == 0);

        return RunInBackground(async () =>
        {
            string? error;
            try { error = await ParkAsync(rows, when, origin, parked).ConfigureAwait(true); }
            finally { Settle(rows); }

            if (error is not null)
            {
                Status = $"Could not snooze that conversation: {error}";
                await RefreshQuietlyAsync().ConfigureAwait(true);
            }
        });
    }

    /// <summary>A mail in the holding folder: where it is now, its snooze entry, and its Message-ID.</summary>
    private sealed record Parked(MailRef Ref, long SnoozeId, string MessageId);

    /// <summary>
    /// Moves every Inbox message of the conversations to the holding folder,
    /// each with its own snooze entry, so they all come back together.
    /// Fills <paramref name="parked"/> as it goes, for undo; returns the first
    /// error, or null.
    /// </summary>
    private async Task<string?> ParkAsync(
        IEnumerable<MailRowViewModel> rows, DateTimeOffset when, FolderRef origin, List<Parked> parked)
    {
        string? error = null;

        try
        {
            var holding = await GetSnoozeFolderAsync().ConfigureAwait(true);

            foreach (var m in rows.SelectMany(r => r.InboxMessages))
            {
                try
                {
                    var to = await _store.MoveAsync(m.Ref, holding).ConfigureAwait(true);

                    var entry = await _snoozes.AddAsync(new SnoozeEntry
                    {
                        InternetMessageId = m.InternetMessageId,
                        EntryId = to.EntryId,
                        StoreId = to.StoreId,
                        Subject = m.Subject,
                        SenderName = m.DisplaySender,
                        OriginFolderEntryId = origin.EntryId,
                        OriginFolderStoreId = origin.StoreId,
                        OriginFolderPath = origin.Path,
                        SnoozedUtc = _clock.UtcNow,
                        ReturnUtc = when.ToUniversalTime(),
                    }).ConfigureAwait(true);

                    parked.Add(new Parked(to, entry.Id, m.InternetMessageId));
                }
                catch (Exception ex) { error ??= ex.Message; }
            }
        }
        catch (Exception ex) { error ??= ex.Message; }

        return error;
    }

    // ---- replies and forwards (r / Shift+R / f) ---------------------------

    /// <summary>
    /// Opens the composer on any mail - how the action board replies to a
    /// task's conversation. Returns a problem to report, or null once open.
    /// </summary>
    public async Task<string?> StartReplyToAsync(MailRef mail, ReplyScope scope)
    {
        try
        {
            var draft = await _store.BuildReplyAsync(mail, scope).ConfigureAwait(true);
            Composer.Open(draft);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not start {(scope == ReplyScope.Forward ? "a forward" : "a reply")}: {ex.Message}";
        }
    }

    /// <summary>
    /// Files a follow-up set in the composer. A reply or forward puts the
    /// answered mail on the action list (as if flagged with `a`, unless it is
    /// there already); a new message answers nothing, so it is filed under a
    /// placeholder until its copy reaches Sent Items. Either way the person
    /// gets a dated hand-off and the card waits on them; on the day it moves
    /// to the board's Follow up column. With <paramref name="park"/>, the
    /// answered conversation also leaves the Inbox until that day. Returns a
    /// line for the status bar.
    /// </summary>
    public async Task<string> RecordFollowUpAsync(MailRef answered, FollowUpRequest followUp, bool park = false)
    {
        try
        {
            ActionItem? item;
            var written = Task.CompletedTask;
            if (answered.IsEmpty)
            {
                item = await _actions.UpsertAsync(new ActionItem
                {
                    InternetMessageId = SentMailMatcher.NewPlaceholder(),
                    Subject = followUp.Subject,
                    SenderName = "You",
                    SenderAddress = "",
                    ReceivedUtc = followUp.ToldUtc,
                }).ConfigureAwait(true);
            }
            else
            {
                var summary = _allRows.SelectMany(r => r.InboxMessages).FirstOrDefault(m => m.Ref == answered)
                    ?? (await _store.GetConversationAsync(answered, 50).ConfigureAwait(true))
                        .FirstOrDefault(m => m.Ref == answered);

                if (summary is null) return "the follow-up could not be saved: that mail was not found";

                item = await _actions.GetByMessageIdAsync(summary.InternetMessageId).ConfigureAwait(true);
                if (item is null || item.IsComplete)
                {
                    written = (await SetActionRequiredAsync(summary, true).ConfigureAwait(true)).Written;
                    item = await _actions.GetByMessageIdAsync(summary.InternetMessageId).ConfigureAwait(true);
                    if (item is null) return "the follow-up could not be saved";
                }
            }

            var assignment = await _actions.AddAssignmentAsync(new Assignment
            {
                ActionItemId = item.Id,
                PersonName = followUp.Person.Display,
                PersonEmail = followUp.Person.Address,
                Task = followUp.Task,
                DueUtc = followUp.DueUtc,
                CreatedUtc = _clock.UtcNow,
                InThread = followUp.InThread,
            }).ConfigureAwait(true);

            // They were told in the message itself, so the wait starts now and
            // the board does not offer to "tell" them all over again.
            await _actions.MarkAssignmentDraftedAsync(assignment.Id).ConfigureAwait(true);

            if (ActionWorkflow.AfterWaitAdded(item) is { } stage)
                await _actions.UpdateStageAsync(item.Id, stage).ConfigureAwait(true);

            var when = followUp.DueUtc.ToLocalTime();
            if (park && _settings.SnoozeUntilActionDate && followUp.DueUtc > _clock.UtcNow
                && Rows.FirstOrDefault(r => r.InboxMessages.Any(m => m.Ref == answered)) is { } row)
            {
                // Moving the mail mid-write would lose its new category.
                await written.ConfigureAwait(true);
                _ = SnoozeRows(new[] { row }, followUp.DueUtc);
                return $"follow up with {followUp.Person.Display} on {when:ddd d MMM} - out of the inbox until {when:ddd d MMM HH:mm}";
            }

            return $"follow up with {followUp.Person.Display} on {when:ddd d MMM} - it joins the board's Follow up column that day";
        }
        catch (Exception ex)
        {
            return $"the follow-up could not be saved: {ex.Message}";
        }
    }

    /// <summary>Opens the composer on a blank message. Returns a problem to report, or null once open.</summary>
    public async Task<string?> StartNewMailAsync()
    {
        try
        {
            var draft = await _store.BuildNewMailAsync().ConfigureAwait(true);
            Composer.Open(draft);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not start a new message: {ex.Message}";
        }
    }

    public async Task StartReplyAsync(ReplyScope scope)
    {
        if (Selected is not { } row) return;

        try
        {
            Status = scope == ReplyScope.Forward ? "Preparing forward..." : "Preparing reply...";
            // A message picked in the expanded conversation is the one answered, as in Outlook.
            var target = FocusedMessage?.Summary.Ref ?? row.Summary.Ref;
            var draft = await _store.BuildReplyAsync(target, scope).ConfigureAwait(true);
            Composer.Open(draft);
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"Could not start a reply: {ex.Message}";
        }
    }

    // ---- archive, delete, undo -------------------------------------------

    public Task ArchiveAsync()
    {
        if (Selected is null) return Task.CompletedTask;

        // Always the Archive beside the Inbox, created if missing. Searching
        // the folder index by name could land on another mailbox's Archive,
        // or find nothing and leave the message sitting in the list.
        return MoveInBackground("Archive", async () =>
        {
            var archive = await GetArchiveFolderAsync().ConfigureAwait(true);
            return new FolderNode
            {
                Ref = archive,
                Name = "Archive",
                Path = archive.Path,
                Depth = 0,
                StoreName = "",
            };
        });
    }

    /// <summary>
    /// Archives the conversation holding <paramref name="mail"/>, for send &amp;
    /// mark done. Found by message rather than trusting the selection, which a
    /// live refresh may have moved since the reply was opened.
    /// </summary>
    /// <summary>The Message-IDs of the conversation holding <paramref name="mail"/>, for its board card.</summary>
    public IReadOnlyList<string> ConversationMessageIds(MailRef mail) =>
        Rows.FirstOrDefault(r => r.InboxMessages.Any(m => m.Ref.EntryId == mail.EntryId))
            ?.InboxMessages.Select(m => m.InternetMessageId).ToList()
        ?? new List<string>();

    public async Task ArchiveConversationOfAsync(MailRef mail)
    {
        var row = Rows.FirstOrDefault(r => r.InboxMessages.Any(m => m.Ref.EntryId == mail.EntryId));
        if (row is null) return;

        ClearMarks();
        Selected = row;
        await ArchiveAsync().ConfigureAwait(true);
    }

    public async Task UndoAsync()
    {
        // A move still running has not filled in what its undo step reverts yet.
        if (!_writes.IsCompleted)
        {
            Status = "Finishing up...";
            try { await _writes.ConfigureAwait(true); } catch { }
        }

        // A move or snooze that failed outright moved nothing: skip past it.
        while (_undo.Count > 0 && _undo[^1].IsEmpty?.Invoke() == true)
            _undo.RemoveAt(_undo.Count - 1);

        if (_undo.Count == 0)
        {
            OnPropertyChanged(nameof(CanUndo));
            Status = "Nothing to undo";
            return;
        }

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        OnPropertyChanged(nameof(CanUndo));

        try
        {
            await step.Revert().ConfigureAwait(true);
            Status = $"Undid {step.Description}";
        }
        catch (Exception ex)
        {
            Status = $"Could not undo {step.Description}: {ex.Message}";
        }
    }

    private void PushUndo(string description, Func<Task> revert, Func<bool>? isEmpty = null)
    {
        _undo.Add(new UndoStep(description, revert, isEmpty));

        // Ten steps back is plenty; past that the underlying items have shifted
        // around too much. The oldest is forgotten, never the one just made.
        while (_undo.Count > 10) _undo.RemoveAt(0);

        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>
    /// Takes conversations out of the list for a move or snooze, landing the
    /// caret on the next one so triage keeps flowing without a keystroke.
    /// </summary>
    private void TakeOut(IReadOnlyCollection<MailRowViewModel> rows)
    {
        foreach (var row in rows) _leaving[row.Key] = int.MaxValue;

        var next = RowAfter(rows);
        ClearMarks();

        // Select first: taking the selected row out of the list would
        // otherwise clear the reading pane before the next one is shown.
        Selected = next;

        foreach (var row in rows)
        {
            _allRows.Remove(row);
            _searchRows.Remove(row);
            Rows.Remove(row);
        }

        if (next is not null) _caret = Rows.IndexOf(next);
    }

    public void CancelOverlays()
    {
        if (Composer.IsOpen) { _ = Composer.DiscardAsync(); return; }
        if (Capture.IsOpen) { Capture.Close(); return; }
        if (Palette.IsOpen) { Palette.Close(); return; }
        if (IsPreviewing) { ClosePreview(); Status = ""; return; }
        if (HasMarks) { ClearMarks(); Status = ""; return; }
        if (IsSearching || HasAiFilter) { CloseSearch(); Status = ""; }
    }
}
