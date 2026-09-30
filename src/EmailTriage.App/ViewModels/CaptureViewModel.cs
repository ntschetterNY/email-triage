using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// The popup behind `a`: what has to happen, who has the ball, when it is
/// due and when to chase, asked once while the mail is still on screen. Every
/// field is optional, so Enter straight away is the plain flag it always was.
/// Holds presentation state only; <see cref="TriageViewModel"/> opens it and
/// files the answers.
/// </summary>
public sealed partial class CaptureViewModel : ObservableObject
{
    private readonly ContactDirectory _contacts;
    private readonly IClock _clock;
    private readonly AppSettings _settings;

    private IReadOnlyList<Recipient> _onThread = Array.Empty<Recipient>();
    private IReadOnlyList<Recipient> _known = Array.Empty<Recipient>();
    private ActionStage _currentStage = ActionStage.ToDo;
    private DateTimeOffset? _existingDue;

    public CaptureViewModel(ContactDirectory contacts, IClock clock, AppSettings settings)
    {
        _contacts = contacts;
        _clock = clock;
        _settings = settings;
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _contextLine = "";

    /// <summary>Several conversations at once: the title box then applies to all of them.</summary>
    [ObservableProperty] private bool _isMultiple;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _who = "";
    [ObservableProperty] private bool _isBlocker;
    [ObservableProperty] private string _dueText = "";
    [ObservableProperty] private string _followUpText = "";
    [ObservableProperty] private ActionPriority _priority = ActionPriority.Normal;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _showNotes;
    [ObservableProperty] private bool _tellThem;

    /// <summary>Why Enter did not save, shown under the form until the next edit.</summary>
    [ObservableProperty] private string _problem = "";

    /// <summary>The cursor is in the Who box, so Up and Down pick a suggestion.</summary>
    [ObservableProperty] private bool _isWhoFocused;

    [ObservableProperty] private int _suggestionIndex = -1;

    /// <summary>The closest matches for what is typed in Who, likeliest first.</summary>
    public ObservableCollection<Recipient> Suggestions { get; } = new();

    public bool HasSuggestions => IsWhoFocused && Suggestions.Count > 0;

    public bool HasWho => Who.Trim().Length > 0;

    /// <summary>Telling someone by email needs a hand-off, not a blocker, and someone to tell.</summary>
    public bool CanTell => HasWho && !IsBlocker;

    /// <summary>Who the Who box means: the highlighted suggestion, or the text as typed.</summary>
    public Recipient? Person
    {
        get
        {
            if (!HasWho) return null;
            if (Suggestions.Count > 0)
                return Suggestions[Math.Clamp(SuggestionIndex, 0, Suggestions.Count - 1)];
            return PersonResolver.Resolve(Who, Array.Empty<Recipient>());
        }
    }

    public string WhoPreview => Person switch
    {
        null => "leave empty when it is on you",
        { Address.Length: 0 } p => $"{p.Name} · no address on file, so no chase mail",
        { } p when p.Name == p.Address => p.Address,
        { } p => $"{p.Name} <{p.Address}>",
    };

    public string WhoKindLabel => IsBlocker
        ? "BLOCKED BY them · Ctrl+B makes it a hand-off"
        : "they owe me · Ctrl+B makes it a blocker";

    public DateTimeOffset? Due => Parse(DueText);

    public string DuePreview => DueText.Trim().Length == 0
        ? _existingDue is { } kept ? $"keeps {kept.ToLocalTime():ddd d MMM}" : "e.g. fri, 3d, 14 oct"
        : Due is { } d ? d.ToLocalTime().ToString("dddd d MMM") : "not a date I understand";

    /// <summary>The chase day: typed, or worked out from the due date and settings.</summary>
    public DateTimeOffset? FollowUp => FollowUpText.Trim().Length > 0
        ? Parse(FollowUpText)
        : HasWho
            ? ActionCapture.DefaultFollowUp(
                Due ?? _existingDue, _clock.Now, _settings.FollowUpBeforeDueDays, _settings.FollowUpAfterDays)
            : null;

    public string FollowUpPreview => FollowUpText.Trim().Length == 0
        ? FollowUp is { } f ? $"{f.ToLocalTime():dddd d MMM} unless you say otherwise" : "no chase"
        : FollowUp is { } d ? d.ToLocalTime().ToString("dddd d MMM") : "not a date I understand";

    public string PriorityLabel => $"Priority  {Priority}";

    public string NotesLabel => ShowNotes ? "Notes" : "Notes  Ctrl+N";

    public string TellLabel => TellThem
        ? "✓ Tell them by email after saving  Ctrl+M"
        : "Tell them by email  Ctrl+M";

    /// <summary>Where the card will land, as the form stands.</summary>
    public string ResultLine => ActionCapture.Describe(Preview(), _currentStage, _clock.Now)
        + (ReturnTime is { } back ? $" · out of the inbox until {back.ToLocalTime():ddd d MMM HH:mm}" : "");

    /// <summary>When the mail comes back to the Inbox, or null when saving leaves it there.</summary>
    public DateTimeOffset? ReturnTime =>
        _settings.SnoozeUntilActionDate ? ActionCapture.ReturnTime(Preview(), _clock.UtcNow) : null;

    public string Hint =>
        "Enter save · Tab next field · Shift+Enter flag with no details · Esc cancel"
        + " · Ctrl+B blocker · Ctrl+N notes · Ctrl+P priority"
        + (CanTell ? " · Ctrl+M tell them" : "");

    public void Open(
        string contextLine, string title, bool multiple, ActionItem? existing,
        IReadOnlyList<Recipient> onThread, IReadOnlyList<Recipient> known)
    {
        _onThread = onThread;
        _known = known;
        _currentStage = existing is null ? ActionStage.ToDo : existing.IsComplete ? ActionStage.Done : existing.Stage;
        _existingDue = existing?.DueUtc;

        ContextLine = contextLine;
        IsMultiple = multiple;
        Title = existing is { Title.Length: > 0 } ? existing.Title : title;
        Who = "";
        IsBlocker = false;
        DueText = "";
        FollowUpText = "";
        Priority = existing?.Priority ?? ActionPriority.Normal;
        Notes = existing?.Notes ?? "";
        ShowNotes = Notes.Length > 0;
        TellThem = false;
        Problem = "";
        Suggestions.Clear();
        SuggestionIndex = -1;
        IsOpen = true;
        RefreshDerived();
    }

    public void Close()
    {
        IsOpen = false;
        Suggestions.Clear();
        Problem = "";
    }

    public void ToggleNotes() => ShowNotes = !ShowNotes;

    public void CyclePriority() => Priority = Priority switch
    {
        ActionPriority.Low => ActionPriority.Normal,
        ActionPriority.Normal => ActionPriority.High,
        _ => ActionPriority.Low,
    };

    public void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0) return;
        var next = (SuggestionIndex + delta) % Suggestions.Count;
        if (next < 0) next += Suggestions.Count;
        SuggestionIndex = next;
    }

    /// <summary>
    /// The answers as a request, or null with <see cref="Problem"/> set when a
    /// date could not be read - a typo must not silently become "no date".
    /// </summary>
    public CaptureRequest? Build()
    {
        if (DueText.Trim().Length > 0 && Due is null)
        {
            Problem = "Due: not a date I understand - try \"fri\", \"14 oct\" or \"3d\"";
            return null;
        }
        if (FollowUpText.Trim().Length > 0 && Parse(FollowUpText) is null)
        {
            Problem = "Follow up: not a date I understand - try \"wed\", \"1 oct\" or \"2d\"";
            return null;
        }

        Problem = "";
        return Preview();
    }

    /// <summary>The form as it stands, unreadable dates treated as empty.</summary>
    private CaptureRequest Preview() => new()
    {
        Title = Title.Trim(),
        Who = Person,
        IsBlocker = IsBlocker,
        DueUtc = Due,
        FollowUpUtc = HasWho ? FollowUp : null,
        Priority = Priority,
        Notes = Notes.Trim(),
    };

    private DateTimeOffset? Parse(string text) =>
        NaturalDateParser.TryParse(text, _clock.Now, out var when, _settings.DayShape)
            ? when.ToUniversalTime()
            : null;

    /// <summary>
    /// Matches for the Who box: people on the thread first, then people already
    /// waited on, then the contact directory - so a first name is usually enough.
    /// </summary>
    private void RefreshSuggestions()
    {
        Suggestions.Clear();

        var typed = Who.Trim();
        if (typed.Length > 0)
        {
            var directory = _contacts.Search(typed, 5)
                .Where(c => c.Address.Length > 0)
                .Select(c => new Recipient(c.Name, c.Address));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ranked = _onThread.Select(r => (Person: r, Group: 0))
                .Concat(_known.Select(r => (Person: r, Group: 1)))
                .Concat(directory.Select(r => (Person: r, Group: 2)))
                .Where(c => seen.Add(c.Person.Address.Length > 0 ? c.Person.Address : c.Person.Display))
                .Select(c => (c.Person, c.Group, Score: Score(typed, c.Person)))
                .Where(c => c.Score is not null)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Group)
                .Take(5);

            foreach (var c in ranked) Suggestions.Add(c.Person);
        }

        SuggestionIndex = Suggestions.Count > 0 ? 0 : -1;
    }

    private static int? Score(string typed, Recipient person)
    {
        if (person.Address.Equals(typed, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        var name = FuzzyMatcher.Score(typed, person.Display);
        var address = person.Address.Length > 0 ? FuzzyMatcher.Score(typed, person.Address) : null;
        return name is null ? address : address is null ? name : Math.Max(name.Value, address.Value);
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(HasWho));
        OnPropertyChanged(nameof(CanTell));
        OnPropertyChanged(nameof(Person));
        OnPropertyChanged(nameof(WhoPreview));
        OnPropertyChanged(nameof(WhoKindLabel));
        OnPropertyChanged(nameof(Due));
        OnPropertyChanged(nameof(DuePreview));
        OnPropertyChanged(nameof(FollowUp));
        OnPropertyChanged(nameof(FollowUpPreview));
        OnPropertyChanged(nameof(PriorityLabel));
        OnPropertyChanged(nameof(NotesLabel));
        OnPropertyChanged(nameof(TellLabel));
        OnPropertyChanged(nameof(ResultLine));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasSuggestions));
    }

    partial void OnWhoChanged(string value)
    {
        Problem = "";
        RefreshSuggestions();
        RefreshDerived();
    }

    partial void OnTitleChanged(string value) { Problem = ""; RefreshDerived(); }
    partial void OnDueTextChanged(string value) { Problem = ""; RefreshDerived(); }
    partial void OnFollowUpTextChanged(string value) { Problem = ""; RefreshDerived(); }
    partial void OnIsBlockerChanged(bool value) => RefreshDerived();
    partial void OnPriorityChanged(ActionPriority value) => RefreshDerived();
    partial void OnShowNotesChanged(bool value) => RefreshDerived();
    partial void OnTellThemChanged(bool value) => RefreshDerived();
    partial void OnIsWhoFocusedChanged(bool value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnSuggestionIndexChanged(int value) => RefreshDerived();
}
