using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EmailTriage.App.ViewModels;

namespace EmailTriage.App.Views;

/// <summary>
/// A meeting's card, opened under its pill in the top bar. Joining is the big
/// button at the top (or Enter); Esc or a click elsewhere closes it.
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };

        // A pop-up: clicking back into the app puts it away.
        Deactivated += (_, _) => { if (!_closing) Close(); };

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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _card.PropertyChanged -= OnCardChanged;
        base.OnClosing(e);
    }
}
