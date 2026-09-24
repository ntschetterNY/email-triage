using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using EmailTriage.App.Input;
using EmailTriage.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace EmailTriage.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool _webViewReady;
    private string _pendingHtml = "";

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        HelpList.ItemsSource = viewModel.HelpRows
            .Select(r => new { r.Group, r.Keys, r.Description })
            .ToList();

        HintStrip.ItemsSource = BuildHints();

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Triage.PropertyChanged += OnTriageChanged;
        viewModel.Triage.Palette.PropertyChanged += OnPaletteChanged;
        viewModel.Triage.Composer.PropertyChanged += OnComposerChanged;
        viewModel.Triage.Composer.SuggestionAccepted += OnSuggestionAccepted;

        // Suggestions belong to the line being typed in; moving elsewhere drops them.
        foreach (var box in new[] { ToBox, CcBox, BccBox, ComposerBox })
            box.GotKeyboardFocus += (_, _) => viewModel.Triage.Composer.CloseSuggestions();
        viewModel.Actions.PropertyChanged += OnActionsChanged;

        Loaded += async (_, _) => await InitialiseWebViewAsync();
    }

    private IReadOnlyList<object> BuildHints()
    {
        var keys = ViewModel.Keys;
        return new object[]
        {
            new { Key = keys.Describe(TriageAction.MarkActionRequired), Label = "action" },
            new { Key = keys.Describe(TriageAction.MoveToFolder), Label = "move" },
            new { Key = keys.Describe(TriageAction.Snooze), Label = "later" },
            new { Key = keys.Describe(TriageAction.ReplyAll), Label = "reply all" },
            new { Key = keys.Describe(TriageAction.ShowHelp), Label = "help" },
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

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await BodyView.EnsureCoreWebView2Async(env);

            var core = BodyView.CoreWebView2;

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
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
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

            _webViewReady = true;
            if (_pendingHtml.Length > 0) core.NavigateToString(_pendingHtml);
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

    // ---- airspace ----------------------------------------------------------

    private int _airspaceVersion;

    private bool IsOverlayOpen =>
        ViewModel.Triage.Palette.IsOpen
        || ViewModel.Triage.Composer.IsOpen
        || ViewModel.Actions.Editor != EditorMode.None
        || ViewModel.IsHelpVisible;

    /// <summary>
    /// WebView2 is a native child window, and WPF cannot draw over one: an
    /// overlay that crosses the reading pane is simply hidden behind the mail.
    /// While an overlay is open, the live view is swapped for a still image of
    /// itself, which WPF can dim and cover like anything else.
    /// </summary>
    private async Task UpdateAirspaceAsync()
    {
        var version = ++_airspaceVersion;

        if (!IsOverlayOpen)
        {
            BodyView.Visibility = Visibility.Visible;
            BodySnapshot.Source = null;
            return;
        }

        if (BodyView.Visibility != Visibility.Visible) return;

        if (_webViewReady && BodyView.CoreWebView2 is { } core && BodyView.ActualWidth > 0)
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
                BodySnapshot.Source = image;
            }
            catch
            {
                // No snapshot just means a blank pane behind the overlay.
            }
        }

        if (version == _airspaceVersion && IsOverlayOpen)
            BodyView.Visibility = Visibility.Hidden;
    }

    // ---- view model reactions --------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Section))
            Dispatcher.BeginInvoke(() => Focus());

        if (e.PropertyName is nameof(MainViewModel.IsHelpVisible))
            _ = UpdateAirspaceAsync();
    }

    private void OnTriageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TriageViewModel.BodyHtml):
                RenderBody(ViewModel.Triage.BodyHtml);
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

    private void OnPaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaletteViewModel.IsOpen)) return;

        _ = UpdateAirspaceAsync();
        if (ViewModel.Triage.Palette.IsOpen) FocusLater(PaletteBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    private async void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmailTriage.Core.Models.MailAttachment attachment)
            await ViewModel.Triage.OpenAttachmentAsync(attachment);

        // Keep the keyboard on the window, where the triage keys live.
        Focus();
    }

    private void OnSuggestionAccepted(object? sender, RecipientField field)
    {
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

        if (e.PropertyName == nameof(ComposerViewModel.IsScheduling))
        {
            FocusLater(ViewModel.Triage.Composer.IsScheduling ? ScheduleBox : ComposerBox);
            return;
        }

        if (e.PropertyName != nameof(ComposerViewModel.IsOpen)) return;

        _ = UpdateAirspaceAsync();
        if (ViewModel.Triage.Composer.IsOpen)
            FocusLater(ViewModel.Triage.Composer.IsForward ? ToBox : ComposerBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    private void OnActionsChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            return await ViewModel.HandleKeyAsync(stroke, ctrlEnter);
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
