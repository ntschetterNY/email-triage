using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EmailTriage.App.Input;
using EmailTriage.App.Services;
using EmailTriage.App.ViewModels;
using EmailTriage.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace EmailTriage.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool _webViewReady;
    private string _pendingHtml = "";

    /// <summary>The browser environment both mail views share, kept for printing.</summary>
    private CoreWebView2Environment? _webEnv;

    // The action board's email view: a second WebView2 on the same browser profile.
    private bool _actionViewReady;
    private string _pendingActionHtml = "";

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
        Title = $"Email Triage {AppUpdater.DisplayVersion}";

        HelpList.ItemsSource = viewModel.HelpRows
            .Select(r => new { r.Group, r.Keys, r.Description })
            .ToList();

        HintStrip.ItemsSource = BuildHints();

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.PrintRequested += async (_, _) => await PrintAsync();
        viewModel.SettingsRequested += (_, _) => ShowSettings();
        LavishLayer.Attach(viewModel.Lavish, viewModel.Keys, AppRoot, LavishButton,
            area: () => ViewModel.Section switch
            {
                Section.Actions => "Action items",
                Section.Calendar => "Calendar",
                _ => "Triage",
            },
            // The reading panes are pictures of mail while Lavish is on; never described.
            privateKind: el => ReferenceEquals(el, BodySnapshot) || ReferenceEquals(el, ActionBodySnapshot) ? "Reading pane" : null);
        viewModel.Lavish.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LavishViewModel.IsAnnotating)) _ = UpdateAirspaceAsync();
        };
        viewModel.Triage.PropertyChanged += OnTriageChanged;
        viewModel.Triage.Palette.PropertyChanged += OnPaletteChanged;
        viewModel.Triage.Capture.PropertyChanged += OnCaptureChanged;
        viewModel.Triage.Capture.NotesFocusRequested += (_, _) => FocusLater(CaptureNotes);
        viewModel.Triage.Composer.PropertyChanged += OnComposerChanged;
        viewModel.Triage.Composer.SuggestionAccepted += OnSuggestionAccepted;
        viewModel.Triage.Composer.FocusRequested += (_, field) => FocusLater(field switch
        {
            RecipientField.To => ToBox,
            RecipientField.Cc => CcBox,
            RecipientField.Bcc => BccBox,
            RecipientField.Subject => SubjectBox,
            RecipientField.FollowUp => FollowUpBox,
            RecipientField.LinkLabel => LinkLabelBox,
            RecipientField.LinkAddress => LinkAddressBox,
            _ => ComposerBox,
        });
        viewModel.Triage.Composer.LinkRequested += (_, _) => viewModel.Triage.Composer.StartLink(
            ComposerBox.SelectionStart, ComposerBox.SelectionLength, ClipboardText());

        // Suggestions belong to the line being typed in; moving elsewhere drops them.
        foreach (var box in new[] { ToBox, CcBox, BccBox, SubjectBox, ComposerBox, FollowUpBox })
            box.GotKeyboardFocus += (_, _) => viewModel.Triage.Composer.CloseSuggestions();

        // "@" in the message searches contacts. Text and caret both matter:
        // clicking or arrowing away from a mention ends the search.
        ComposerBox.TextChanged += (_, _) => UpdateMentionSearch();
        ComposerBox.SelectionChanged += (_, _) => UpdateMentionSearch();
        viewModel.Actions.PropertyChanged += OnActionsChanged;
        viewModel.Actions.FocusRequested += OnFocusRequested;
        viewModel.Calendar.PropertyChanged += OnCalendarChanged;
        viewModel.Calendar.RangeChanged += (_, _) => ScrollCalendarToWorkday();

        // The grid's day headings sit outside its scroller; leave room for its scroll bar so the columns line up.
        var scrollBar = new Thickness(0, 0, SystemParameters.VerticalScrollBarWidth, 0);
        CalendarHeader.Margin = scrollBar;
        CalendarAllDay.Margin = scrollBar;
        // Also when the tab or view first shows it: a hidden grid cannot be scrolled.
        CalendarScroll.IsVisibleChanged += (_, e) => { if (e.NewValue is true) ScrollCalendarToWorkday(); };

        Loaded += async (_, _) => await InitialiseWebViewAsync();
    }

    /// <summary>
    /// The key hints in the status bar, for whichever tab is showing. Read
    /// from the keymap, so a rebinding in keybindings.json shows up here too.
    /// </summary>
    private IReadOnlyList<object> BuildHints()
    {
        var keys = ViewModel.Keys;
        object Hint(string label, params TriageAction[] actions) => new
        {
            Key = string.Join(" ", actions.Select(keys.Describe).Where(k => k.Length > 0)),
            Label = label,
        };

        // The first of several actions that has a key, for commands that moved.
        object HintFirst(string label, params TriageAction[] actions) => new
        {
            Key = actions.Select(keys.Describe).FirstOrDefault(k => k.Length > 0) ?? "",
            Label = label,
        };

        return ViewModel.Section switch
        {
            Section.Triage => new[]
            {
                Hint("next/prev", TriageAction.NextMail, TriageAction.PrevMail),
                Hint("expand", TriageAction.NextColumn, TriageAction.PrevColumn),
                Hint("action", TriageAction.MarkActionRequired),
                Hint("no action", TriageAction.MarkNoAction),
                Hint("move", TriageAction.MoveToFolder),
                Hint("archive", TriageAction.Archive),
                Hint("later", TriageAction.Snooze),
                Hint("schedule", TriageAction.ScheduleTime),
                Hint("meeting", TriageAction.ReplyWithMeeting),
                // Reply all lives on Enter (Confirm) in the Superhuman layout.
                HintFirst("reply all", TriageAction.ReplyAll, TriageAction.Confirm),
                Hint("reply", TriageAction.ReplySender),
                Hint("forward", TriageAction.Forward),
                Hint("attachment", TriageAction.OpenAttachment),
                Hint("read", TriageAction.ToggleRead),
                Hint("search", TriageAction.Search),
                Hint("ask AI", TriageAction.AiSearch),
                Hint("AI draft", TriageAction.AiDraftReply),
                Hint("undo", TriageAction.Undo),
                Hint("help", TriageAction.ShowHelp),
            },
            Section.Calendar => new[]
            {
                new
                {
                    Key = $"{keys.Describe(TriageAction.CalendarDay)}–{keys.Describe(TriageAction.CalendarAgenda)}",
                    Label = "view",
                },
                Hint("prev/next", TriageAction.PrevColumn, TriageAction.NextColumn),
                Hint("today", TriageAction.FirstMail),
                Hint("next/prev meeting", TriageAction.NextMail, TriageAction.PrevMail),
                Hint("join / open", TriageAction.Confirm),
                Hint("outlook", TriageAction.OpenInOutlook),
                Hint("answer", TriageAction.Rsvp),
                Hint("join now", TriageAction.JoinMeeting),
                Hint("undo", TriageAction.Undo),
                Hint("refresh", TriageAction.Refresh),
                Hint("help", TriageAction.ShowHelp),
            },
            _ => new[]
            {
                Hint("column", TriageAction.PrevColumn, TriageAction.NextColumn),
                Hint("card", TriageAction.NextMail, TriageAction.PrevMail),
                Hint("stage", TriageAction.StageBack, TriageAction.StageForward),
                Hint("by person", TriageAction.ToggleBoardView),
                Hint("reply all", TriageAction.Confirm),
                Hint("reply", TriageAction.ReplySender),
                Hint("forward", TriageAction.Forward),
                Hint("blocked by", TriageAction.AddBlocker),
                Hint("assign", TriageAction.AddAssignment),
                Hint("clear", TriageAction.ClearWait),
                Hint("chase", TriageAction.Chase),
                Hint("due", TriageAction.SetDue),
                Hint("schedule", TriageAction.ScheduleTime),
                Hint("notes", TriageAction.AddNote),
                Hint("done", TriageAction.ToggleComplete),
                Hint("priority", TriageAction.CyclePriority),
                Hint("outlook", TriageAction.OpenInOutlook),
                Hint("help", TriageAction.ShowHelp),
            },
        };
    }

    // ---- WebView2 ---------------------------------------------------------

    private async Task InitialiseWebViewAsync()
    {
        try
        {
            // Keep the browser profile out of the install directory, which may
            // be read-only under Program Files.
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmailTriage", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(BundledWebView2Folder(), userData);
            _webEnv = env;

            await ConfigureMailViewAsync(BodyView, env);
            _webViewReady = true;
            if (_pendingHtml.Length > 0) BodyView.CoreWebView2.NavigateToString(_pendingHtml);

            await ConfigureMailViewAsync(ActionBodyView, env);
            _actionViewReady = true;
            RenderActionBody(_pendingActionHtml.Length > 0 ? _pendingActionHtml : ViewModel.Actions.BodyHtml);
        }
        catch (Exception ex)
        {
            // Without the WebView2 runtime the app is still usable for triage;
            // only the reading pane is lost, so say so rather than crashing.
            ViewModel.Triage.Status =
                $"Message preview unavailable (WebView2 runtime missing?): {ex.Message}";
        }
    }

    /// <summary>
    /// The WebView2 runtime is normally already on the machine (Windows 11 and
    /// Microsoft 365 both ship it). Where it isn't and can't be installed, a
    /// "Fixed Version" copy of it can be dropped into a WebView2Runtime folder
    /// next to EmailTriage.exe; this returns that folder so it gets used.
    /// Null means "use whatever Windows has", which is the usual case.
    /// </summary>
    private static string? BundledWebView2Folder()
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (installDir is null) return null;

        var bundled = Path.Combine(installDir, "WebView2Runtime");
        if (!File.Exists(Path.Combine(bundled, "msedgewebview2.exe"))) return null;

        // Prefer the system runtime when present: it is kept current by
        // Windows Update, whereas the bundled copy only changes with the app.
        try
        {
            if (!string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
                return null;
        }
        catch (WebView2RuntimeNotFoundException) { }

        return bundled;
    }

    // ---- printing a conversation -------------------------------------------

    private async void OnPrint(object sender, RoutedEventArgs e) => await PrintAsync();

    /// <summary>
    /// The Print button and Ctrl+P: asks for a printer, renders the
    /// conversation for paper and prints it from a browser nobody sees, so
    /// the reading pane stays as it is.
    /// </summary>
    private async Task PrintAsync()
    {
        var triage = ViewModel.Triage;
        if (_webEnv is null)
        {
            triage.Status = "Printing needs the WebView2 runtime, which is not available";
            return;
        }

        string? html;
        try
        {
            triage.Status = "Preparing to print...";
            html = await triage.RenderSelectedForPrintAsync();
        }
        catch (Exception ex)
        {
            triage.Status = $"Could not read the conversation to print: {ex.Message}";
            return;
        }

        if (html is null)
        {
            triage.Status = "Select a conversation to print it";
            return;
        }

        // Windows' own dialog picks the printer and the copies; the browser
        // then prints straight to it. "Microsoft Print to PDF" still saves a PDF.
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        if (dialog.ShowDialog() != true)
        {
            triage.Status = "";
            return;
        }

        CoreWebView2Controller? controller = null;
        try
        {
            triage.Status = "Printing...";
            controller = await _webEnv.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(this).Handle);
            controller.IsVisible = false;

            var core = controller.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping(
                MailImages.InlineImageHost, EmailTriage.Outlook.OutlookMailStore.DefaultInlineImageFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Only the page itself loads; a link or refresh in the mail goes nowhere.
            var loaded = new TaskCompletionSource<bool>();
            var navigations = 0;
            core.NavigationStarting += (_, e) => { if (++navigations > 1) e.Cancel = true; };
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.NavigateToString(html);
            if (!await loaded.Task) throw new InvalidOperationException("the page did not load");

            var settings = _webEnv.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.PrinterName = dialog.PrintQueue.FullName;
            settings.Copies = Math.Max(1, dialog.PrintTicket.CopyCount ?? 1);
            if (dialog.PrintTicket.PageOrientation == System.Printing.PageOrientation.Landscape)
                settings.Orientation = CoreWebView2PrintOrientation.Landscape;

            var status = await core.PrintAsync(settings);
            triage.Status = status switch
            {
                CoreWebView2PrintStatus.Succeeded => $"Sent to {dialog.PrintQueue.Name}",
                CoreWebView2PrintStatus.PrinterUnavailable => $"{dialog.PrintQueue.Name} is not available",
                _ => "Could not print the conversation",
            };
        }
        catch (Exception ex)
        {
            triage.Status = $"Could not print: {ex.Message}";
        }
        finally
        {
            controller?.Close();
        }
    }

    /// <summary>
    /// Locks a WebView2 down to rendering mail: no scripts from the mail, links
    /// to the real browser, embedded images from the local cache, and the
    /// app's shortcut keys still working while it has focus.
    /// </summary>
    private async Task ConfigureMailViewAsync(Microsoft.Web.WebView2.Wpf.WebView2 view, CoreWebView2Environment env)
    {
        await view.EnsureCoreWebView2Async(env);

        var core = view.CoreWebView2;

        // Embedded (cid:) images are saved here by the mail store and served
        // under a private host name; see HtmlPresenter.ResolveInlineImages.
        var inlineImages = EmailTriage.Outlook.OutlookMailStore.DefaultInlineImageFolder;
        Directory.CreateDirectory(inlineImages);
        core.SetVirtualHostNameToFolderMapping(
            EmailTriage.Core.Services.MailImages.InlineImageHost, inlineImages, CoreWebView2HostResourceAccessKind.DenyCors);

        // The pane renders mail, nothing more: no devtools, no context menu,
        // no downloads initiated by message content.
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // Links open in the real browser rather than hijacking the pane.
        // Attachment previews are served from the local cache under a private host.
        var attachments = EmailTriage.Outlook.OutlookMailStore.DefaultAttachmentFolder;
        Directory.CreateDirectory(attachments);
        core.SetVirtualHostNameToFolderMapping(
            AttachmentHost, attachments, CoreWebView2HostResourceAccessKind.DenyCors);

        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            if (e.Uri.StartsWith($"https://{AttachmentHost}/", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternally(e.Uri);
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };

        // Keys typed into the pane go to the browser, not to WPF. Forward
        // the bound ones so shortcuts work wherever focus is. Injected
        // scripts are exempt from the page CSP; mail's own scripts are not.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildKeyForwardingScript());
        core.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>
    /// Chords with Ctrl or Alt, and function keys, already reach WPF through
    /// WebView2's accelerator-key routing, so only the plain ones are forwarded.
    /// Everything else (Space, Ctrl+C, ...) keeps its browser meaning.
    /// </summary>
    private string BuildKeyForwardingScript()
    {
        var chords = ViewModel.Keys.Bindings.Keys
            .Where(s => (s.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
            .Where(s => s.Key is not (>= Key.F1 and <= Key.F24))
            .Select(s => $"{KeyInterop.VirtualKeyFromKey(s.Key)}:{(s.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0)}")
            .Distinct();

        return $$"""
            (() => {
              const bound = new Set({{JsonSerializer.Serialize(chords)}});
              addEventListener('keydown', e => {
                if (e.ctrlKey || e.altKey || e.metaKey || e.isComposing) return;
                if (!bound.has(e.keyCode + ':' + (e.shiftKey ? 1 : 0))) return;
                e.preventDefault();
                e.stopImmediatePropagation();
                chrome.webview.postMessage({ vk: e.keyCode, shift: e.shiftKey });
              }, true);
            })();
            """;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        KeyStroke stroke;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var key = KeyInterop.KeyFromVirtualKey(root.GetProperty("vk").GetInt32());
            var mods = root.GetProperty("shift").GetBoolean() ? ModifierKeys.Shift : ModifierKeys.None;
            stroke = new KeyStroke(key, mods);
        }
        catch { return; }

        // Only ever act on chords the keymap actually binds.
        if (ViewModel.Keys.Resolve(stroke) == TriageAction.None) return;

        // Take focus back from the browser so what follows (typing into the
        // palette, Esc, the next shortcut) lands in WPF.
        Focus();
        await DispatchKeyAsync(stroke);
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;

        // Only hand the shell things that are actually web or mail links.
        if (parsed.Scheme is not ("http" or "https" or "mailto")) return;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* no default handler; nothing useful to do */ }
    }

    private void RenderBody(string html)
    {
        if (!_webViewReady || BodyView.CoreWebView2 is null)
        {
            _pendingHtml = html;
            return;
        }

        BodyView.CoreWebView2.NavigateToString(
            string.IsNullOrEmpty(html) ? "<html><body></body></html>" : html);
    }

    private void RenderActionBody(string html)
    {
        if (!_actionViewReady || ActionBodyView.CoreWebView2 is null)
        {
            _pendingActionHtml = html;
            return;
        }

        ActionBodyView.CoreWebView2.NavigateToString(
            string.IsNullOrEmpty(html) ? "<html><body style='background:#16181d'></body></html>" : html);
    }

    // ---- action form and report ----------------------------------------------

    private async void OnFormDueSave(object sender, RoutedEventArgs e) => await SubmitFormAsync("form:due");
    private async void OnFormBlockerAdd(object sender, RoutedEventArgs e) => await SubmitFormAsync("form:blocker");
    private async void OnFormAssignAdd(object sender, RoutedEventArgs e) => await SubmitFormAsync("form:assign");
    private async void OnFormNotesSave(object sender, RoutedEventArgs e) => await SubmitFormAsync("form:notes");

    private async Task SubmitFormAsync(string form)
    {
        var saved = form switch
        {
            "form:due" => await ViewModel.Actions.SaveDueFromFormAsync(),
            "form:blocker" => await ViewModel.Actions.AddBlockerFromFormAsync(),
            "form:assign" => await ViewModel.Actions.AddAssignmentFromFormAsync(),
            "form:notes" => await ViewModel.Actions.SaveNotesFromFormAsync(),
            _ => false,
        };

        // Saved: back to the board, so its keys (chase, Ctrl+G) work at once.
        // Not saved: stay in the field to fix what the status line says.
        if (saved) Focus();
    }

    /// <summary>The form a focused field belongs to, from the Tag on it or an ancestor.</summary>
    private string? FormOf(DependencyObject? element)
    {
        for (var e = element; e is not null; e = System.Windows.Media.VisualTreeHelper.GetParent(e))
        {
            if (e is FrameworkElement { Tag: string tag } && tag.StartsWith("form:", StringComparison.Ordinal)) return tag;
            if (ReferenceEquals(e, ActionForm)) return null;
        }
        return null;
    }

    /// <summary>
    /// While typing in the action form, keys are text: Enter saves that form
    /// (Ctrl+Enter for the multi-line notes), Esc leaves the field, and
    /// nothing else is taken as a shortcut - except Ctrl+G, which a text box
    /// has no use for, so Claude can draft straight from the form. Decided
    /// synchronously, so the key is marked handled before the field can also
    /// act on it.
    /// </summary>
    /// <returns>True when the form owns the key; <paramref name="submit"/> names a form to save.</returns>
    private bool TryHandleFormKey(KeyEventArgs e, out string? submit)
    {
        submit = null;
        if (Keyboard.FocusedElement is not DependencyObject focused || FormOf(focused) is not { } form) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (key == Key.Escape)
        {
            e.Handled = true;
            Focus();
            return true;
        }

        if (key == Key.Return && (form != "form:notes" || ctrl))
        {
            e.Handled = true;

            // An open drop-down gets Enter first, to take the highlighted name.
            if (FindParent<ComboBox>(focused) is { IsDropDownOpen: true } combo) combo.IsDropDownOpen = false;
            else submit = form;
            return true;
        }

        if (ctrl && ViewModel.Keys.Resolve(KeyStroke.FromEvent(e)) == TriageAction.AiDraftReply) return false;

        return true; // the field has it; not a shortcut
    }

    private static T? FindParent<T>(DependencyObject? e) where T : DependencyObject
    {
        for (; e is not null; e = System.Windows.Media.VisualTreeHelper.GetParent(e))
            if (e is T match) return match;
        return null;
    }

    private void OnFocusRequested(object? sender, FormField field)
    {
        Control target = field switch
        {
            FormField.Blocker => FormBlockerWhat,
            FormField.Assignment => FormAssignWho,
            FormField.Notes => FormNotes,
            _ => FormDue,
        };

        ViewModel.Actions.IsByPerson = false;
        target.BringIntoView();
        FocusLater(target);
    }

    private void OnShowAllMail(object sender, RoutedEventArgs e) { ViewModel.Triage.ShowUnreadOnly = false; Focus(); }
    private void OnShowUnreadMail(object sender, RoutedEventArgs e) { ViewModel.Triage.ShowUnreadOnly = true; Focus(); }
    private async void OnShowBoard(object sender, RoutedEventArgs e) { ViewModel.Actions.IsByPerson = false; await ViewModel.Actions.ShowDoneLogAsync(false); Focus(); }
    private void OnShowByPerson(object sender, RoutedEventArgs e) { ViewModel.Actions.IsByPerson = true; Focus(); }
    private async void OnShowDoneLog(object sender, RoutedEventArgs e) { await ViewModel.Actions.ShowDoneLogAsync(true); Focus(); }
    private void OnReportExport(object sender, RoutedEventArgs e) { ViewModel.Actions.ExportReport(); Focus(); }
    private async void OnReportEmail(object sender, RoutedEventArgs e) { await ViewModel.Actions.EmailReportAsync(); Focus(); }

    private void OnReportRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.ActionItem item)
            ViewModel.Actions.OpenFromReport(item);
        Focus();
    }

    private async void OnComposeClick(object sender, RoutedEventArgs e) => await ViewModel.ComposeAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnLavishButtonClick(object sender, RoutedEventArgs e) => LavishLayer.Toggle();

    private void ShowSettings()
    {
        new SettingsWindow(ViewModel.CreateSettings(), ViewModel.Lavish, ViewModel.Keys) { Owner = this }.ShowDialog();
    }

    // ---- calendar ------------------------------------------------------------

    /// <summary>
    /// A meeting pill in the top bar: opens the meeting's card under it. It
    /// never joins by itself - that is the card's Join button, a second click.
    /// </summary>
    private void OnPillClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MeetingPill { Event: { } ev } } pill) return;

        var card = ViewModel.Calendar.OpenCard(ev);
        new MeetingWindow(ViewModel, card) { Owner = this }.ShowUnder(pill);
    }

    // Keep the keyboard on the window, where the calendar keys live.
    private async void OnAgendaClick(object sender, MouseButtonEventArgs e)
    {
        Focus();

        // Only a click on a meeting row - not the scroll bar or a day heading.
        var item = ItemsControl.ContainerFromElement(AgendaList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is AgendaRow row) await ViewModel.Calendar.ClickAsync(row.Event);
    }

    private async void OnAgendaJoin(object sender, RoutedEventArgs e) { await ViewModel.Calendar.ActivateAsync(); Focus(); }
    private async void OnAgendaOpen(object sender, RoutedEventArgs e) { await ViewModel.Calendar.OpenInOutlookAsync(); Focus(); }
    private void OnAgendaAnswer(object sender, RoutedEventArgs e) => ViewModel.AnswerSelectedMeeting();

    private void OnCalendarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalendarViewModel.Selected)) return;

        Dispatcher.BeginInvoke(() =>
        {
            var calendar = ViewModel.Calendar;
            if (calendar.IsAgenda)
            {
                if (calendar.AgendaSelected is { } row) AgendaList.ScrollIntoView(row);
            }
            else if (calendar.IsTimeGrid) ScrollToSelectedBlock();
        });
    }

    /// <summary>Brings a meeting reached with j or k into view, with a little of the hour before it.</summary>
    private void ScrollToSelectedBlock()
    {
        if (ViewModel.Calendar.SelectedBlock is not { } block) return;

        var top = CalendarScroll.VerticalOffset;
        var bottom = top + CalendarScroll.ViewportHeight;
        if (block.Top < top || block.Top + block.Height > bottom)
            CalendarScroll.ScrollToVerticalOffset(Math.Max(0, block.Top - CalendarViewModel.HourHeight / 2));
    }

    /// <summary>
    /// The grid opens on the working day rather than midnight - unless the
    /// selected meeting (one opened from the strip, say) is outside it.
    /// </summary>
    private void ScrollCalendarToWorkday()
    {
        if (!ViewModel.Calendar.IsTimeGrid) return;

        Dispatcher.BeginInvoke(() =>
        {
            CalendarScroll.ScrollToVerticalOffset(ViewModel.Calendar.WorkdayTop);
            CalendarScroll.UpdateLayout();
            ScrollToSelectedBlock();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCalendarViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string name && Enum.TryParse<CalendarView>(name, out var view))
            ViewModel.Calendar.SetView(view);
        Focus();
    }

    private void OnCalendarPrev(object sender, RoutedEventArgs e) { ViewModel.Calendar.Step(-1); Focus(); }
    private void OnCalendarNext(object sender, RoutedEventArgs e) { ViewModel.Calendar.Step(1); Focus(); }
    private void OnCalendarToday(object sender, RoutedEventArgs e) { ViewModel.Calendar.GoToToday(); Focus(); }

    private void OnCalendarNew(object sender, RoutedEventArgs e) => ViewModel.NewCalendarEntry();

    /// <summary>A meeting in the grid, the all-day row or a month cell: show it, joining it if it is on.</summary>
    /// <summary>
    /// A meeting in the grid, a month cell or the all-day row. The first click
    /// selects it (and joins one about to start); a click on the meeting
    /// already selected - or a double-click - expands it into its card.
    /// </summary>
    private async void OnCalendarItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarItem item } element) return;

        e.Handled = true; // not also the month day underneath
        if (item.IsSelected)
        {
            var card = ViewModel.Calendar.OpenCard(item.Event);
            new MeetingWindow(ViewModel, card) { Owner = this }.ShowUnder(element);
            return;
        }

        Focus();
        await ViewModel.Calendar.ClickAsync(item.Event);
    }

    /// <summary>
    /// A double-click on an empty stretch of a day column: a new entry there,
    /// starting on the half hour clicked and running 30 minutes.
    /// </summary>
    private void OnCalendarColumnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: CalendarDayColumn day } column) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is CalendarBlock) return; // on a meeting, not a gap

        e.Handled = true;
        var halfHours = Math.Clamp((int)(e.GetPosition(column).Y / (CalendarViewModel.HourHeight / 2)), 0, 47);
        var start = new DateTimeOffset(DateTime.SpecifyKind(day.Date.Date.AddMinutes(halfHours * 30), DateTimeKind.Local));
        ViewModel.NewCalendarEntryAt(start);
    }

    /// <summary>A month day, away from its meetings: open it in the Day view.</summary>
    private void OnMonthDayClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonthCell cell) ViewModel.Calendar.ShowDay(cell.Date);
        Focus();
    }

    // ---- action board clicks -----------------------------------------------

    private Point _cardPressedAt;
    private EmailTriage.Core.Models.ActionItem? _cardPressed;

    private void OnBoardCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _cardPressed = (sender as FrameworkElement)?.DataContext as EmailTriage.Core.Models.ActionItem;
        _cardPressedAt = e.GetPosition(this);
    }

    /// <summary>Starts a drag once the mouse has moved far enough to not be a click.</summary>
    private void OnBoardCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_cardPressed is not { } item || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - _cardPressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _cardPressed = null;
        ViewModel.Actions.Select(item);
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(EmailTriage.Core.Models.ActionItem), item), DragDropEffects.Move);
        foreach (var column in ViewModel.Actions.Columns) column.IsDropTarget = false;
    }

    private void OnBoardCardClick(object sender, MouseButtonEventArgs e)
    {
        _cardPressed = null;
        if ((sender as FrameworkElement)?.DataContext is EmailTriage.Core.Models.ActionItem item)
            ViewModel.Actions.Select(item);
        Focus();
    }

    private void OnColumnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(typeof(EmailTriage.Core.Models.ActionItem));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && (sender as FrameworkElement)?.Tag is BoardColumn column)
        {
            if (column.IsFollowUps) e.Effects = DragDropEffects.None;
            foreach (var c in ViewModel.Actions.Columns) c.IsDropTarget = c == column && !column.IsFollowUps;
        }
        e.Handled = true;
    }

    private void OnColumnDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BoardColumn column) column.IsDropTarget = false;
    }

    private async void OnColumnDrop(object sender, DragEventArgs e)
    {
        foreach (var c in ViewModel.Actions.Columns) c.IsDropTarget = false;

        if ((sender as FrameworkElement)?.Tag is BoardColumn column &&
            e.Data.GetData(typeof(EmailTriage.Core.Models.ActionItem)) is EmailTriage.Core.Models.ActionItem item)
        {
            await ViewModel.Actions.MoveToColumnAsync(item, column);
        }
        Focus();
    }

    private async void OnWaitingChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string person)
            await ViewModel.Actions.FilterToAsync(person);
        Focus();
    }

    private async void OnBlockerCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.BlockingTask blocker)
            await ViewModel.Actions.ToggleBlockerAsync(blocker);
        Focus();
    }

    private async void OnAssignmentCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.Assignment assignment)
            await ViewModel.Actions.ToggleAssignmentAsync(assignment);
        Focus();
    }

    // ---- airspace ----------------------------------------------------------

    private int _airspaceVersion;

    private bool IsOverlayOpen =>
        ViewModel.Triage.Palette.IsOpen
        || ViewModel.Triage.Composer.IsOpen
        || ViewModel.Triage.Capture.IsOpen
        || ViewModel.Actions.Editor != EditorMode.None
        || ViewModel.Actions.IsReviewing
        || ViewModel.IsHelpVisible
        || ViewModel.Lavish.IsAnnotating;

    /// <summary>
    /// WebView2 is a native child window, and WPF cannot draw over one: an
    /// overlay that crosses the reading pane is simply hidden behind the mail.
    /// While an overlay is open, the live view is swapped for a still image of
    /// itself, which WPF can dim and cover like anything else.
    /// </summary>
    private async Task UpdateAirspaceAsync()
    {
        var version = ++_airspaceVersion;
        await CoverAsync(BodyView, BodySnapshot, _webViewReady, version);
        await CoverAsync(ActionBodyView, ActionBodySnapshot, _actionViewReady, version);
    }

    private async Task CoverAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 view, Image snapshot, bool ready, int version)
    {
        if (!IsOverlayOpen)
        {
            view.Visibility = Visibility.Visible;
            snapshot.Source = null;
            return;
        }

        if (view.Visibility != Visibility.Visible || !view.IsVisible) return;

        if (ready && view.CoreWebView2 is { } core && view.ActualWidth > 0)
        {
            try
            {
                using var stream = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                // The overlay may have closed while the capture was running.
                if (version != _airspaceVersion) return;
                snapshot.Source = image;
            }
            catch
            {
                // No snapshot just means a blank pane behind the overlay.
            }
        }

        if (version == _airspaceVersion && IsOverlayOpen)
            view.Visibility = Visibility.Hidden;
    }

    // ---- view model reactions --------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Section))
        {
            HintStrip.ItemsSource = BuildHints();
            Dispatcher.BeginInvoke(() => Focus());
        }

        if (e.PropertyName is nameof(MainViewModel.IsHelpVisible))
            _ = UpdateAirspaceAsync();
    }

    private void OnTriageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TriageViewModel.BodyHtml):
                if (!ViewModel.Triage.IsPreviewing) RenderBody(ViewModel.Triage.BodyHtml);
                break;

            case nameof(TriageViewModel.PreviewPath):
                ShowPreviewOrBody();
                break;

            case nameof(TriageViewModel.Selected):
                Dispatcher.BeginInvoke(() =>
                {
                    if (ViewModel.Triage.Selected is { } row) MailList.ScrollIntoView(row);
                });
                break;

            case nameof(TriageViewModel.IsSearching):
                if (ViewModel.Triage.IsSearching) FocusLater(SearchBox);
                else Dispatcher.BeginInvoke(Focus);
                break;
        }
    }

    private async void OnCaptureChanged(object? sender, PropertyChangedEventArgs e)
    {
        var capture = ViewModel.Triage.Capture;

        if (e.PropertyName != nameof(CaptureViewModel.IsOpen)) return;

        if (!capture.IsOpen)
        {
            _ = UpdateAirspaceAsync();
            Dispatcher.BeginInvoke(Focus);
            return;
        }

        // The title starts as the subject, selected, so typing replaces it.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () => { CaptureTitle.Focus(); CaptureTitle.SelectAll(); });

        // If the mail had the keyboard, it keeps it even once hidden, and Tab
        // and typing vanish into it; take it back for the box once it is covered.
        await UpdateAirspaceAsync();
        if (capture.IsOpen && !CaptureCard.IsKeyboardFocusWithin)
        {
            CaptureTitle.Focus();
            CaptureTitle.SelectAll();
        }
    }

    private void OnCaptureWhoFocus(object sender, RoutedEventArgs e) => ViewModel.Triage.Capture.IsWhoFocused = true;
    private void OnCaptureWhoBlur(object sender, RoutedEventArgs e) => ViewModel.Triage.Capture.IsWhoFocused = false;

    private void OnPaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaletteViewModel.IsOpen)) return;

        _ = UpdateAirspaceAsync();
        if (ViewModel.Triage.Palette.IsOpen) FocusLater(PaletteBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    // The arrow on a conversation row lists its messages; clicking one reads just it.
    private async void OnExpandClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is MailRowViewModel row)
            await ViewModel.Triage.ToggleExpandAsync(row);
        Focus();
    }

    // ---- attaching files by dropping them on the composer -------------------

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        // Text dragged within the message still moves as text.
        if (DroppedFiles(e) is null) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ComposerDropHint.Visibility = Visibility.Visible;
    }

    private void OnComposerDragLeave(object sender, DragEventArgs e)
    {
        // Leave also fires moving between the composer's own children; only
        // hide the hint once the pointer is really outside it.
        var element = (FrameworkElement)sender;
        var at = e.GetPosition(element);
        if (at.X > 0 && at.Y > 0 && at.X < element.ActualWidth && at.Y < element.ActualHeight) return;

        ComposerDropHint.Visibility = Visibility.Collapsed;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        ComposerDropHint.Visibility = Visibility.Collapsed;
        if (DroppedFiles(e) is not { } files) return;

        e.Handled = true;
        ViewModel.Triage.Composer.AddAttachments(files);
    }

    private void OnFollowUpButtonClick(object sender, RoutedEventArgs e)
    {
        var composer = ViewModel.Triage.Composer;
        if (!composer.IsFollowingUp) composer.ToggleFollowUp();
        composer.RequestFocus(RecipientField.FollowUp);
    }

    private void OnComposeAttachmentRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ComposeAttachment attachment)
            ViewModel.Triage.Composer.RemoveAttachment(attachment);
    }

    private void OnConversationMessageClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is ConversationMessageViewModel message)
            ViewModel.Triage.FocusMessage(message);
        Focus();
    }

    // Pressing on a chip starts saving the file, so a drag that follows has it
    // on disk by the time Explorer asks for it.
    private Point _chipPressedAt;
    private EmailTriage.Core.Models.MailAttachment? _chipPressed;
    private Task<string?>? _chipSave;
    private bool _chipDragged;

    private void OnAttachmentMouseDown(object sender, MouseButtonEventArgs e)
    {
        _chipPressed = (sender as FrameworkElement)?.Tag as EmailTriage.Core.Models.MailAttachment;
        _chipPressedAt = e.GetPosition(this);
        _chipDragged = false;
        _chipSave = _chipPressed is { IsBlockedType: false } a ? ViewModel.Triage.SaveAttachmentForDragAsync(a) : null;
    }

    private async void OnAttachmentMouseMove(object sender, MouseEventArgs e)
    {
        if (_chipPressed is not { } attachment || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - _chipPressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _chipPressed = null;
        _chipDragged = true;

        if (attachment.IsBlockedType)
        {
            await ViewModel.Triage.SaveAttachmentForDragAsync(attachment); // reports why not
            return;
        }

        var path = _chipSave is null ? null : await _chipSave;
        if (path is null || Mouse.LeftButton != MouseButtonState.Pressed) return;

        // A plain file drop: Explorer, the desktop and synced SharePoint or
        // OneDrive folders all take a copy of the file.
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        ViewModel.Triage.Status = $"{attachment.Name} copied where you dropped it";
    }

    private async void OnAttachmentClick(object sender, MouseButtonEventArgs e)
    {
        var dragged = _chipDragged;
        _chipPressed = null;
        _chipDragged = false;

        if (!dragged && (sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.MailAttachment attachment)
            await ViewModel.Triage.ShowAttachmentAsync(attachment);

        // Keep the keyboard on the window, where the triage keys live.
        Focus();
    }

    private async void OnAttachmentOpenClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _chipPressed = null;
        if ((sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.MailAttachment attachment)
            await ViewModel.Triage.OpenAttachmentAsync(attachment);
        Focus();
    }

    /// <summary>Private host the panes map onto the attachment cache, for in-app previews.</summary>
    private const string AttachmentHost = "attachments.example";

    private void OnOpenPreviewExternally(object sender, RoutedEventArgs e) => ViewModel.Triage.OpenPreviewExternally();

    private void ShowPreviewOrBody()
    {
        if (!_webViewReady || BodyView.CoreWebView2 is null) return;

        if (ViewModel.Triage.PreviewPath is not { } path)
        {
            RenderBody(ViewModel.Triage.BodyHtml);
            return;
        }

        var root = EmailTriage.Outlook.OutlookMailStore.DefaultAttachmentFolder;
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..")) return; // only ever serve the attachment cache

        var url = $"https://{AttachmentHost}/" +
                  string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        BodyView.CoreWebView2.Navigate(url);
    }

    /// <summary>The clipboard's text, or null - another app can hold it locked.</summary>
    private static string? ClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private void UpdateMentionSearch()
    {
        if (!ComposerBox.IsKeyboardFocusWithin) return;
        ViewModel.Triage.Composer.UpdateMentionSearch(ComposerBox.Text, ComposerBox.CaretIndex);
    }

    /// <summary>
    /// Recipient suggestions hang under the header; mention suggestions sit
    /// under the "@" being typed, or above it when that is low in the box.
    /// </summary>
    private void PlaceSuggestions()
    {
        var composer = ViewModel.Triage.Composer;

        if (composer.SuggestingFor != RecipientField.Body)
        {
            Grid.SetRow(SuggestionPopup, 1);
            Grid.SetRowSpan(SuggestionPopup, 1);
            SuggestionPopup.Width = double.NaN;
            SuggestionPopup.CornerRadius = new CornerRadius(0, 0, 6, 6);
            SuggestionPopup.HorizontalAlignment = HorizontalAlignment.Stretch;
            SuggestionPopup.VerticalAlignment = VerticalAlignment.Top;
            SuggestionPopup.Margin = new Thickness(52, 0, 18, 0);
            return;
        }

        if (SuggestionPopup.Parent is not Grid dialog) return;

        var start = Math.Min(composer.MentionStart, ComposerBox.Text.Length);
        var rect = ComposerBox.GetRectFromCharacterIndex(start);
        if (rect.IsEmpty) return;

        // The whole dialog, not just the message row, so a long list is not squashed.
        var at = ComposerBox.TranslatePoint(rect.TopLeft, dialog);
        const double width = 420;
        var left = Math.Clamp(at.X, 18, Math.Max(18, dialog.ActualWidth - width - 18));

        Grid.SetRow(SuggestionPopup, 0);
        Grid.SetRowSpan(SuggestionPopup, 3);
        SuggestionPopup.Width = width;
        SuggestionPopup.CornerRadius = new CornerRadius(6);
        SuggestionPopup.HorizontalAlignment = HorizontalAlignment.Left;

        if (at.Y < dialog.ActualHeight / 2)
        {
            SuggestionPopup.VerticalAlignment = VerticalAlignment.Top;
            SuggestionPopup.Margin = new Thickness(left, at.Y + rect.Height + 2, 0, 0);
        }
        else
        {
            SuggestionPopup.VerticalAlignment = VerticalAlignment.Bottom;
            SuggestionPopup.Margin = new Thickness(left, 0, 0, dialog.ActualHeight - at.Y + 2);
        }
    }

    private void OnSuggestionAccepted(object? sender, RecipientField field)
    {
        if (field == RecipientField.Body)
        {
            // Focus too: a link is taken from the link row, outside the message.
            var caret = ViewModel.Triage.Composer.BodyCaret;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                ComposerBox.Focus();
                ComposerBox.CaretIndex = Math.Min(caret, ComposerBox.Text.Length);
            });
            return;
        }

        var box = field switch
        {
            RecipientField.To => ToBox,
            RecipientField.Cc => CcBox,
            RecipientField.Bcc => BccBox,
            _ => null,
        };

        // Replacing the text from the view model drops the caret at the start.
        if (box is not null) Dispatcher.BeginInvoke(() => box.CaretIndex = box.Text.Length);
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposerViewModel.SelectedSuggestion)
            && ViewModel.Triage.Composer.SelectedSuggestion is { } pick)
        {
            SuggestionList.ScrollIntoView(pick);
            return;
        }

        if (e.PropertyName is nameof(ComposerViewModel.SuggestingFor) or nameof(ComposerViewModel.MentionStart))
        {
            // After layout, so the caret position reflects the text just typed.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlaceSuggestions);
            return;
        }

        if (e.PropertyName == nameof(ComposerViewModel.IsScheduling))
        {
            FocusLater(ViewModel.Triage.Composer.IsScheduling ? ScheduleBox : ComposerBox);
            return;
        }

        if (e.PropertyName != nameof(ComposerViewModel.IsOpen)) return;

        _ = UpdateAirspaceAsync();
        if (ViewModel.Triage.Composer.IsOpen)
            FocusLater(ViewModel.Triage.Composer.StartsWithRecipients ? ToBox : ComposerBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    private void OnActionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionItemsViewModel.BodyHtml))
        {
            RenderActionBody(ViewModel.Actions.BodyHtml);
            return;
        }

        if (e.PropertyName == nameof(ActionItemsViewModel.IsReviewing))
        {
            _ = UpdateAirspaceAsync();
            return;
        }

        if (e.PropertyName != nameof(ActionItemsViewModel.Editor)) return;

        _ = UpdateAirspaceAsync();
        if (ViewModel.Actions.Editor != EditorMode.None) FocusLater(EditorPrimary);
        else Dispatcher.BeginInvoke(Focus);
    }

    /// <summary>
    /// Focuses once the overlay has actually been made visible; focusing a
    /// collapsed element silently does nothing.
    /// </summary>
    private void FocusLater(Control control) =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                control.Focus();
                if (control is TextBox box) box.CaretIndex = box.Text.Length;
            });

    // ---- keyboard ---------------------------------------------------------

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Lavish sits over everything, so it hears keys first: its chord works
        // over any pop-up, and while it is on nothing else hears them.
        if (LavishLayer.HandleKey(e)) return;

        // Typing in the action form: its own keys, never shortcuts.
        if (ViewModel.Section == Section.Actions && TryHandleFormKey(e, out var submit))
        {
            if (submit is not null) await SubmitFormAsync(submit);
            return;
        }

        var stroke = KeyStroke.FromEvent(e);
        if (stroke.IsEmpty) return;

        if (await DispatchKeyAsync(stroke)) e.Handled = true;
    }

    private async Task<bool> DispatchKeyAsync(KeyStroke stroke)
    {
        var ctrlEnter = stroke.Key == Key.Return
                     && stroke.Modifiers.HasFlag(ModifierKeys.Control);

        try
        {
            // Where the search caret sits decides whether Left/Right edit the
            // query or fold a result; a selection means the user is editing.
            var inSearch = SearchBox.IsKeyboardFocused && SearchBox.SelectionLength == 0;
            return await ViewModel.HandleKeyAsync(stroke, ctrlEnter,
                caretAtStart: inSearch && SearchBox.CaretIndex == 0,
                caretAtEnd: inSearch && SearchBox.CaretIndex == SearchBox.Text.Length);
        }
        catch (Exception ex)
        {
            ViewModel.Triage.Status = $"That did not work: {ex.Message}";
            return true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A half-written reply is easy to lose to a stray Alt+F4.
        if (ViewModel.Triage.Composer.IsOpen &&
            !string.IsNullOrWhiteSpace(ViewModel.Triage.Composer.BodyText))
        {
            var answer = MessageBox.Show(
                "You have an unsent message. Close anyway?",
                "Email Triage", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        base.OnClosing(e);
    }
}
