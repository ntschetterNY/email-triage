using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.Core.Models;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// One message under an expanded conversation row, as in Outlook's
/// conversation view: click it (or arrow onto it) to read just that email,
/// with its own attachments - wherever it is filed.
/// </summary>
public sealed partial class ConversationMessageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isFocused;
    [ObservableProperty] private bool _isUnread;

    public ConversationMessageViewModel(MailRowViewModel row, MailSummary summary)
    {
        Row = row;
        Summary = summary;
        _isUnread = !summary.IsSent && summary.IsUnread;
    }

    /// <summary>The conversation row this message sits under.</summary>
    public MailRowViewModel Row { get; }

    public MailSummary Summary { get; }

    public string Sender => Summary.IsSent ? "You" : Summary.DisplaySender;

    public bool HasAttachments => Summary.HasAttachments;

    public string When => Summary.ReceivedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm");
}
