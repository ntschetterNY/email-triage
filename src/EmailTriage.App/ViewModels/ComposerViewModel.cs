using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;

namespace EmailTriage.App.ViewModels;

/// <summary>
/// The inline reply box. Outlook builds the draft - quoted history, signature,
/// recipients - and this only collects the new text on top, so replies look the
/// same as they would from Outlook itself.
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly IMailStore _store;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _bodyText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private ReplyDraft? _draft;

    public ComposerViewModel(IMailStore store) => _store = store;

    public string Header => Draft is null
        ? ""
        : Draft.Scope == ReplyScope.All ? "Reply all" : "Reply to sender";

    public string Recipients => Draft?.RecipientSummary ?? "";

    public string Subject => Draft?.Subject ?? "";

    /// <summary>Raised once a reply is away, so the list can advance.</summary>
    public event EventHandler? Sent;

    public void Open(ReplyDraft draft)
    {
        Draft = draft;
        BodyText = "";
        Status = "";
        IsOpen = true;

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Recipients));
        OnPropertyChanged(nameof(Subject));
    }

    public async Task SendAsync()
    {
        if (Draft is null || IsSending) return;

        if (string.IsNullOrWhiteSpace(BodyText))
        {
            Status = "Nothing to send - type a reply first.";
            return;
        }

        IsSending = true;
        Status = "Sending...";

        try
        {
            var html = HtmlPresenter.ComposeReplyFragment(BodyText);
            await _store.SendReplyAsync(Draft.Ref, html).ConfigureAwait(true);

            IsOpen = false;
            Draft = null;
            BodyText = "";
            Status = "";

            Sent?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = $"Send failed: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    public async Task DiscardAsync()
    {
        if (Draft is null) { IsOpen = false; return; }

        var draft = Draft;

        IsOpen = false;
        Draft = null;
        BodyText = "";
        Status = "";

        try { await _store.DiscardDraftAsync(draft.Ref).ConfigureAwait(true); }
        catch { /* the draft is already gone; nothing to report */ }
    }
}
