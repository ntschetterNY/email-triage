using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EmailTriage.App.Services;
using EmailTriage.Core.Abstractions;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.ViewModels;

/// <summary>A message away or scheduled: the status line, what it answered, and whether to archive that.</summary>
public sealed record SentEventArgs(string Message, MailRef InReplyTo, bool MarkDone)
{
    /// <summary>The follow-up to file against the answered mail, when one was set.</summary>
    public FollowUpRequest? FollowUp { get; init; }
}

/// <summary>Where suggestions are showing: a recipient line, or an @mention in the message.</summary>
public enum RecipientField { None, To, Cc, Bcc, Body, Subject, FollowUp, LinkLabel, LinkAddress }

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

    // "Send later" row (Ctrl+Shift+L).
    [ObservableProperty] private bool _isScheduling;
    [ObservableProperty] private string _scheduleText = "";
    [ObservableProperty] private bool _holdIfReplied = true;

    // Follow-up row (Ctrl+Shift+F): who owes what, by when. An empty date means none.
    [ObservableProperty] private bool _isFollowingUp;
    [ObservableProperty] private string _followUpWhen = "";
    [ObservableProperty] private string _followUpWho = "";
    [ObservableProperty] private string _followUpWhat = "";
    [ObservableProperty] private bool _trackAsTask = true;
    [ObservableProperty] private bool _mentionInEmail = true;

    // Who follows the first To recipient until the user types in it.
    private bool _followUpWhoEdited;
    private bool _settingWho;

    // Link row (Ctrl+K): the text shown and where it goes.
    [ObservableProperty] private bool _isLinking;
    [ObservableProperty] private string _linkLabel = "";
    [ObservableProperty] private string _linkAddress = "";

    [ObservableProperty] private RecipientField _suggestingFor;
    [ObservableProperty] private ContactEntry? _selectedSuggestion;

    // What Outlook filled in, so an untouched line is sent exactly as built
    // rather than re-resolved from text.
    private string _initialTo = "", _initialCc = "", _initialBcc = "";

    // Set while lines are filled in programmatically, so that is not taken as
    // the user typing a search.
    private bool _settingLines;

    // People @mentioned in the message so far, and the "@jan" being typed.
    private readonly List<ContactEntry> _mentions = new();
    private MentionText.Query? _mentionQuery;
    private int _mentionCaret;

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

    // ---- links ----------------------------------------------------------------

    // The part of the message the link replaces, noted when the row opens.
    private int _linkStart, _linkLength;

    /// <summary>
    /// Raised for Ctrl+K. The view knows the selection and the clipboard, so
    /// it answers with <see cref="StartLink"/>.
    /// </summary>
    public event EventHandler? LinkRequested;

    public void RequestLink()
    {
        if (!IsLinking) LinkRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the link row for the text selected in the message (or the caret,
    /// when nothing is). A web address already on the clipboard is filled in.
    /// </summary>
    public void StartLink(int selectionStart, int selectionLength, string? clipboard)
    {
        CloseSuggestions();
        _linkStart = Math.Clamp(selectionStart, 0, BodyText.Length);
        _linkLength = Math.Clamp(selectionLength, 0, BodyText.Length - _linkStart);

        var selected = BodyText.Substring(_linkStart, _linkLength);
        if (LinkText.LooksLikeAddress(selected))
        {
            LinkLabel = "";
            LinkAddress = selected.Trim();
        }
        else
        {
            LinkLabel = selected.Trim();
            LinkAddress = LinkText.LooksLikeAddress(clipboard) ? clipboard!.Trim() : "";
        }

        Status = "";
        IsLinking = true;
        RequestFocus(LinkLabel.Length == 0 && LinkAddress.Length == 0 ? RecipientField.LinkLabel : RecipientField.LinkAddress);
    }

    /// <summary>Writes the link into the message and goes back to typing after it.</summary>
    public void InsertLink()
    {
        if (!IsLinking) return;

        if (LinkText.Normalize(LinkAddress).Length == 0)
        {
            Status = "Where should the link go? Type or paste an address.";
            return;
        }

        (BodyText, BodyCaret) = LinkText.Insert(BodyText, _linkStart, _linkLength, LinkLabel, LinkAddress);
        IsLinking = false;
        LinkLabel = LinkAddress = "";
        Status = "";
        SuggestionAccepted?.Invoke(this, RecipientField.Body);
    }

    public void CancelLink()
    {
        IsLinking = false;
        LinkLabel = LinkAddress = "";
        Status = "";
        RequestFocus(RecipientField.Body);
    }

    // ---- follow-up ------------------------------------------------------------

    /// <summary>Shows or hides the follow-up row.</summary>
    public void ToggleFollowUp()
    {
        IsFollowingUp = !IsFollowingUp;
        Status = "";
    }

    public void ToggleTrackAsTask()
    {
        if (IsFollowingUp) TrackAsTask = !TrackAsTask;
    }

    public DateTimeOffset? FollowUpDue =>
        NaturalDateParser.TryParse(FollowUpWhen, _clock.Now, out var when, _dayShape) ? when : null;

    public bool HasFollowUp => IsFollowingUp && FollowUpWhen.Trim().Length > 0;

    /// <summary>
    /// "Friday 2 Oct 11:00": the time too, since a parked mail comes back
    /// then - so "tom 11am" visibly keeps its 11am.
    /// </summary>
    public string FollowUpWhenPreview => FollowUpWhen.Trim().Length == 0
        ? "e.g. fri, tom 11am, 14 oct"
        : FollowUpDue is { } d ? d.ToLocalTime().ToString("dddd d MMM HH:mm") : "not a date I understand";

    /// <summary>
    /// Who the name box means. People on the message come first, so a first
    /// name is usually enough; then the contact directory.
    /// </summary>
    public Recipient? FollowUpPerson
    {
        get
        {
            var onMessage = RecipientLine.Parse(ToLine).Concat(RecipientLine.Parse(CcLine))
                .Select(text => PersonResolver.Resolve(text, Array.Empty<Recipient>()))
                .Where(r => r is not null)
                .Select(r => r!.Value);

            var directory = FollowUpWho.Trim().Length == 0
                ? Enumerable.Empty<Recipient>()
                : _contacts.Search(FollowUpWho.Trim(), 5)
                    .Where(c => c.Address.Length > 0)
                    .Select(c => new Recipient(c.Name, c.Address));

            return PersonResolver.Resolve(FollowUpWho, onMessage.Concat(directory).ToList());
        }
    }

    public string FollowUpWhoPreview => FollowUpPerson switch
    {
        null => "who owes it?",
        { Address.Length: 0 } p => $"{p.Name} - no address found, so no chase mail",
        { } p when p.Name == p.Address => p.Address,
        { } p => $"{p.Name} <{p.Address}>",
    };

    partial void OnFollowUpWhenChanged(string value)
    {
        OnPropertyChanged(nameof(FollowUpDue));
        OnPropertyChanged(nameof(FollowUpWhenPreview));
    }

    partial void OnFollowUpWhoChanged(string value)
    {
        if (!_settingWho) _followUpWhoEdited = true;
        OnPropertyChanged(nameof(FollowUpPerson));
        OnPropertyChanged(nameof(FollowUpWhoPreview));
    }

    private void SetFollowUpWho(string value)
    {
        _settingWho = true;
        try { FollowUpWho = value; }
        finally { _settingWho = false; }
    }

    /// <summary>
    /// Until the user types a name of their own, the follow-up is on whoever
    /// the message goes to first.
    /// </summary>
    private void SyncFollowUpWho()
    {
        if (_followUpWhoEdited) return;

        var first = ToLine.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        SetFollowUpWho(first);
    }

    /// <summary>Adds someone to the To line unless they are on the message already.</summary>
    public void EnsureRecipient(string name, string address)
    {
        if (address.Length == 0 || IsOnMessage(new Recipient(name, address))) return;

        _settingLines = true;
        try { ToLine = RecipientLine.Append(ToLine, new ContactEntry(name, address, 0)); }
        finally { _settingLines = false; }
    }

    /// <summary>True when the person is on the To or Cc line, so the ask reaches them in this thread.</summary>
    private bool IsOnMessage(Recipient person) =>
        person.Address.Length > 0 &&
        RecipientLine.Parse(ToLine).Concat(RecipientLine.Parse(CcLine))
            .Any(a => string.Equals(a, person.Address, StringComparison.OrdinalIgnoreCase));

    private string FollowUpTask =>
        FollowUpWhat.Trim() is { Length: > 0 } what ? what : $"Reply about \"{Subject.Trim()}\"";

    /// <summary>The line put under the message so the recipient sees the ask.</summary>
    private string FollowUpLine(Recipient person, DateTimeOffset due)
    {
        static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var what = FollowUpWhat.Trim() is { Length: > 0 } w ? $" - {Enc(w)}" : "";
        return $"""<div style="font-family:Calibri,sans-serif;font-size:11pt"><b>Follow-up:</b> {Enc(person.Display)}{what} by <b>{due.ToLocalTime():dddd d MMM}</b></div><br>""";
    }

    private void ResetFollowUp()
    {
        IsFollowingUp = false;
        FollowUpWhen = FollowUpWhat = "";
        SetFollowUpWho("");
        _followUpWhoEdited = false;
        TrackAsTask = true;
        MentionInEmail = true;
    }

    public ObservableCollection<ContactEntry> Suggestions { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>
    /// The files going out: any the draft already carries (a forward's
    /// originals) and any dropped on the composer. Taking one off the list
    /// leaves it out of the message.
    /// </summary>
    public ObservableCollection<ComposeAttachment> Attachments { get; } = new();

    /// <summary>
    /// Adds dropped files, skipping any already on the list. Folders are left
    /// out: Outlook can only attach files.
    /// </summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        var skippedFolders = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { skippedFolders++; continue; }
            if (!File.Exists(path)) continue;
            if (Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            Attachments.Add(ComposeAttachment.FromFile(path, new FileInfo(path).Length));
        }

        Status = skippedFolders == 0 ? "" : "Folders can't be attached - drop the files inside, or zip it first.";
    }

    public void RemoveAttachment(ComposeAttachment attachment) => Attachments.Remove(attachment);

    public string Header => Draft?.Scope switch
    {
        null => "",
        ReplyScope.All => "Reply all",
        ReplyScope.Forward => "Forward",
        ReplyScope.New => "New message",
        _ => "Reply to sender",
    };

    /// <summary>A forward starts with no recipients, so focus goes to the To line.</summary>
    public bool IsForward => Draft?.Scope == ReplyScope.Forward;

    /// <summary>A new message: the subject is typed, not inherited.</summary>
    public bool IsNew => Draft?.Scope == ReplyScope.New;

    /// <summary>Forwards and new messages have nobody on them yet, so they open on the To line.</summary>
    public bool StartsWithRecipients => IsForward || IsNew;

    /// <summary>The subject line of a new message; replies keep Outlook's.</summary>
    [ObservableProperty] private string _subjectLine = "";

    public string Subject => IsNew ? SubjectLine : Draft?.Subject ?? "";

    // Sending with no subject asks once first, as Outlook does.
    private bool _noSubjectConfirmed;

    partial void OnSubjectLineChanged(string value) => _noSubjectConfirmed = false;

    /// <summary>Raised once a message is away or scheduled, with a line for the status bar.</summary>
    public event EventHandler<SentEventArgs>? Sent;

    /// <summary>Raised for Ctrl+Shift+O/C/B/M, so the view can move the caret there.</summary>
    public event EventHandler<RecipientField>? FocusRequested;

    public void RequestFocus(RecipientField field)
    {
        CloseSuggestions();
        FocusRequested?.Invoke(this, field);
    }

    /// <summary>Where the '@' of the mention being typed sits, so the view can drop suggestions beside it.</summary>
    public int MentionStart => _mentionQuery?.Start ?? 0;

    /// <summary>Where the caret belongs in the message after a mention is taken.</summary>
    public int BodyCaret { get; private set; }

    /// <summary>Raised after a suggestion or link is taken, so the view can put the caret after it.</summary>
    public event EventHandler<RecipientField>? SuggestionAccepted;

    public void Open(ReplyDraft draft)
    {
        Draft = draft;
        BodyText = "";
        Status = "";
        IsScheduling = false;
        ScheduleText = "";
        HoldIfReplied = true;
        IsLinking = false;
        LinkLabel = LinkAddress = "";
        ResetFollowUp();

        _settingLines = true;
        ToLine = _initialTo = RecipientLine.Format(draft.To);
        CcLine = _initialCc = RecipientLine.Format(draft.Cc);
        SubjectLine = draft.Subject;
        BccLine = _initialBcc = "";
        _settingLines = false;
        SyncFollowUpWho();
        _mentions.Clear();
        Attachments.Clear();
        foreach (var carried in draft.Attachments) Attachments.Add(ComposeAttachment.Carried(carried));
        CloseSuggestions();

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsForward));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(StartsWithRecipients));
        OnPropertyChanged(nameof(Subject));

        // Last, so the view sees the right IsForward when it picks what to focus.
        IsOpen = true;
    }

    // ---- autocomplete -------------------------------------------------------

    partial void OnToLineChanged(string value)
    {
        SyncFollowUpWho();
        Suggest(RecipientField.To, value);
    }

    partial void OnCcLineChanged(string value) => Suggest(RecipientField.Cc, value);
    partial void OnBccLineChanged(string value) => Suggest(RecipientField.Bcc, value);

    private void Suggest(RecipientField field, string line)
    {
        if (_settingLines) return;

        ShowSuggestions(field, _contacts.Search(RecipientLine.CurrentToken(line)));
    }

    /// <summary>
    /// Called as the message text or caret changes: an "@" starting a word
    /// searches contacts, the way Outlook's @mentions do.
    /// </summary>
    public void UpdateMentionSearch(string text, int caret)
    {
        if (_settingLines) return;

        var query = MentionText.Find(text, caret, _mentions);
        if (query is not { } q)
        {
            _mentionQuery = null;
            if (SuggestingFor == RecipientField.Body) CloseSuggestions();
            return;
        }

        _mentionQuery = q;
        _mentionCaret = caret;
        ShowSuggestions(RecipientField.Body, q.Text.Length == 0 ? _contacts.Frequent() : _contacts.Search(q.Text));
        OnPropertyChanged(nameof(MentionStart));
    }

    private void ShowSuggestions(RecipientField field, IReadOnlyList<ContactEntry> matches)
    {
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
            case RecipientField.Body: AcceptMention(pick); break;
        }
        _settingLines = false;

        CloseSuggestions();
        SuggestionAccepted?.Invoke(this, field);
    }

    /// <summary>
    /// Writes "@Jane Smith" into the message and, as Outlook does, puts her on
    /// the To line if she is not already getting it.
    /// </summary>
    private void AcceptMention(ContactEntry pick)
    {
        if (_mentionQuery is not { } query) return;

        (BodyText, BodyCaret) = MentionText.Insert(BodyText, query, _mentionCaret, pick);
        _mentionQuery = null;

        if (!_mentions.Any(m => string.Equals(m.Address, pick.Address, StringComparison.OrdinalIgnoreCase)))
            _mentions.Add(pick);

        if (!RecipientLine.Contains(ToLine, pick) && !RecipientLine.Contains(CcLine, pick) && !RecipientLine.Contains(BccLine, pick))
        {
            ToLine = RecipientLine.Append(ToLine, pick);
            Status = $"Added {pick.Display} to To";
        }
    }

    public void CloseSuggestions()
    {
        Suggestions.Clear();
        SuggestingFor = RecipientField.None;
        SelectedSuggestion = null;
        OnPropertyChanged(nameof(HasSuggestions));
    }

    // ---- send / discard -------------------------------------------------------

    /// <summary>
    /// Sends now, or at the time in the "send later" row when that is open.
    /// With <paramref name="markDone"/> the conversation is archived afterwards.
    /// </summary>
    public async Task SendAsync(bool markDone = false)
    {
        if (Draft is null || IsSending) return;

        // Sent with the link row still open: the link the user was writing goes in.
        if (IsLinking)
        {
            if (LinkText.Normalize(LinkAddress).Length > 0) InsertLink();
            else CancelLink();
        }

        CloseSuggestions();

        var to = RecipientLine.Parse(ToLine);
        var cc = RecipientLine.Parse(CcLine);
        var bcc = RecipientLine.Parse(BccLine);

        if (to.Count + cc.Count + bcc.Count == 0)
        {
            Status = "Who is this going to? Fill in the To line.";
            return;
        }

        // Forwarding with no note of your own is normal, as is sending just a
        // file; an empty reply is not.
        var hasFiles = Attachments.Any(a => a.Path is not null);
        if (IsNew)
        {
            if (string.IsNullOrWhiteSpace(SubjectLine) && string.IsNullOrWhiteSpace(BodyText) && !hasFiles)
            {
                Status = "Nothing to send - add a subject or a message.";
                return;
            }

            if (string.IsNullOrWhiteSpace(SubjectLine) && !_noSubjectConfirmed)
            {
                _noSubjectConfirmed = true;
                Status = "No subject - send again to send it anyway, or Ctrl+Shift+S to add one.";
                return;
            }
        }
        else if (!IsForward && string.IsNullOrWhiteSpace(BodyText) && !hasFiles)
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
                Status = "When should it go? Type a time like \"tomorrow 9am\", or Ctrl+Shift+L to close this and send now.";
                return;
            }
        }

        FollowUpRequest? followUp = null;
        var followUpLine = "";
        if (HasFollowUp)
        {
            // Refuse rather than silently drop a follow-up the user asked for.
            if (FollowUpDue is not { } due)
            {
                Status = "When is the follow-up? Type a date like \"fri\", or Ctrl+Shift+F to close the row.";
                return;
            }
            if (FollowUpPerson is not { } person)
            {
                Status = "Who is the follow-up for?";
                return;
            }

            if (MentionInEmail) followUpLine = FollowUpLine(person, due);
            if (TrackAsTask)
            {
                followUp = new FollowUpRequest
                {
                    Person = person,
                    Task = FollowUpTask,
                    DueUtc = due.ToUniversalTime(),
                    ToldUtc = (sendAt ?? _clock.Now).ToUniversalTime(),
                    Subject = IsNew ? SubjectLine.Trim() : Subject,
                    InThread = IsOnMessage(person),
                };
            }
        }

        // Only the lines the user changed are rewritten; the rest keep the
        // exact recipients Outlook resolved when it built the draft.
        var overrides = new RecipientOverrides(
            ToLine != _initialTo ? to : null,
            CcLine != _initialCc ? cc : null,
            BccLine != _initialBcc ? bcc : null)
        {
            Subject = IsNew ? SubjectLine.Trim() : null,
            Attachments = Attachments.Where(a => a.Path is not null).Select(a => a.Path!).ToList(),
            RemoveAttachments = Draft.Attachments
                .Where(c => !Attachments.Any(a => a.CarriedIndex == c.Index))
                .Select(c => c.Index)
                .ToList(),
        };
        var changes = overrides is { ChangesRecipients: false, Subject: null, Attachments.Count: 0, RemoveAttachments.Count: 0 }
            ? null
            : overrides;

        IsSending = true;
        Status = sendAt is null ? "Sending..." : "Scheduling...";

        try
        {
            var html = (string.IsNullOrWhiteSpace(BodyText)
                ? ""
                : HtmlPresenter.ComposeReplyFragment(BodyText, _mentions)) + followUpLine;

            string done;
            if (sendAt is { } when)
            {
                var saved = await _store.SaveDraftForLaterAsync(Draft.Ref, html, changes).ConfigureAwait(true);
                await _scheduled.AddAsync(new ScheduledSend
                {
                    DraftEntryId = saved.EntryId,
                    DraftStoreId = saved.StoreId,
                    Subject = Subject,
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

            var inReplyTo = Draft.InReplyTo;
            Reset();
            Sent?.Invoke(this, new SentEventArgs(done, inReplyTo, markDone) { FollowUp = followUp });
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
        IsLinking = false;
        LinkLabel = LinkAddress = "";
        ResetFollowUp();

        _settingLines = true;
        ToLine = CcLine = BccLine = SubjectLine = "";
        _settingLines = false;
        _mentions.Clear();
        Attachments.Clear();
        CloseSuggestions();
    }
}

/// <summary>
/// A file waiting to go out with the message being written: one dropped from
/// disk (<see cref="Path"/>), or one the draft already carries
/// (<see cref="CarriedIndex"/>, its position on the draft).
/// </summary>
public sealed record ComposeAttachment(string Name, long Size)
{
    public string? Path { get; init; }

    public int CarriedIndex { get; init; }

    public string SizeDisplay => MailAttachment.FormatSize(Size);

    /// <summary>Where it comes from, for the chip's tooltip.</summary>
    public string Where => Path ?? "From the message being forwarded";

    public static ComposeAttachment FromFile(string path, long size) =>
        new(System.IO.Path.GetFileName(path), size) { Path = path };

    public static ComposeAttachment Carried(MailAttachment attachment) =>
        new(attachment.Name, attachment.Size) { CarriedIndex = attachment.Index };
}
