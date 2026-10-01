using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// The pop-up a meeting pill in the top bar opens: what the meeting is, who is
/// coming, and a Join button. Joining takes a second, deliberate click. Your
/// own meetings and appointments can be edited here too, without Outlook.
/// </summary>
public sealed partial class MeetingCardViewModel : ObservableObject
{
    public MeetingCardViewModel(AgendaRow row, DateTimeOffset now)
    {
        Row = row;
        var ev = row.Event;
        Timing = ev.Start <= now
            ? $"On now · ends {CalendarMath.Countdown(ev.End - now)}"
            : $"Starts {CalendarMath.Countdown(ev.Start - now)}";
    }

    public AgendaRow Row { get; }

    /// <summary>"Starts in 4 min", "On now · ends in 12 min".</summary>
    public string Timing { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJoin), nameof(HasNoLink), nameof(JoinText), nameof(CanStartEdit), nameof(CanAddTeams))]
    private CalendarEventDetail? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLink), nameof(JoinText), nameof(CanStartEdit))]
    private bool _isLoading = true;

    public bool CanJoin => Detail?.JoinUrl is not null;

    public bool HasNoLink => !IsLoading && !CanJoin;

    /// <summary>"Join Teams meeting", or a word on why the button is not ready.</summary>
    public string JoinText => IsLoading ? "Finding the meeting link…"
        : Detail?.JoinUrl is { } url ? $"Join {Host(url)}meeting"
        : "No meeting link";

    // ---- editing ---------------------------------------------------------

    /// <summary>An appointment, or a meeting you organised: yours to change.</summary>
    public bool CanEdit => MeetingEdit.CanEdit(Row.Event);

    /// <summary>The attendees and notes are read first, so the form starts from all of it.</summary>
    public bool CanStartEdit => CanEdit && !IsLoading && Detail is not null;

    /// <summary>Why there is no Edit button, for someone else's meeting.</summary>
    public string EditHint => CanEdit ? "" : $"Only {(Row.Event.Organizer.Length > 0 ? Row.Event.Organizer : "the organizer")} can change this meeting.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    private bool _isEditing;

    public bool IsViewing => !IsEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaving;

    public bool CanSave => !IsSaving;

    /// <summary>Why the last save did not go through, or what it is doing now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditMessage))]
    private string _editMessage = "";

    public bool HasEditMessage => EditMessage.Length > 0;

    [ObservableProperty] private string _editSubject = "";
    [ObservableProperty] private string _editDate = "";
    [ObservableProperty] private string _editStart = "";
    [ObservableProperty] private string _editEnd = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTimes))]
    private bool _editAllDay;

    public bool ShowsTimes => !EditAllDay;

    [ObservableProperty] private string _editLocation = "";
    [ObservableProperty] private ShowAsChoice _editShowAs = ShowAs[2];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    private string _editRequired = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    private string _editOptional = "";

    [ObservableProperty] private string _editBody = "";
    [ObservableProperty] private bool _editAddTeams;

    /// <summary>The meeting already has a link; a second Teams meeting would only confuse.</summary>
    public bool CanAddTeams => !CanJoin;

    /// <summary>"Save", "Send update" or "Send invitation": says whether people hear of it.</summary>
    public string SaveLabel => MeetingEdit.SaveLabel(Form, Row.Event);

    /// <summary>A word on what the edit reaches: one day of a series, and who is told.</summary>
    public string EditNote => Row.Event.IsRecurring
        ? "Changes this day only - the rest of the series stays as it is."
        : "";

    public bool HasEditNote => EditNote.Length > 0;

    /// <summary>The Show as list, for the form's drop-down.</summary>
    public IReadOnlyList<ShowAsChoice> ShowAsChoices => ShowAs;

    private static readonly IReadOnlyList<ShowAsChoice> ShowAs = new[]
    {
        new ShowAsChoice(BusyStatus.Free, "Free"),
        new ShowAsChoice(BusyStatus.Tentative, "Tentative"),
        new ShowAsChoice(BusyStatus.Busy, "Busy"),
        new ShowAsChoice(BusyStatus.OutOfOffice, "Out of office"),
        new ShowAsChoice(BusyStatus.WorkingElsewhere, "Working elsewhere"),
    };

    private MeetingForm _original = new();

    /// <summary>Fills the form from the meeting as it stands.</summary>
    public void BeginEdit()
    {
        if (!CanStartEdit) return;

        _original = MeetingEdit.FormFor(Row.Event, Detail);
        EditSubject = _original.Subject;
        EditDate = _original.Date;
        EditStart = _original.StartTime;
        EditEnd = _original.EndTime;
        EditAllDay = _original.IsAllDay;
        EditLocation = _original.Location;
        EditShowAs = ShowAs.FirstOrDefault(c => c.Value == _original.ShowAs) ?? ShowAs[2];
        EditRequired = _original.Required;
        EditOptional = _original.Optional;
        EditBody = _original.Body;
        EditAddTeams = false;
        EditMessage = "";
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditMessage = "";
    }

    /// <summary>The form as typed.</summary>
    public MeetingForm Form => new()
    {
        Subject = EditSubject,
        Date = EditDate,
        StartTime = EditStart,
        EndTime = EditEnd,
        IsAllDay = EditAllDay,
        Location = EditLocation,
        ShowAs = EditShowAs.Value,
        Required = EditRequired,
        Optional = EditOptional,
        Body = EditBody,
        AddTeams = EditAddTeams && CanAddTeams,
    };

    /// <summary>The change to make, or null with <see cref="EditMessage"/> saying what is wrong.</summary>
    public CalendarEventChange? BuildChange(DateTimeOffset now)
    {
        var problem = MeetingEdit.TryBuild(Form, _original, Row.Event, now, out var change);
        EditMessage = problem ?? "";
        return change;
    }

    private static string Host(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        if (host.Contains("teams.", StringComparison.OrdinalIgnoreCase)) return "Teams ";
        if (host.Contains("zoom.", StringComparison.OrdinalIgnoreCase)) return "Zoom ";
        if (host.Contains("meet.google.", StringComparison.OrdinalIgnoreCase)) return "Google Meet ";
        if (host.Contains("webex.", StringComparison.OrdinalIgnoreCase)) return "Webex ";
        return "";
    }
}

/// <summary>One entry in the Show as list.</summary>
public sealed record ShowAsChoice(BusyStatus Value, string Label)
{
    public override string ToString() => Label;
}
