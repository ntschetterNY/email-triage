using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>Remembers one reversible triage action.</summary>
internal sealed record UndoStep(string Description, Func<Task> Revert);

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

    private FolderRef _inbox;
    private FolderRef? _sent;
    private readonly Stack<UndoStep> _undo = new();
    private CancellationTokenSource? _bodyLoad;
    private IReadOnlyList<SnoozeOption> _snoozePresets = Array.Empty<SnoozeOption>();

    public ObservableCollection<MailRowViewModel> Rows { get; } = new();
    public PaletteViewModel Palette { get; } = new();
    public ComposerViewModel Composer { get; }

    [ObservableProperty] private MailRowViewModel? _selected;
    [ObservableProperty] private MailBody? _openBody;

    /// <summary>Attachments from every message in the open conversation, newest first.</summary>
    [ObservableProperty] private IReadOnlyList<MailAttachment> _threadAttachments = Array.Empty<MailAttachment>();
    [ObservableProperty] private string _bodyHtml = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;

    private List<MailRowViewModel> _allRows = new();

    // Reloads are serialised: one that arrives mid-load is folded into a
    // single follow-up pass rather than racing the one in flight.
    private bool _loadRunning;
    private bool _reloadPending;
    private bool _pendingQuiet = true;

    public TriageViewModel(
        IMailStore store,
        IActionItemRepository actions,
        ISnoozeRepository snoozes,
        FolderSearchService folders,
        AppSettings settings,
        IClock clock,
        ContactDirectory contacts,
        IScheduledSendRepository scheduled)
    {
        _store = store;
        _actions = actions;
        _snoozes = snoozes;
        _folders = folders;
        _settings = settings;
        _clock = clock;

        Composer = new ComposerViewModel(store, contacts, scheduled, clock, settings.DayShape);
        Palette.QueryChanged += (_, _) => RefreshPalette();
    }

    public bool CanUndo => _undo.Count > 0;

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
        if (!quiet) IsLoading = true;
        try
        {
            _inbox = await _store.GetInboxAsync(ct).ConfigureAwait(true);
            _sent ??= await _store.GetSentItemsAsync(ct).ConfigureAwait(true);

            var mail = await _store
                .GetMailAsync(_inbox, _settings.InboxPageSize, ct).ConfigureAwait(true);

            // Your side of each conversation. Only decoration for the list, so a
            // failure here still shows the Inbox rather than nothing.
            IReadOnlyList<MailSummary> sent;
            try { sent = await _store.GetMailAsync(_sent.Value, _settings.SentPageSize, ct).ConfigureAwait(true); }
            catch (Exception) when (!ct.IsCancellationRequested) { sent = Array.Empty<MailSummary>(); }

            var threads = ConversationGrouper.Group(mail, sent);

            var flagged = (await _actions.GetOpenAsync(ct).ConfigureAwait(true))
                .Select(a => a.InternetMessageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Reuse the row objects for conversations that are still there.
            // Rebuilding them all would drop the selection and reload the
            // reading pane on every change notification.
            var existing = new Dictionary<string, MailRowViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _allRows) existing.TryAdd(row.Key, row);

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
            var selectedIndex = selected is null ? -1 : Rows.IndexOf(selected);

            ApplySearchFilter();

            if (selected is null || !Rows.Contains(selected))
            {
                // A conversation that came back (an undone move) is still the
                // same one; otherwise land on its neighbour.
                var key = selected?.Key;
                Selected = (string.IsNullOrEmpty(key)
                               ? null
                               : Rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)))
                           ?? (Rows.Count == 0 ? null : Rows[Math.Clamp(selectedIndex, 0, Rows.Count - 1)]);
            }

            if (!quiet)
            {
                Status = $"{Rows.Count} conversation{(Rows.Count == 1 ? "" : "s")}"
                       + $"  ·  {Rows.Count(r => r.IsUnread)} unread";
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not read the inbox: {ex.Message}";
        }
        finally
        {
            if (!quiet) IsLoading = false;
        }
    }

    private void ApplySearchFilter()
    {
        var query = SearchQuery.Trim();

        var source = query.Length == 0
            ? _allRows
            : _allRows.Where(r =>
                  FuzzyMatcher.Score(query, r.Subject) is not null ||
                  FuzzyMatcher.Score(query, r.Sender) is not null).ToList();

        SyncRows(source);
    }

    /// <summary>
    /// Brings <see cref="Rows"/> in line with <paramref name="target"/> using the
    /// fewest edits, so the list keeps its scroll position and selection
    /// instead of being cleared and refilled.
    /// </summary>
    private void SyncRows(IReadOnlyList<MailRowViewModel> target)
    {
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
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplySearchFilter();
        if (Selected is null || !Rows.Contains(Selected)) Selected = Rows.FirstOrDefault();
    }

    partial void OnSelectedChanged(MailRowViewModel? value)
    {
        _ = LoadBodyAsync(value);
    }

    private async Task LoadBodyAsync(MailRowViewModel? row)
    {
        // Cancel the previous load but do not dispose it: an in-flight
        // Task.Delay still holds the token, and disposing under it raises
        // ObjectDisposedException instead of the cancellation we want.
        _bodyLoad?.Cancel();

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
            // Every message in the conversation, yours included, newest first.
            var bodies = new List<MailBody>();
            foreach (var message in row.Thread.Messages.Take(Math.Max(1, _settings.ThreadMessageLimit)))
            {
                try { bodies.Add(await _store.GetBodyAsync(message.Ref, cts.Token).ConfigureAwait(true)); }
                catch (Exception) when (!cts.IsCancellationRequested && bodies.Count > 0) { /* skip one that moved */ }
                if (cts.IsCancellationRequested) return;
            }

            OpenBody = bodies[0];
            ThreadAttachments = bodies
                .SelectMany(b => b.Attachments)
                .DistinctBy(a => (a.Name.ToLowerInvariant(), a.Size))
                .ToList();
            BodyHtml = HtmlPresenter.RenderThread(bodies, _settings.BlockRemoteImages,
                hiddenOlder: row.Thread.Count - bodies.Count);

            await MarkReadAfterDwellAsync(row, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Status = $"Could not open that message: {ex.Message}";
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
        if (Rows.Count == 0) return;

        var index = Selected is null ? 0 : Rows.IndexOf(Selected) + delta;
        Selected = Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    public void MoveToEnd(bool last) =>
        Selected = last ? Rows.LastOrDefault() : Rows.FirstOrDefault();

    // ---- triage actions ---------------------------------------------------

    public async Task ToggleActionRequiredAsync(bool required)
    {
        if (Selected is not { } row) return;

        row.IsBusy = true;
        try
        {
            var summary = row.Summary;

            if (required)
            {
                // The Outlook category makes the flag visible inside Outlook
                // itself, so the state is not trapped in this app.
                await _store.SetCategoryAsync(summary.Ref, _settings.ActionCategory, true)
                    .ConfigureAwait(true);

                await _actions.UpsertAsync(new ActionItem
                {
                    InternetMessageId = summary.InternetMessageId,
                    EntryId = summary.Ref.EntryId,
                    StoreId = summary.Ref.StoreId,
                    Subject = summary.Subject,
                    SenderName = summary.SenderName,
                    SenderAddress = summary.SenderAddress,
                    ReceivedUtc = summary.ReceivedUtc,
                    CreatedUtc = _clock.UtcNow,
                }).ConfigureAwait(true);

                row.IsActionRequired = true;
                Status = $"Flagged for action · {row.Subject}";
            }
            else
            {
                await _store.SetCategoryAsync(summary.Ref, _settings.ActionCategory, false)
                    .ConfigureAwait(true);

                var existing = await _actions
                    .GetByMessageIdAsync(summary.InternetMessageId).ConfigureAwait(true);

                if (existing is not null)
                    await _actions.DeleteAsync(existing.Id).ConfigureAwait(true);

                row.IsActionRequired = false;
                Status = "Marked as needing no action";
            }

            Move(1);
        }
        catch (Exception ex)
        {
            Status = $"Could not update that message: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public async Task ToggleReadAsync()
    {
        if (Selected is not { } row) return;

        try
        {
            if (row.IsUnread)
            {
                // Reading the conversation clears all of it...
                foreach (var m in row.InboxMessages.Where(m => m.IsUnread))
                    await _store.SetReadAsync(m.Ref, true).ConfigureAwait(true);
                row.Refresh(row.Thread.WithInboxUnread(false));
            }
            else
            {
                // ...but "come back to this" only needs the newest message bold.
                await _store.SetReadAsync(row.Summary.Ref, false).ConfigureAwait(true);
                row.Refresh(row.Thread.WithLatestInboxUnread());
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not change the read state: {ex.Message}";
        }
    }

    // ---- move palette (k) -------------------------------------------------

    public async Task OpenFolderPaletteAsync()
    {
        if (Selected is not { } row) return;

        Palette.Open(
            PaletteMode.Folder,
            "Move to folder",
            "Enter move · Ctrl+Enter create and move · Esc cancel",
            row.Subject);

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

    private void RefreshPalette()
    {
        if (!Palette.IsOpen) return;

        switch (Palette.Mode)
        {
            case PaletteMode.Folder: RefreshFolderPalette(); break;
            case PaletteMode.Attachment: RefreshAttachmentPalette(); break;
            default: RefreshSnoozePalette(); break;
        }
    }

    private void RefreshFolderPalette()
    {
        var matches = _folders.Search(Palette.Query);

        Palette.SetEntries(matches.Select(m => new PaletteEntry(
            m.Folder.Name,
            TrimPath(m.Folder.Path, m.Folder.Name),
            m.Folder,
            m.NameHighlights)));

        // When nothing matches, offer to create what was typed rather than
        // making the user leave and go build the folder in Outlook.
        var typed = Palette.Query.Trim();
        Palette.CreatePrompt = matches.Count == 0 && typed.Length > 0
            ? BuildCreatePrompt(typed)
            : null;
    }

    private string BuildCreatePrompt(string typed)
    {
        var (parent, name) = _folders.ResolveCreationTarget(typed);
        return parent is null
            ? $"Create \"{name}\" at the top level and move here"
            : $"Create \"{name}\" under {parent.Path} and move here";
    }

    private static string TrimPath(string path, string leaf)
    {
        var idx = path.LastIndexOf('\\');
        return idx <= 0 ? "" : path[..idx];
    }

    public async Task ConfirmPaletteAsync(bool forceCreate)
    {
        if (!Palette.IsOpen) return;

        switch (Palette.Mode)
        {
            case PaletteMode.Folder: await ConfirmFolderAsync(forceCreate).ConfigureAwait(true); break;
            case PaletteMode.Attachment:
                var pick = Palette.Selected?.Payload as MailAttachment;
                Palette.Close();
                if (pick is not null) await OpenAttachmentAsync(pick).ConfigureAwait(true);
                break;
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

        // One attachment: skip the picker and just open it.
        if (ThreadAttachments.Count == 1) return OpenAttachmentAsync(ThreadAttachments[0]);

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

                var (parent, name) = _folders.ResolveCreationTarget(typed);
                target = await _store
                    .CreateFolderAsync(parent?.Ref ?? default, name).ConfigureAwait(true);

                _folders.AddToIndex(target);
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

    private async Task MoveSelectedAsync(FolderNode target)
    {
        if (Selected is not { } row) return;

        var origin = _inbox;
        var moved = new List<MailRef>();

        try
        {
            row.IsBusy = true;

            // The whole conversation goes, so the thread leaves the list in one
            // keystroke. Your sent copies stay in Sent Items.
            foreach (var m in row.InboxMessages)
            {
                var to = await _store.MoveAsync(m.Ref, target.Ref).ConfigureAwait(true);
                moved.Add(to);
                await _actions.UpdateLocationAsync(m.InternetMessageId, to.EntryId, to.StoreId).ConfigureAwait(true);
            }
            await _folders.RecordUseAsync(target).ConfigureAwait(true);

            RemoveRow(row);
            Status = moved.Count == 1
                ? $"Moved to {target.Path}"
                : $"Moved {moved.Count} messages to {target.Path}";
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            Status = moved.Count == 0
                ? $"Move failed: {ex.Message}"
                : $"Moved {moved.Count} of {row.InboxMessages.Count} messages, then failed: {ex.Message}";
        }

        if (moved.Count > 0)
        {
            PushUndo($"move to {target.Name}", async () =>
            {
                foreach (var m in moved) await _store.MoveAsync(m, origin).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            });
        }
    }

    // ---- snooze palette (g) ----------------------------------------------

    public void OpenSnoozePalette()
    {
        if (Selected is not { } row) return;

        _snoozePresets = SnoozePresets.For(_clock.Now, _settings.DayShape);

        Palette.Open(
            PaletteMode.Snooze,
            "Come back to this",
            "Enter confirm · type e.g. \"tomorrow 9am\", \"fri\", \"3d\" · Esc cancel",
            row.Subject);

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

    private async Task ConfirmSnoozeAsync()
    {
        if (Palette.Selected?.Payload is not DateTimeOffset when) return;
        if (Selected is not { } row) return;

        Palette.Close();

        var origin = _inbox;
        var moved = new List<(MailRef Ref, long SnoozeId)>();

        try
        {
            row.IsBusy = true;

            var holding = await _store
                .EnsureFolderPathAsync(_settings.SnoozeFolder).ConfigureAwait(true);

            // Every Inbox message in the conversation is parked, each with its
            // own entry, so they all come back together.
            foreach (var m in row.InboxMessages)
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

                moved.Add((to, entry.Id));
            }

            RemoveRow(row);
            Status = $"Back in your inbox {Humanise(when - _clock.Now)} · {when:ddd d MMM HH:mm}";
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            Status = $"Could not snooze that conversation: {ex.Message}";
        }

        if (moved.Count > 0)
        {
            PushUndo("snooze", async () =>
            {
                foreach (var (m, id) in moved)
                {
                    await _store.MoveAsync(m, origin).ConfigureAwait(true);
                    await _snoozes.CancelAsync(id).ConfigureAwait(true);
                }
                await LoadAsync().ConfigureAwait(true);
            });
        }
    }

    // ---- replies and forwards (r / Shift+R / f) ---------------------------

    public async Task StartReplyAsync(ReplyScope scope)
    {
        if (Selected is not { } row) return;

        try
        {
            Status = scope == ReplyScope.Forward ? "Preparing forward..." : "Preparing reply...";
            var draft = await _store.BuildReplyAsync(row.Summary.Ref, scope).ConfigureAwait(true);
            Composer.Open(draft);
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"Could not start a reply: {ex.Message}";
        }
    }

    // ---- archive, delete, undo -------------------------------------------

    public async Task ArchiveAsync()
    {
        try
        {
            if (Selected is null) return;

            // Always the Archive beside the Inbox, created if missing. Searching
            // the folder index by name could land on another mailbox's Archive,
            // or find nothing and leave the message sitting in the list.
            var archive = await _store.EnsureFolderPathAsync("Archive").ConfigureAwait(true);

            await MoveSelectedAsync(new FolderNode
            {
                Ref = archive,
                Name = "Archive",
                Path = archive.Path,
                Depth = 0,
                StoreName = "",
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Archive failed: {ex.Message}";
        }
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0)
        {
            Status = "Nothing to undo";
            return;
        }

        var step = _undo.Pop();
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

    private void PushUndo(string description, Func<Task> revert)
    {
        _undo.Push(new UndoStep(description, revert));

        // One level of regret is enough; more would be misleading once the
        // underlying items have shifted around.
        while (_undo.Count > 10) _undo.Pop();

        OnPropertyChanged(nameof(CanUndo));
    }

    private void RemoveRow(MailRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        _allRows.Remove(row);

        // A live refresh may already have taken it out and moved the
        // selection on; doing it again would jump to the top of the list.
        if (index < 0) return;

        Rows.RemoveAt(index);

        // Land on the next message so triage keeps flowing without a keystroke.
        Selected = Rows.Count == 0
            ? null
            : Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    public void CancelOverlays()
    {
        if (Composer.IsOpen) { _ = Composer.DiscardAsync(); return; }
        if (Palette.IsOpen) { Palette.Close(); return; }
        if (IsSearching) { IsSearching = false; SearchQuery = ""; }
    }
}
