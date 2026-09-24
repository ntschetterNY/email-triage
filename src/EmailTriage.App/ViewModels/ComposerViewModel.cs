using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

public enum RecipientField { None, To, Cc, Bcc }

/// <summary>
/// The inline reply and forward box. Outlook builds the draft - quoted history,
/// signature, recipients - and this collects the new text on top, plus any
/// edits to the To, Cc and Bcc lines, so mail looks the same as it would from
/// Outlook itself.
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly ContactDirectory _contacts;
    private readonly IScheduledSendRepository _scheduled;
    private readonly IClock _clock;
    private readonly SnoozeDayShape _dayShape;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _bodyText = "";
    [ObservableProperty] private string _toLine = "";
    [ObservableProperty] private string _ccLine = "";
    [ObservableProperty] private string _bccLine = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private ReplyDraft? _draft;

    // "Send later" row (Ctrl+Shift+Enter).
    [ObservableProperty] private bool _isScheduling;
    [ObservableProperty] private string _scheduleText = "";
    [ObservableProperty] private bool _holdIfReplied = true;

    [ObservableProperty] private RecipientField _suggestingFor;
    [ObservableProperty] private ContactEntry? _selectedSuggestion;

    // What Outlook filled in, so an untouched line is sent exactly as built
    // rather than re-resolved from text.
    private string _initialTo = "", _initialCc = "", _initialBcc = "";

    // Set while lines are filled in programmatically, so that is not taken as
    // the user typing a search.
    private bool _settingLines;

    public ComposerViewModel(
        IMailStore store, ContactDirectory contacts,
        IScheduledSendRepository scheduled, IClock clock, SnoozeDayShape dayShape)
    {
        _store = store;
        _contacts = contacts;
        _scheduled = scheduled;
        _clock = clock;
        _dayShape = dayShape;
    }

    /// <summary>When the typed time resolves to, or null while it does not parse.</summary>
    public DateTimeOffset? ScheduleAt =>
        NaturalDateParser.TryParse(ScheduleText, _clock.Now, out var when, _dayShape) && when > _clock.Now
            ? when
            : null;

    public string SchedulePreview => ScheduleText.Trim().Length == 0
        ? "e.g. tomorrow 9am, fri 2pm, 3d"
        : ScheduleAt is { } when ? when.ToLocalTime().ToString("ddd d MMM, HH:mm") : "not a time I understand";

    partial void OnScheduleTextChanged(string value)
    {
        OnPropertyChanged(nameof(ScheduleAt));
        OnPropertyChanged(nameof(SchedulePreview));
    }

    /// <summary>Shows or hides the "send later" row.</summary>
    public void ToggleSchedule()
    {
        IsScheduling = !IsScheduling;
        Status = "";
    }

    public ObservableCollection<ContactEntry> Suggestions { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    public string Header => Draft?.Scope switch
    {
        null => "",
        ReplyScope.All => "Reply all",
        ReplyScope.Forward => "Forward",
        _ => "Reply to sender",
    };

    /// <summary>A forward starts with no recipients, so focus goes to the To line.</summary>
    public bool IsForward => Draft?.Scope == ReplyScope.Forward;

    public string Subject => Draft?.Subject ?? "";

    /// <summary>Raised once a message is away or scheduled, with a line for the status bar.</summary>
    public event EventHandler<string>? Sent;

    /// <summary>Raised after a suggestion is taken, so the view can put the caret at the end.</summary>
    public event EventHandler<RecipientField>? SuggestionAccepted;

    public void Open(ReplyDraft draft)
    {
        Draft = draft;
        BodyText = "";
        Status = "";
        IsScheduling = false;
        ScheduleText = "";
        HoldIfReplied = true;

        _settingLines = true;
        ToLine = _initialTo = RecipientLine.Format(draft.To);
        CcLine = _initialCc = RecipientLine.Format(draft.Cc);
        BccLine = _initialBcc = "";
        _settingLines = false;
        CloseSuggestions();

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsForward));
        OnPropertyChanged(nameof(Subject));

        // Last, so the view sees the right IsForward when it picks what to focus.
        IsOpen = true;
    }

    // ---- autocomplete -------------------------------------------------------

    partial void OnToLineChanged(string value) => Suggest(RecipientField.To, value);
    partial void OnCcLineChanged(string value) => Suggest(RecipientField.Cc, value);
    partial void OnBccLineChanged(string value) => Suggest(RecipientField.Bcc, value);

    private void Suggest(RecipientField field, string line)
    {
        if (_settingLines) return;

        var matches = _contacts.Search(RecipientLine.CurrentToken(line));

        Suggestions.Clear();
        foreach (var m in matches) Suggestions.Add(m);

        SuggestingFor = matches.Count == 0 ? RecipientField.None : field;
        SelectedSuggestion = Suggestions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    public void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0) return;

        var index = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        SelectedSuggestion = Suggestions[Math.Clamp(index + delta, 0, Suggestions.Count - 1)];
    }

    public void AcceptSuggestion()
    {
        if (SelectedSuggestion is not { } pick || SuggestingFor == RecipientField.None) return;

        var field = SuggestingFor;

        _settingLines = true;
        switch (field)
        {
            case RecipientField.To: ToLine = RecipientLine.Accept(ToLine, pick); break;
            case RecipientField.Cc: CcLine = RecipientLine.Accept(CcLine, pick); break;
            case RecipientField.Bcc: BccLine = RecipientLine.Accept(BccLine, pick); break;
        }
        _settingLines = false;

        CloseSuggestions();
        SuggestionAccepted?.Invoke(this, field);
    }

    public void CloseSuggestions()
    {
        Suggestions.Clear();
        SuggestingFor = RecipientField.None;
        SelectedSuggestion = null;
        OnPropertyChanged(nameof(HasSuggestions));
    }

    // ---- send / discard -------------------------------------------------------

    public async Task SendAsync()
    {
        if (Draft is null || IsSending) return;

        CloseSuggestions();

        var to = RecipientLine.Parse(ToLine);
        var cc = RecipientLine.Parse(CcLine);
        var bcc = RecipientLine.Parse(BccLine);

        if (to.Count + cc.Count + bcc.Count == 0)
        {
            Status = "Who is this going to? Fill in the To line.";
            return;
        }

        // Forwarding with no note of your own is normal; an empty reply is not.
        if (!IsForward && string.IsNullOrWhiteSpace(BodyText))
        {
            Status = "Nothing to send - type a reply first.";
            return;
        }

        DateTimeOffset? sendAt = null;
        if (IsScheduling)
        {
            sendAt = ScheduleAt;
            if (sendAt is null)
            {
                Status = "When should it go? Type a time like \"tomorrow 9am\", or Ctrl+Shift+Enter to send now.";
                return;
            }
        }

        // Only the lines the user changed are rewritten; the rest keep the
        // exact recipients Outlook resolved when it built the draft.
        var overrides = new RecipientOverrides(
            ToLine != _initialTo ? to : null,
            CcLine != _initialCc ? cc : null,
            BccLine != _initialBcc ? bcc : null);
        var changes = overrides is { To: null, Cc: null, Bcc: null } ? null : overrides;

        IsSending = true;
        Status = sendAt is null ? "Sending..." : "Scheduling...";

        try
        {
            var html = string.IsNullOrWhiteSpace(BodyText)
                ? ""
                : HtmlPresenter.ComposeReplyFragment(BodyText);

            string done;
            if (sendAt is { } when)
            {
                var saved = await _store.SaveDraftForLaterAsync(Draft.Ref, html, changes).ConfigureAwait(true);
                await _scheduled.AddAsync(new ScheduledSend
                {
                    DraftEntryId = saved.EntryId,
                    DraftStoreId = saved.StoreId,
                    Subject = Draft.Subject,
                    Recipients = string.Join("; ", to.Concat(cc)),
                    SendAtUtc = when.ToUniversalTime(),
                    HoldIfReplied = HoldIfReplied,
                }).ConfigureAwait(true);

                done = $"Scheduled for {when.ToLocalTime():ddd d MMM HH:mm}"
                     + (HoldIfReplied ? " · held for review if they reply first" : "");
            }
            else
            {
                await _store.SendReplyAsync(Draft.Ref, html, changes).ConfigureAwait(true);
                done = "Sent";
            }

            Reset();
            Sent?.Invoke(this, done);
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
        Reset();

        try { await _store.DiscardDraftAsync(draft.Ref).ConfigureAwait(true); }
        catch { /* the draft is already gone; nothing to report */ }
    }

    private void Reset()
    {
        IsOpen = false;
        Draft = null;
        BodyText = "";
        Status = "";
        IsScheduling = false;
        ScheduleText = "";

        _settingLines = true;
        ToLine = CcLine = BccLine = "";
        _settingLines = false;
        CloseSuggestions();
    }
}
