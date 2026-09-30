using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// The pop-up a meeting pill in the top bar opens: what the meeting is, who is
/// coming, and a Join button. Joining takes a second, deliberate click.
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
    [NotifyPropertyChangedFor(nameof(CanJoin), nameof(HasNoLink), nameof(JoinText))]
    private CalendarEventDetail? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLink), nameof(JoinText))]
    private bool _isLoading = true;

    public bool CanJoin => Detail?.JoinUrl is not null;

    public bool HasNoLink => !IsLoading && !CanJoin;

    /// <summary>"Join Teams meeting", or a word on why the button is not ready.</summary>
    public string JoinText => IsLoading ? "Finding the meeting link…"
        : Detail?.JoinUrl is { } url ? $"Join {Host(url)}meeting"
        : "No meeting link";

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
