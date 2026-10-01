using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EmailTriage.App.ViewModels;

namespace EmailTriage.App.Views;

/// <summary>
/// A meeting's card, opened under its pill in the top bar. Joining is the big
/// button at the top (or Enter); Esc or a click elsewhere closes it. Edit (or
/// E) turns it into a form for your own meetings: Ctrl+Enter saves or sends
/// the update, Esc goes back to the card.
/// </summary>
public partial class MeetingWindow : Window
{
    private readonly MainViewModel _main;
    private readonly MeetingCardViewModel _card;
    private bool _closing;

    public MeetingWindow(MainViewModel main, MeetingCardViewModel card)
    {
        _main = main;
        _card = card;
        DataContext = card;
        InitializeComponent();

        LavishLayer.Attach(main.Lavish, main.Keys, WindowRoot, LavishButton, area: () => "Meeting card", panel: false);

        PreviewKeyDown += (_, e) =>
        {
            // Lavish first, so Esc leaves comment mode rather than closing the card.
            if (LavishLayer.HandleKey(e)) return;

            if (_card.IsEditing)
            {
                if (e.Key == Key.Escape && !_card.IsSaving) { e.Handled = true; CancelEdit(); }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = SaveAsync(); }
                return;
            }

            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.None && _card.CanStartEdit) { e.Handled = true; BeginEdit(); }
        };

        // A pop-up: clicking back into the app puts it away - unless you are
        // commenting on it, when sending a note may well open the browser, or
        // part way through an edit, which a stray click must not throw away
        // (and adding Teams brings Outlook's own window up in front).
        Deactivated += (_, _) => { if (!_closing && !LavishLayer.IsActive && !_card.IsEditing) Close(); };

        // The link is read after the card opens; once it is there, Enter joins.
        card.PropertyChanged += OnCardChanged;
        Loaded += (_, _) => { if (card.CanJoin) JoinButton.Focus(); else Focus(); };
    }

    /// <summary>Places the card just under <paramref name="anchor"/>, kept on its screen.</summary>
    public void ShowUnder(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is { } target)
        {
            var corner = target.TransformFromDevice.Transform(anchor.PointToScreen(new Point(0, anchor.ActualHeight)));
            var area = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(corner.X, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = corner.Y + 6;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        Show();
        Activate();
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingCardViewModel.CanJoin) && _card.CanJoin && IsActive) JoinButton.Focus();
    }

    private void OnJoinClick(object sender, RoutedEventArgs e)
    {
        if (!_card.CanJoin) return;
        _main.JoinFromCard(_card);
        Close();
    }

    private async void OnOutlookClick(object sender, RoutedEventArgs e)
    {
        Close();
        await _main.OpenCardInOutlookAsync(_card);
    }

    private void OnCalendarClick(object sender, RoutedEventArgs e)
    {
        _main.ShowCardInCalendar(_card);
        Close();
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => BeginEdit();

    private void BeginEdit()
    {
        _card.BeginEdit();
        if (!_card.IsEditing) return;

        // Once the form is laid out; it is collapsed until now.
        Dispatcher.BeginInvoke(() => { EditSubjectBox.Focus(); EditSubjectBox.SelectAll(); },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CancelEdit()
    {
        _card.CancelEdit();
        if (_card.CanJoin) JoinButton.Focus(); else Focus();
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e) => CancelEdit();

    private void OnSaveClick(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (await _main.SaveCardAsync(_card).ConfigureAwait(true) && !_closing) Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLavishButtonClick(object sender, RoutedEventArgs e) => LavishLayer.Toggle();

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _card.PropertyChanged -= OnCardChanged;
        base.OnClosing(e);
    }
}
