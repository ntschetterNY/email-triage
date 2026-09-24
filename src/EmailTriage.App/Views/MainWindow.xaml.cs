using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ---- view model reactions --------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Section))
            Dispatcher.BeginInvoke(() => Focus());
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

        if (ViewModel.Triage.Palette.IsOpen) FocusLater(PaletteBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComposerViewModel.IsOpen)) return;

        if (ViewModel.Triage.Composer.IsOpen) FocusLater(ComposerBox);
        else Dispatcher.BeginInvoke(Focus);
    }

    private void OnActionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActionItemsViewModel.Editor)) return;

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

        var ctrlEnter = stroke.Key == Key.Return
                     && stroke.Modifiers.HasFlag(ModifierKeys.Control);

        try
        {
            if (await ViewModel.HandleKeyAsync(stroke, ctrlEnter)) e.Handled = true;
        }
        catch (Exception ex)
        {
            ViewModel.Triage.Status = $"That did not work: {ex.Message}";
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A half-written reply is easy to lose to a stray Alt+F4.
        if (ViewModel.Triage.Composer.IsOpen &&
            !string.IsNullOrWhiteSpace(ViewModel.Triage.Composer.BodyText))
        {
            var answer = MessageBox.Show(
                "You have an unsent reply. Close anyway?",
                "Email Triage", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        base.OnClosing(e);
    }
}
