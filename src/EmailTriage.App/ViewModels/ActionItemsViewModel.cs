using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum EditorMode { None, Note, Blocker, Assignment }

/// <summary>
/// The second surface: mail that needs work, with the notes, blockers and
/// hand-offs attached to it. Assignments stay local until the user explicitly
/// asks for a draft, so nothing leaves the machine by accident.
/// </summary>
public sealed partial class ActionItemsViewModel : ObservableObject
{
    private readonly IActionItemRepository _repo;
    private readonly IMailStore _store;
    private readonly IClock _clock;
    private readonly AppSettings _settings;

    public ObservableCollection<ActionItem> Items { get; } = new();

    [ObservableProperty] private ActionItem? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showCompleted;

    // Inline editor state. One editor at a time keeps the key handling simple.
    [ObservableProperty] private EditorMode _editor = EditorMode.None;
    [ObservableProperty] private string _editorTitle = "";
    [ObservableProperty] private string _fieldPrimary = "";
    [ObservableProperty] private string _fieldSecondary = "";
    [ObservableProperty] private string _fieldDue = "";
    [ObservableProperty] private string _editorHint = "";

    public ObservableCollection<string> KnownAssignees { get; } = new();

    public ActionItemsViewModel(
        IActionItemRepository repo, IMailStore store, IClock clock, AppSettings settings)
    {
        _repo = repo;
        _store = store;
        _clock = clock;
        _settings = settings;
    }

    public bool HasSelection => Selected is not null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var previous = Selected?.Id;

            var open = await _repo.GetOpenAsync(ct).ConfigureAwait(true);
            var items = open
                .OrderByDescending(i => i.Priority)
                .ThenBy(i => i.IsBlocked)
                .ThenByDescending(i => i.ReceivedUtc)
                .ToList();

            if (ShowCompleted)
                items.AddRange(await _repo.GetCompletedAsync(50, ct).ConfigureAwait(true));

            Items.Clear();
            foreach (var i in items) Items.Add(i);

            Selected = previous is not null
                ? Items.FirstOrDefault(i => i.Id == previous) ?? Items.FirstOrDefault()
                : Items.FirstOrDefault();

            var assignees = await _repo.GetKnownAssigneesAsync(ct).ConfigureAwait(true);
            KnownAssignees.Clear();
            foreach (var (name, email) in assignees)
                KnownAssignees.Add(string.IsNullOrWhiteSpace(email) ? name : $"{name} <{email}>");

            var blocked = Items.Count(i => i.IsBlocked && !i.IsComplete);
            Status = $"{Items.Count(i => !i.IsComplete)} open"
                   + (blocked > 0 ? $"  ·  {blocked} blocked" : "");
        }
        catch (Exception ex)
        {
            Status = $"Could not load action items: {ex.Message}";
        }
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;

        var index = Selected is null ? 0 : Items.IndexOf(Selected) + delta;
        Selected = Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    partial void OnSelectedChanged(ActionItem? value) => OnPropertyChanged(nameof(HasSelection));

    // ---- editors ----------------------------------------------------------

    public void OpenEditor(EditorMode mode)
    {
        if (Selected is null) return;

        Editor = mode;
        FieldPrimary = mode == EditorMode.Note ? Selected.Notes : "";
        FieldSecondary = "";
        FieldDue = "";

        (EditorTitle, EditorHint) = mode switch
        {
            EditorMode.Note => ("Notes", "Ctrl+Enter save · Esc cancel"),
            EditorMode.Blocker => ("What is blocking this?", "Tab between fields · Ctrl+Enter save · Esc cancel"),
            EditorMode.Assignment => ("Assign to someone", "Tab between fields · Ctrl+Enter save · Esc cancel"),
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
                    Status = "Blocker added";
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
                    Status = $"Assigned to {name} · nothing sent yet";
                    break;
            }

            CloseEditor();
            OnPropertyChanged(nameof(Selected));
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

    // ---- item operations --------------------------------------------------

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

            Status = target ? "Done" : "Reopened";
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
        await _repo.SetBlockerResolvedAsync(blocker.Id, !blocker.IsResolved).ConfigureAwait(true);
        blocker.ResolvedUtc = blocker.IsResolved ? null : _clock.UtcNow;
        OnPropertyChanged(nameof(Selected));
        await LoadAsync().ConfigureAwait(true);
    }

    public async Task ToggleAssignmentAsync(Assignment assignment)
    {
        await _repo.SetAssignmentDoneAsync(assignment.Id, !assignment.IsDone).ConfigureAwait(true);
        assignment.DoneUtc = assignment.IsDone ? null : _clock.UtcNow;
        OnPropertyChanged(nameof(Selected));
        await LoadAsync().ConfigureAwait(true);
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
}
