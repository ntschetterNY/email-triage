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
    private readonly Stack<UndoStep> _undo = new();
    private CancellationTokenSource? _bodyLoad;
    private IReadOnlyList<SnoozeOption> _snoozePresets = Array.Empty<SnoozeOption>();

    public ObservableCollection<MailRowViewModel> Rows { get; } = new();
    public PaletteViewModel Palette { get; } = new();
    public ComposerViewModel Composer { get; }

    [ObservableProperty] private MailRowViewModel? _selected;
    [ObservableProperty] private MailBody? _openBody;
    [ObservableProperty] private string _bodyHtml = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;

    private List<MailRowViewModel> _allRows = new();

    public TriageViewModel(
        IMailStore store,
        IActionItemRepository actions,
        ISnoozeRepository snoozes,
        FolderSearchService folders,
        AppSettings settings,
        IClock clock)
    {
        _store = store;
        _actions = actions;
        _snoozes = snoozes;
        _folders = folders;
        _settings = settings;
        _clock = clock;

        Composer = new ComposerViewModel(store);
        Palette.QueryChanged += (_, _) => RefreshPalette();
    }

    public bool CanUndo => _undo.Count > 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            _inbox = await _store.GetInboxAsync(ct).ConfigureAwait(true);

            var mail = await _store
                .GetMailAsync(_inbox, _settings.InboxPageSize, ct).ConfigureAwait(true);

            var flagged = (await _actions.GetOpenAsync(ct).ConfigureAwait(true))
                .Select(a => a.InternetMessageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var previouslySelected = Selected?.Summary.InternetMessageId;

            _allRows = mail
                .Select(m => new MailRowViewModel(m, flagged.Contains(m.InternetMessageId)))
                .ToList();

            ApplySearchFilter();

            // Keep the caret where it was across a refresh where possible.
            Selected = previouslySelected is not null
                ? Rows.FirstOrDefault(r => r.Summary.InternetMessageId == previouslySelected) ?? Rows.FirstOrDefault()
                : Rows.FirstOrDefault();

            Status = $"{Rows.Count} message{(Rows.Count == 1 ? "" : "s")}"
                   + $"  ·  {Rows.Count(r => r.IsUnread)} unread";
        }
        catch (Exception ex)
        {
            Status = $"Could not read the inbox: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
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

        Rows.Clear();
        foreach (var row in source) Rows.Add(row);
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
            BodyHtml = "";
            return;
        }

        var cts = new CancellationTokenSource();
        _bodyLoad = cts;

        try
        {
            var body = await _store.GetBodyAsync(row.Summary.Ref, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            OpenBody = body;
            BodyHtml = HtmlPresenter.Render(body, _settings.BlockRemoteImages);

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

        await _store.SetReadAsync(row.Summary.Ref, true, ct).ConfigureAwait(true);
        row.Refresh(row.Summary with { IsUnread = false });
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
            var target = row.IsUnread;
            await _store.SetReadAsync(row.Summary.Ref, target).ConfigureAwait(true);
            row.Refresh(row.Summary with { IsUnread = !target });
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

        if (Palette.Mode == PaletteMode.Folder) RefreshFolderPalette();
        else RefreshSnoozePalette();
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

        if (Palette.Mode == PaletteMode.Folder) await ConfirmFolderAsync(forceCreate).ConfigureAwait(true);
        else await ConfirmSnoozeAsync().ConfigureAwait(true);
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
        var summary = row.Summary;

        try
        {
            row.IsBusy = true;

            var moved = await _store.MoveAsync(summary.Ref, target.Ref).ConfigureAwait(true);
            await _folders.RecordUseAsync(target).ConfigureAwait(true);
            await _actions.UpdateLocationAsync(
                summary.InternetMessageId, moved.EntryId, moved.StoreId).ConfigureAwait(true);

            RemoveRow(row);
            Status = $"Moved to {target.Path}";

            PushUndo($"move to {target.Name}", async () =>
            {
                await _store.MoveAsync(moved, origin).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            });
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            Status = $"Move failed: {ex.Message}";
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

        var summary = row.Summary;
        var origin = _inbox;

        try
        {
            row.IsBusy = true;

            var holding = await _store
                .EnsureFolderPathAsync(_settings.SnoozeFolder).ConfigureAwait(true);

            var moved = await _store.MoveAsync(summary.Ref, holding).ConfigureAwait(true);

            await _snoozes.AddAsync(new SnoozeEntry
            {
                InternetMessageId = summary.InternetMessageId,
                EntryId = moved.EntryId,
                StoreId = moved.StoreId,
                Subject = summary.Subject,
                SenderName = summary.DisplaySender,
                OriginFolderEntryId = origin.EntryId,
                OriginFolderStoreId = origin.StoreId,
                OriginFolderPath = origin.Path,
                SnoozedUtc = _clock.UtcNow,
                ReturnUtc = when.ToUniversalTime(),
            }).ConfigureAwait(true);

            RemoveRow(row);
            Status = $"Back in your inbox {Humanise(when - _clock.Now)} · {when:ddd d MMM HH:mm}";

            PushUndo("snooze", async () =>
            {
                await _store.MoveAsync(moved, origin).ConfigureAwait(true);
                await LoadAsync().ConfigureAwait(true);
            });
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            Status = $"Could not snooze that message: {ex.Message}";
        }
    }

    // ---- replies (r / Shift+R) -------------------------------------------

    public async Task StartReplyAsync(ReplyScope scope)
    {
        if (Selected is not { } row) return;

        try
        {
            Status = "Preparing reply...";
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
            await _folders.EnsureIndexedAsync().ConfigureAwait(true);

            var archive = _folders.Search("Archive")
                .FirstOrDefault(m => m.Folder.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase));

            if (archive is null)
            {
                Status = "No Archive folder found - use the move command instead";
                return;
            }

            await MoveSelectedAsync(archive.Folder).ConfigureAwait(true);
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

        Rows.Remove(row);
        _allRows.Remove(row);

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
