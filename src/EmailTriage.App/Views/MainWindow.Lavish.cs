using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Run = System.Windows.Documents.Run;
using System.Windows.Input;
using System.Windows.Media;
using EmailTriage.App.Input;
using EmailTriage.App.ViewModels;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Views;

/// <summary>
/// Lavish comment mode: finding the element under the mouse, saying what it
/// is in words a stranger reading the issue would follow, and pinning sent
/// notes to it. The view model decides what happens to a note.
/// </summary>
public partial class MainWindow
{
    private const double LavishCardGap = 8;
    private const double LavishEdge = 12;

    /// <summary>The element under the outline: hovered, or picked while the card is open.</summary>
    private FrameworkElement? _lavishElement;

    /// <summary>Notes sent this session, each pinned to the element it was about.</summary>
    private readonly List<(FrameworkElement Element, object? Data, Border Pin)> _lavishPins = new();

    private LavishViewModel Lavish => ViewModel.Lavish;

    private void WireLavish()
    {
        Lavish.PropertyChanged += OnLavishChanged;
        Lavish.NoteSent += (_, note) => PinLavishNote(note);
        LayoutUpdated += (_, _) => { if (Lavish.IsAnnotating) PlaceLavishPins(); };
    }

    private void OnLavishButtonClick(object sender, RoutedEventArgs e) => Lavish.Toggle();

    private void OnLavishChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LavishViewModel.IsAnnotating):
                LavishButton.Tag = Lavish.IsAnnotating ? "on" : null;
                ShowLavishOutline(null);
                _ = UpdateAirspaceAsync();
                if (!Lavish.IsAnnotating) Dispatcher.BeginInvoke(() => Focus());
                break;

            case nameof(LavishViewModel.IsComposing):
                if (Lavish.IsComposing)
                {
                    Dispatcher.BeginInvoke(PlaceLavishCard, System.Windows.Threading.DispatcherPriority.Loaded);
                    FocusLater(LavishCommentBox);
                }
                else
                {
                    ShowLavishOutline(null);
                    Focus();
                }
                break;
        }
    }

    /// <summary>
    /// Keys while comment mode is on. Nothing reaches triage: a stray `e`
    /// must not archive a mail while you are writing about it.
    /// </summary>
    private void HandleLavishKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (key == Key.Escape)
        {
            if (Lavish.IsComposing) Lavish.Cancel();
            else Lavish.Stop();
            e.Handled = true;
            return;
        }

        if (key == Key.Return && ctrl && Lavish.IsComposing)
        {
            if (Lavish.SendCommand.CanExecute(null)) Lavish.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Typing in the note goes to the note.
        if (LavishCommentBox.IsKeyboardFocusWithin) return;

        e.Handled = true;
    }

    // ---- finding elements ---------------------------------------------------

    private void OnLavishMouseMove(object sender, MouseEventArgs e)
    {
        if (Lavish.IsComposing) return;
        ShowLavishOutline(LavishElementAt(e.GetPosition(AppRoot)));
    }

    private void OnLavishMouseLeave(object sender, MouseEventArgs e)
    {
        if (!Lavish.IsComposing) ShowLavishOutline(null);
    }

    private void OnLavishPanelEnter(object sender, MouseEventArgs e)
    {
        if (!Lavish.IsComposing) ShowLavishOutline(null);
    }

    private void OnLavishMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // A click inside the open card is the card's own business.
        if (Lavish.IsComposing && LavishCard.IsMouseOver) return;

        // With words already written, a click elsewhere must not throw them away.
        if (Lavish.IsComposing && !string.IsNullOrWhiteSpace(Lavish.Comment))
        {
            LavishCommentBox.Focus();
            return;
        }

        var element = LavishElementAt(e.GetPosition(AppRoot));
        if (element is null) return;

        // The Lavish button itself still switches comment mode off.
        if (IsWithin(element, LavishButton)) { Lavish.Stop(); return; }

        var (target, safe) = DescribeForLavish(element);
        ShowLavishOutline(element);
        Lavish.Begin(target, safe);
        PlaceLavishCard();
    }

    /// <summary>What the app would have hit at this point, were the Lavish layer not over it.</summary>
    private FrameworkElement? LavishElementAt(Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(AppRoot,
            node => ReferenceEquals(node, LavishLayer) || node is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(point));

        return hit is null ? null : PickLavishElement(hit);
    }

    /// <summary>
    /// The element a person means when they point: the button, not the
    /// letters on it; the row, not the text in the row; else the text itself.
    /// </summary>
    private static FrameworkElement? PickLavishElement(DependencyObject hit)
    {
        // A scroll bar's arrows and thumb are the scroll bar, to anyone pointing at them.
        var depth = 0;
        for (var node = hit; node is not null && depth < 6; node = UpOf(node), depth++)
            if (node is ScrollBar bar) return bar;

        depth = 0;
        for (var node = hit; node is not null && depth < 10; node = UpOf(node), depth++)
        {
            if (node is ButtonBase or TextBoxBase or ComboBox or ListBoxItem or ScrollBar or Slider)
                return (FrameworkElement)node;
        }

        for (var node = hit; node is not null; node = UpOf(node))
            if (node is FrameworkElement { ActualWidth: > 0 } fe && fe is not ContentPresenter)
                return fe;

        return null;
    }

    private static DependencyObject? UpOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = UpOf(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    // ---- describing them ----------------------------------------------------

    /// <summary>
    /// What the issue says the note is about. The text is only called safe
    /// when it is written into the window itself; anything bound comes from
    /// data - a subject, a sender, a folder - and stays out unless ticked.
    /// </summary>
    private (LavishTarget Target, bool TextIsSafe) DescribeForLavish(FrameworkElement element)
    {
        var kind = element switch
        {
            _ when ReferenceEquals(element, BodySnapshot) || ReferenceEquals(element, ActionBodySnapshot) => "Reading pane",
            CheckBox => "Checkbox",
            ToggleButton => "Toggle",
            ButtonBase => "Button",
            TextBoxBase => "Text box",
            ComboBox => "Drop-down",
            ListBoxItem => "Row",
            ScrollBar => "Scroll bar",
            Slider => "Slider",
            TextBlock => "Text",
            Image => "Image",
            Border or Panel => "Area",
            _ => Humanize(element.GetType().Name),
        };

        var label = OwnName(element)
                 ?? (element is ListBoxItem ? NamedAncestors(element).LastOrDefault() : null)
                 ?? NonEmpty(AutomationProperties.GetName(element))
                 ?? (element.ToolTip is string tip && tip.Length > 0 ? Shorten(tip, 60) : null)
                 ?? "";

        var path = string.Join(" › ", NamedAncestors(element).Where(n => n != label).TakeLast(3));

        // Never what someone typed, and never the picture of a mail.
        string? text = null;
        var safe = false;
        if (element is not TextBoxBase && kind != "Reading pane")
        {
            var parts = TextsOf(element).Take(4).ToList();
            if (parts.Count > 0)
            {
                text = Shorten(string.Join(" · ", parts.Select(p => p.Text).Distinct()), 160);
                safe = parts.All(p => p.Literal);
            }
        }

        var area = ViewModel.Section switch
        {
            Section.Actions => "Action items",
            Section.Calendar => "Calendar",
            _ => "Triage",
        };

        return (new LavishTarget(kind, label, path, area, text), safe);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? OwnName(FrameworkElement element) =>
        element.Name is { Length: >= 4 } name && name != "Root" ? Humanize(name) : null;

    /// <summary>The window's own named regions around an element, outermost first ("Top bar", "Mail list").</summary>
    private static IEnumerable<string> NamedAncestors(DependencyObject element)
    {
        var names = new List<string>();
        for (var node = UpOf(element); node is not null; node = UpOf(node))
        {
            if (node is FrameworkElement { TemplatedParent: null, Name.Length: >= 4 } fe
                && fe.Name is not ("AppRoot" or "Root"))
                names.Add(Humanize(fe.Name));
        }
        names.Reverse();
        return names.Distinct();
    }

    /// <summary>The words an element shows, and whether each is fixed in the XAML rather than bound to data.</summary>
    private static IEnumerable<(string Text, bool Literal)> TextsOf(DependencyObject element)
    {
        if (element is ContentControl { Content: string content } control)
        {
            yield return (content.Trim(), BindingOperations.GetBindingExpressionBase(control, ContentControl.ContentProperty) is null);
            yield break;
        }

        var queue = new Queue<DependencyObject>();
        queue.Enqueue(element);
        var seen = 0;
        while (queue.Count > 0 && seen++ < 200)
        {
            var node = queue.Dequeue();
            if (node is TextBlock { IsVisible: true } block && block.Text.Trim() is { Length: > 0 } words)
            {
                yield return (words, IsLiteral(block));
                continue;
            }
            if (node is TextBoxBase) continue;

            var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static bool IsLiteral(TextBlock block)
    {
        if (BindingOperations.GetBindingExpressionBase(block, TextBlock.TextProperty) is not null) return false;
        return block.Inlines.OfType<Run>().All(r => BindingOperations.GetBindingExpressionBase(r, Run.TextProperty) is null);
    }

    private static string Humanize(string name)
    {
        var words = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Shorten(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    // ---- drawing ------------------------------------------------------------

    private Rect? BoundsInLavish(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null || !IsWithin(element, AppRoot)) return null;
        try
        {
            return element.TransformToVisual(LavishCanvas).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null; // not in this window's tree any more
        }
    }

    private void ShowLavishOutline(FrameworkElement? element)
    {
        _lavishElement = element;
        var bounds = element is null ? null : BoundsInLavish(element);
        if (bounds is not { } r || r.Width < 1 || r.Height < 1)
        {
            LavishHover.Visibility = Visibility.Collapsed;
            LavishHoverChip.Visibility = Visibility.Collapsed;
            return;
        }

        r.Inflate(2, 2);
        Canvas.SetLeft(LavishHover, r.Left);
        Canvas.SetTop(LavishHover, r.Top);
        LavishHover.Width = r.Width;
        LavishHover.Height = r.Height;
        LavishHover.Visibility = Visibility.Visible;

        LavishHoverText.Text = DescribeForLavish(element!).Target.Describe();
        LavishHoverChip.Visibility = Visibility.Visible;
        Canvas.SetLeft(LavishHoverChip, Math.Max(2, r.Left));
        Canvas.SetTop(LavishHoverChip, r.Top >= 22 ? r.Top - 20 : r.Bottom + 2);
    }

    /// <summary>The card sits under the picked element, or over it when there is no room below.</summary>
    private void PlaceLavishCard()
    {
        if (_lavishElement is null || BoundsInLavish(_lavishElement) is not { } r) return;

        LavishCard.UpdateLayout();
        var width = LavishCard.ActualWidth > 0 ? LavishCard.ActualWidth : LavishCard.Width;
        var height = LavishCard.ActualHeight > 0 ? LavishCard.ActualHeight : 300;
        var right = LavishCanvas.ActualWidth - (Lavish.IsPanelOpen ? LavishPanel.ActualWidth : 0) - LavishEdge;

        var left = Math.Clamp(r.Left, LavishEdge, Math.Max(LavishEdge, right - width));
        var top = r.Bottom + LavishCardGap;
        if (top + height > LavishCanvas.ActualHeight - LavishEdge) top = r.Top - height - LavishCardGap;
        top = Math.Clamp(top, LavishEdge, Math.Max(LavishEdge, LavishCanvas.ActualHeight - height - LavishEdge));

        Canvas.SetLeft(LavishCard, left);
        Canvas.SetTop(LavishCard, top);
    }

    // ---- pins ---------------------------------------------------------------

    private void PinLavishNote(LavishNote note)
    {
        if (_lavishElement is not { } element) return;

        var pin = new Border
        {
            Background = (Brush)FindResource("LavishBrass"),
            BorderBrush = (Brush)FindResource("LavishInk"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(11),
            MinWidth = 22,
            Height = 22,
            Padding = new Thickness(5, 0, 5, 0),
            IsHitTestVisible = false,
            ToolTip = note.Comment,
            Child = new TextBlock
            {
                Text = note.IssueNumber is { } n ? $"#{n}" : "•",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("LavishBrassInk"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        LavishCanvas.Children.Add(pin);
        _lavishPins.Add((element, element.DataContext, pin));
        PlaceLavishPins();
    }

    /// <summary>
    /// Keeps each pin on its element's top-right corner. A recycled list row
    /// now showing a different mail loses its pin rather than lie about it.
    /// </summary>
    private void PlaceLavishPins()
    {
        foreach (var (element, data, pin) in _lavishPins)
        {
            if (!ReferenceEquals(element.DataContext, data) || BoundsInLavish(element) is not { } r)
            {
                pin.Visibility = Visibility.Collapsed;
                continue;
            }
            pin.Visibility = Visibility.Visible;
            Canvas.SetLeft(pin, r.Right - 11);
            Canvas.SetTop(pin, r.Top - 11);
        }
    }

    private void OnLavishNoteClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LavishNoteRow row)
            Lavish.OpenNoteCommand.Execute(row);
    }

    /// <summary>The Lavish chord works from anywhere, even over a half-written reply.</summary>
    private bool IsLavishStroke(KeyStroke stroke) =>
        !stroke.IsEmpty && ViewModel.Keys.Resolve(stroke) == TriageAction.Lavish;
}
