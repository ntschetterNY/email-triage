using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;

namespace EmailTriage.App.ViewModels;

/// <summary>One row in the triage list.</summary>
public sealed partial class MailRowViewModel : ObservableObject
{
    [ObservableProperty] private MailSummary _summary;
    [ObservableProperty] private bool _isActionRequired;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set while a move or snooze animates the row out of the list.</summary>
    [ObservableProperty] private bool _isLeaving;

    public MailRowViewModel(MailSummary summary, bool isActionRequired)
    {
        _summary = summary;
        _isActionRequired = isActionRequired;
    }

    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject)
        ? "(no subject)"
        : Summary.Subject;

    public string Sender => Summary.DisplaySender;

    public bool IsUnread => Summary.IsUnread;

    public bool HasAttachments => Summary.HasAttachments;

    public string When => FormatWhen(Summary.ReceivedUtc.ToLocalTime());

    /// <summary>Relative for recent mail, absolute once it is older than a week.</summary>
    private static string FormatWhen(DateTimeOffset when)
    {
        var now = DateTimeOffset.Now;
        var age = now - when;

        if (age < TimeSpan.Zero) return when.ToString("HH:mm");
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (when.Date == now.Date) return when.ToString("HH:mm");
        if (when.Date == now.Date.AddDays(-1)) return "yesterday";
        if (age.TotalDays < 7) return when.ToString("ddd");
        if (when.Year == now.Year) return when.ToString("d MMM");
        return when.ToString("MMM yyyy");
    }

    public void Refresh(MailSummary summary)
    {
        Summary = summary;
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(Sender));
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(When));
        OnPropertyChanged(nameof(HasAttachments));
    }
}
