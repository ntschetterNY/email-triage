using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// One row in the triage list: a whole conversation, including your own sent
/// messages. Triage actions work on its Inbox messages; replies answer
/// <see cref="Summary"/>, the newest message from someone else.
/// </summary>
public sealed partial class MailRowViewModel : ObservableObject
{
    [ObservableProperty] private ConversationThread _thread;
    [ObservableProperty] private bool _isActionRequired;

    /// <summary>Set while a move or snooze animates the row out of the list.</summary>
    [ObservableProperty] private bool _isLeaving;

    /// <summary>Opened with the arrow (or Right): each message listed under the row.</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingMessages;

    /// <summary>
    /// The whole conversation, newest first, filled when the row is expanded.
    /// Unlike <see cref="Thread"/> this includes messages already filed away.
    /// </summary>
    public ObservableCollection<ConversationMessageViewModel> Messages { get; } = new();

    public MailRowViewModel(ConversationThread thread, bool isActionRequired)
    {
        _thread = thread;
        _isActionRequired = isActionRequired;
    }

    public string Key => Thread.Key;

    /// <summary>The newest message someone else sent: what replies, flags and forwards act on.</summary>
    public MailSummary Summary => Thread.LatestInbox;

    public IReadOnlyList<MailSummary> InboxMessages => Thread.InboxMessages;

    public string Subject => string.IsNullOrWhiteSpace(Thread.Latest.Subject)
        ? "(no subject)"
        : Thread.Latest.Subject;

    /// <summary>Who spoke last - "You" when it was your own follow-up.</summary>
    public string Sender => Thread.LatestIsMine ? "You" : Thread.Latest.DisplaySender;

    public bool LatestIsMine => Thread.LatestIsMine;

    public int Count => Thread.Count;

    public bool HasCount => Thread.Count > 1;

    public bool IsUnread => Thread.IsUnread;

    public bool HasAttachments => Thread.HasAttachments;

    /// <summary>
    /// Worth an expand arrow: several messages here, or a reply whose earlier
    /// messages may already be filed away.
    /// </summary>
    public bool CanExpand => Thread.Count > 1 ||
        !string.Equals(ConversationGrouper.StripPrefixes(Thread.Latest.Subject), Thread.Latest.Subject.Trim(), StringComparison.Ordinal);

    /// <summary>"INVITE", "CANCELLED", "ACCEPTED"... for meeting messages; empty for mail.</summary>
    public string KindTag => Summary.Kind switch
    {
        MailKind.MeetingRequest => "INVITE",
        MailKind.MeetingCancellation => "CANCELLED",
        MailKind.MeetingAccepted => "ACCEPTED",
        MailKind.MeetingTentative => "MAYBE",
        MailKind.MeetingDeclined => "DECLINED",
        _ => "",
    };

    public bool HasKindTag => KindTag.Length > 0;

    public string When => FormatWhen(Thread.LastActivityUtc.ToLocalTime());

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

    public void Refresh(ConversationThread thread)
    {
        Thread = thread;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(InboxMessages));
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(Sender));
        OnPropertyChanged(nameof(LatestIsMine));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasCount));
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(When));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(KindTag));
        OnPropertyChanged(nameof(HasKindTag));
    }
}
