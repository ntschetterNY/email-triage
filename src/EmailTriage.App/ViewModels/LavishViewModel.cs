using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmailTriage.App.Services;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// Lavish: the button at the top right that turns the window into something
/// to comment on. Point at any element, say what should change, and it goes
/// to the app's GitHub repo as an issue; the panel then follows each one
/// through branch, pull request, merge and release. Presentation state only -
/// the window finds and describes the element under the mouse.
/// </summary>
public sealed partial class LavishViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly LavishConnection _connection;
    private readonly LavishLog _log;
    private readonly List<LavishNote> _notes;
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public LavishViewModel(LavishConnection connection)
    {
        _connection = connection;
        _log = new LavishLog(LavishConnection.LogPath);
        _notes = _log.Load();
        Rebuild();
    }

    /// <summary>Comment mode is on: the next click picks an element instead of working it.</summary>
    [ObservableProperty] private bool _isAnnotating;

    /// <summary>An element has been picked and the note card is open under it.</summary>
    [ObservableProperty] private bool _isComposing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(HasTargetText), nameof(TargetAnchor), nameof(TargetPath))]
    private LavishTarget? _target;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _comment = "";

    /// <summary>Whether what the element says goes in the issue. Off for anything that could be mail.</summary>
    [ObservableProperty] private bool _includeText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendLabel))]
    private bool _isSending;

    public string SendLabel => IsSending ? "Sending…" : "Send to GitHub";

    [ObservableProperty] private string _status = "";

    /// <summary>The notes panel down the right; it folds away to reach what is under it.</summary>
    [ObservableProperty] private bool _isPanelOpen = true;

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    /// <summary>Notes not yet merged, for the badge on the button.</summary>
    [ObservableProperty] private int _openCount;

    public ObservableCollection<LavishNoteRow> Notes { get; } = new();

    public string Repo => _connection.Repo;

    public string TargetAnchor => Target?.Describe() ?? "";
    public string TargetPath => Target is { Path.Length: > 0 } t ? t.Path : Target?.Area ?? "";
    public string? TargetText => Target?.Text;
    public bool HasTargetText => Target?.Text is { Length: > 0 };

    /// <summary>The window drops a numbered pin on the element once its note is away.</summary>
    public event EventHandler<LavishNote>? NoteSent;

    /// <summary>
    /// The window comment mode is running in - the main window, Settings, a
    /// meeting card. Each has its own Lavish layer; only this one's is shown.
    /// </summary>
    [ObservableProperty] private object? _host;

    /// <summary>The Lavish button or Ctrl+Shift+L in <paramref name="host"/>: on there, or off wherever it is.</summary>
    public void Toggle(object host)
    {
        if (IsAnnotating) Stop();
        else Start(host);
    }

    public void Start(object host)
    {
        Cancel();
        Host = host;
        IsAnnotating = true;
        Status = "Click anything to comment on it · Esc to finish";
        if (DateTimeOffset.Now - _refreshedAt > RefreshEvery) _ = RefreshAsync();
    }

    public void Stop()
    {
        Cancel();
        IsAnnotating = false;
    }

    /// <summary>Opens the note card on an element. <paramref name="textIsSafe"/> says what it says is app text, not mail.</summary>
    public void Begin(LavishTarget target, bool textIsSafe)
    {
        Target = target;
        IncludeText = textIsSafe && target.Text is { Length: > 0 };
        Comment = "";
        IsComposing = true;
    }

    /// <summary>Esc on the card: drop the note, stay in comment mode.</summary>
    public void Cancel()
    {
        IsComposing = false;
        Target = null;
        Comment = "";
    }

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Comment);

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (Target is null || !CanSend()) return;

        var note = new LavishNote
        {
            Comment = Comment.Trim(),
            Target = IncludeText ? Target : Target with { Text = null },
        };
        var title = LavishIssue.Title(note.Comment);
        var body = LavishIssue.Body(note, AppUpdater.DisplayVersion, Environment.OSVersion.VersionString);

        IsSending = true;
        try
        {
            var github = await _connection.ConnectAsync();
            string? problem = null;
            if (github.CanFile)
            {
                try
                {
                    var (number, url) = await github.CreateAsync(title, body);
                    note.IssueNumber = number;
                    note.IssueUrl = url;
                    note.Stage = LavishStage.Filed;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _connection.ForgetToken();
                    problem = ex is TaskCanceledException ? "GitHub did not answer in time" : ex.Message;
                }
            }

            if (note.IssueNumber is null)
            {
                // No token, or GitHub said no: the form, filled in, in the browser.
                Open(LavishIssue.NewIssueUrl(_connection.Repo, title, body));
            }

            _notes.Insert(0, note);
            Save();
            Rebuild();
            Cancel();
            NoteSent?.Invoke(this, note);

            Status = note.IssueNumber is { } n
                ? $"Filed #{n} on GitHub. It moves along here as it is branched, reviewed and merged."
                : problem is null
                    ? "Opened on GitHub - press Submit there to file it."
                    : $"{problem.TrimEnd('.')}. Opened GitHub's form instead - press Submit there.";
        }
        catch (Exception ex)
        {
            Status = $"That did not send: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Reads each note's progress back from GitHub.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_notes.Count == 0) return;
        _refreshedAt = DateTimeOffset.Now;
        try
        {
            var github = await _connection.ConnectAsync();
            var issues = await github.ListAsync();
            if (GitHubIssues.Apply(_notes, issues))
            {
                Save();
                Rebuild();
            }
        }
        catch (Exception ex)
        {
            // Offline or rate-limited: the last known stages stay on screen.
            Debug.WriteLine($"Lavish refresh: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenNote(LavishNoteRow? row)
    {
        if (row is null) return;
        Open(row.Note.IssueUrl ?? $"https://github.com/{_connection.Repo}/issues?q=label%3Alavish");
    }

    [RelayCommand]
    private void OpenBoard() => Open($"https://github.com/{_connection.Repo}/issues?q=label%3Alavish");

    private void Save()
    {
        try { _log.Save(_notes); }
        catch (Exception ex) { Status = $"Could not save the Lavish log: {ex.Message}"; }
    }

    private void Rebuild()
    {
        Notes.Clear();
        foreach (var note in _notes) Notes.Add(new LavishNoteRow(note));
        OpenCount = _notes.Count(n => n.Stage < LavishStage.Merged);
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered; the status line still says where it went */ }
    }
}

/// <summary>One note in the Lavish panel: what was said, about what, and how far it has got.</summary>
public sealed class LavishNoteRow
{
    public LavishNoteRow(LavishNote note)
    {
        Note = note;
        Steps =
        [
            new("Filed", note.Stage >= LavishStage.Filed && note.Stage != LavishStage.Declined),
            new("Branch", note.Stage is >= LavishStage.Branch and <= LavishStage.Released),
            new("PR", note.Stage is >= LavishStage.InReview and <= LavishStage.Released),
            new("Merged", note.Stage is LavishStage.Merged or LavishStage.Released),
            new("Released", note.Stage is LavishStage.Released),
        ];
    }

    public LavishNote Note { get; }
    public string Comment => Note.Comment;
    public string Anchor => Note.Target.Describe();
    public string Number => Note.IssueNumber is { } n ? $"#{n}" : "";
    public string StageText => LavishIssue.Describe(Note.Stage);
    public string Stage => Note.Stage.ToString();
    public string When => Note.CreatedAt.ToString("MMM d");
    public IReadOnlyList<LavishStep> Steps { get; }
}

public sealed record LavishStep(string Label, bool Reached);
