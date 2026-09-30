using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EmailTriage.App.Input;
using EmailTriage.App.ViewModels;
using EmailTriage.Core.Services;
using Run = System.Windows.Documents.Run;

namespace EmailTriage.App.Views;

/// <summary>
/// Lavish comment mode for one window: finding the element under the mouse,
/// saying what it is in words a stranger reading the issue would follow, and
/// pinning sent notes to it. Every window that can be on top - the main
/// window, Settings, a meeting card - hosts one as its last child, so comment
/// mode works over whatever is open. They share one <see cref="LavishViewModel"/>,
/// and only the one it is running in (its Host) shows.
/// </summary>
public partial class LavishOverlay : UserControl
{
    private const double CardGap = 8;
    private const double Edge = 12;

    private LavishViewModel? _lavish;
    private KeyMap? _keys;
    private FrameworkElement _root = null!;
    private FrameworkElement? _button;
    private Func<string> _area = () => "";
    private Func<FrameworkElement, string?> _privateKind = _ => null;
    private bool _hasPanel = true;

    /// <summary>The element under the outline: hovered, or picked while the card is open.</summary>
    private FrameworkElement? _element;

    /// <summary>Where the keyboard was when comment mode began, to hand it back afterwards.</summary>
    private IInputElement? _returnFocus;

    /// <summary>A size-to-content window's own minimum height, while the card stretches it.</summary>
    private double? _savedMinHeight;

    /// <summary>Notes sent from this window this session, each pinned to the element it was about.</summary>
    private readonly List<(FrameworkElement Element, object? Data, Border Pin)> _pins = new();

    public LavishOverlay()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Comment mode is on, and in this window.</summary>
    public bool IsActive => _lavish is { IsAnnotating: true } lavish && ReferenceEquals(lavish.Host, this);

    /// <summary>
    /// Hooks the overlay to its window. <paramref name="root"/> is what it
    /// comments on; <paramref name="button"/> is the window's Lavish button,
    /// if it has one; <paramref name="area"/> names where the user is;
    /// <paramref name="privateKind"/> names elements that picture mail, whose
    /// contents are never described; <paramref name="panel"/> shows the notes
    /// list, which only the main window has room for.
    /// </summary>
    public void Attach(
        LavishViewModel lavish, KeyMap keys, FrameworkElement root,
        FrameworkElement? button = null,
        Func<string>? area = null,
        Func<FrameworkElement, string?>? privateKind = null,
        bool panel = true)
    {
        _lavish = lavish;
        _keys = keys;
        _root = root;
        _button = button;
        _area = area ?? (() => "");
        _privateKind = privateKind ?? (_ => null);
        _hasPanel = panel;
        DataContext = lavish;

        if (!panel)
        {
            var grid = (Grid)Content;
            grid.Children.Remove(LavishPanel);
            grid.Children.Remove(LavishFoldTab);
        }

        lavish.PropertyChanged += OnLavishChanged;
        lavish.NoteSent += OnNoteSent;
        LayoutUpdated += OnLayoutUpdated;
        Sync();
    }

    private void Detach()
    {
        if (_lavish is not { } lavish) return;

        // A window closing with comment mode on in it takes comment mode with it.
        if (IsActive) lavish.Stop();
        lavish.PropertyChanged -= OnLavishChanged;
        lavish.NoteSent -= OnNoteSent;
        LayoutUpdated -= OnLayoutUpdated;
        _lavish = null;
    }

    /// <summary>The Lavish button in this window.</summary>
    public void Toggle() => _lavish?.Toggle(this);

    private void OnCloseClick(object sender, RoutedEventArgs e) => _lavish?.Stop();

    private void OnLavishChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LavishViewModel.IsAnnotating):
            case nameof(LavishViewModel.Host):
                Sync();
                break;

            case nameof(LavishViewModel.IsComposing) when IsActive:
                if (_lavish!.IsComposing)
                {
                    Dispatcher.BeginInvoke(PlaceCard, DispatcherPriority.Loaded);
                    Dispatcher.BeginInvoke(() => LavishCommentBox.Focus(), DispatcherPriority.Input);
                }
                else
                {
                    ShowOutline(null);
                    RestoreHeight();
                    Window.GetWindow(this)?.Focus();
                }
                break;
        }
    }

    /// <summary>Shows or hides this window's layer to match the shared state.</summary>
    private void Sync()
    {
        var active = IsActive;
        if (_button is not null) _button.Tag = active ? "on" : null;

        var wasActive = Visibility == Visibility.Visible;
        if (active == wasActive) return;

        ShowOutline(null);
        if (active)
        {
            _returnFocus = Keyboard.FocusedElement;
            Visibility = Visibility.Visible;
            Window.GetWindow(this)?.Focus();
        }
        else
        {
            Visibility = Visibility.Collapsed;
            RestoreHeight();

            // Back to where you were: the reply you were typing, the Settings field.
            var back = _returnFocus;
            _returnFocus = null;
            // Only in the window in front: a window behind a dialog must not take the keyboard back.
            Dispatcher.BeginInvoke(() =>
            {
                if (Window.GetWindow(this) is not { IsActive: true } window) return;
                if (back is UIElement { IsVisible: true, Focusable: true } el) el.Focus();
                else window.Focus();
            }, DispatcherPriority.Input);
        }
    }

    // ---- keys ---------------------------------------------------------------

    /// <summary>
    /// For the window's PreviewKeyDown, before anything else sees the key.
    /// The Lavish chord works from anywhere, even over a half-written reply;
    /// while comment mode is on, nothing else in the window hears keys, so a
    /// stray `e` cannot archive a mail while you are writing about it.
    /// Returns true when the key was dealt with here.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_lavish is not { } lavish || _keys is null) return false;

        var stroke = KeyStroke.FromEvent(e);
        if (!stroke.IsEmpty && _keys.Resolve(stroke) == TriageAction.Lavish)
        {
            Toggle();
            e.Handled = true;
            return true;
        }

        if (!IsActive) return false;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (lavish.IsComposing) lavish.Cancel();
            else lavish.Stop();
            e.Handled = true;
            return true;
        }

        if (key == Key.Return && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && lavish.IsComposing)
        {
            if (lavish.SendCommand.CanExecute(null)) lavish.SendCommand.Execute(null);
            e.Handled = true;
            return true;
        }

        // Typing in the note goes to the note; everything else stops here.
        if (!LavishCommentBox.IsKeyboardFocusWithin) e.Handled = true;
        return true;
    }

    // ---- finding elements ---------------------------------------------------

    private void OnLavishMouseMove(object sender, MouseEventArgs e)
    {
        if (_lavish is { IsComposing: false }) ShowOutline(ElementAt(e.GetPosition(_root)));
    }

    private void OnLavishMouseLeave(object sender, MouseEventArgs e)
    {
        if (_lavish is { IsComposing: false }) ShowOutline(null);
    }

    private void OnLavishPanelEnter(object sender, MouseEventArgs e)
    {
        if (_lavish is { IsComposing: false }) ShowOutline(null);
    }

    private void OnLavishMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_lavish is not { } lavish) return;
        e.Handled = true;

        // A click inside the open card is the card's own business.
        if (lavish.IsComposing && LavishCard.IsMouseOver) return;

        // With words already written, a click elsewhere must not throw them away.
        if (lavish.IsComposing && !string.IsNullOrWhiteSpace(lavish.Comment))
        {
            LavishCommentBox.Focus();
            return;
        }

        var element = ElementAt(e.GetPosition(_root));
        if (element is null) return;

        // The Lavish button itself still switches comment mode off.
        if (_button is not null && IsWithin(element, _button)) { lavish.Stop(); return; }

        var (target, safe) = Describe(element);
        ShowOutline(element);
        lavish.Begin(target, safe);
        PlaceCard();
    }

    /// <summary>What the window would have hit at this point, were this layer not over it.</summary>
    private FrameworkElement? ElementAt(Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(_root,
            node => ReferenceEquals(node, this) || node is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(point));

        return hit is null ? null : Pick(hit);
    }

    /// <summary>
    /// The element a person means when they point: the button, not the
    /// letters on it; the row, not the text in the row; else the text itself.
    /// </summary>
    private static FrameworkElement? Pick(DependencyObject hit)
    {
        // A scroll bar's arrows and thumb are the scroll bar, to anyone pointing at them.
        var depth = 0;
        for (var node = hit; node is not null && depth < 6; node = UpOf(node), depth++)
            if (node is ScrollBar bar) return bar;

        depth = 0;
        for (var node = hit; node is not null && depth < 10; node = UpOf(node), depth++)
        {
            if (node is ButtonBase or TextBoxBase or ComboBox or ListBoxItem or Slider)
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
    private (LavishTarget Target, bool TextIsSafe) Describe(FrameworkElement element)
    {
        var privateKind = _privateKind(element);
        var kind = privateKind ?? element switch
        {
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
        if (element is not TextBoxBase && privateKind is null)
        {
            var parts = TextsOf(element).Take(4).ToList();
            if (parts.Count > 0)
            {
                text = Shorten(string.Join(" · ", parts.Select(p => p.Text).Distinct()), 160);
                safe = parts.All(p => p.Literal);
            }
        }

        return (new LavishTarget(kind, label, path, _area(), text), safe);
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
                && fe.Name is not ("AppRoot" or "Root" or "WindowRoot"))
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

    private Rect? BoundsOf(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null || !IsWithin(element, _root)) return null;
        try
        {
            return element.TransformToVisual(LavishCanvas).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null; // not in this window's tree any more
        }
    }

    private void ShowOutline(FrameworkElement? element)
    {
        _element = element;
        var bounds = element is null ? null : BoundsOf(element);
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

        LavishHoverText.Text = Describe(element!).Target.Describe();
        LavishHoverChip.Visibility = Visibility.Visible;
        Canvas.SetLeft(LavishHoverChip, Math.Max(2, r.Left));
        Canvas.SetTop(LavishHoverChip, r.Top >= 22 ? r.Top - 20 : r.Bottom + 2);
    }

    /// <summary>The card sits under the picked element, or over it when there is no room below.</summary>
    private void PlaceCard()
    {
        if (_lavish is null || _element is null || BoundsOf(_element) is not { } r) return;

        LavishCard.UpdateLayout();
        var width = LavishCard.ActualWidth > 0 ? LavishCard.ActualWidth : LavishCard.Width;
        var height = LavishCard.ActualHeight > 0 ? LavishCard.ActualHeight : 300;
        if (MakeRoom(height)) return; // placed again once the window has grown

        var panel = _hasPanel && _lavish.IsPanelOpen ? LavishPanel.ActualWidth : 0;
        var right = LavishCanvas.ActualWidth - panel - Edge;

        var left = Math.Clamp(r.Left, Edge, Math.Max(Edge, right - width));
        var top = r.Bottom + CardGap;
        if (top + height > LavishCanvas.ActualHeight - Edge) top = r.Top - height - CardGap;
        top = Math.Clamp(top, Edge, Math.Max(Edge, LavishCanvas.ActualHeight - height - Edge));

        Canvas.SetLeft(LavishCard, left);
        Canvas.SetTop(LavishCard, top);
    }

    /// <summary>
    /// A small window sized to its content (a meeting card) can be shorter
    /// than the note card; it grows while the card is open. Returns true when
    /// it had to, and places the card again once it has.
    /// </summary>
    private bool MakeRoom(double cardHeight)
    {
        var needed = cardHeight + 2 * Edge;
        if (LavishCanvas.ActualHeight >= needed) return false;
        if (Window.GetWindow(this) is not { SizeToContent: not SizeToContent.Manual } window) return false;

        _savedMinHeight ??= window.MinHeight;
        window.MinHeight = window.ActualHeight + (needed - LavishCanvas.ActualHeight);
        Dispatcher.BeginInvoke(PlaceCard, DispatcherPriority.Loaded);
        return true;
    }

    private void RestoreHeight()
    {
        if (_savedMinHeight is { } saved && Window.GetWindow(this) is { } window) window.MinHeight = saved;
        _savedMinHeight = null;
    }

    // ---- pins ---------------------------------------------------------------

    private void OnNoteSent(object? sender, LavishNote note)
    {
        if (!IsActive || _element is not { } element) return;

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
        _pins.Add((element, element.DataContext, pin));
        PlacePins();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (IsActive) PlacePins();
    }

    /// <summary>
    /// Keeps each pin on its element's top-right corner. A recycled list row
    /// now showing a different mail loses its pin rather than lie about it.
    /// </summary>
    private void PlacePins()
    {
        foreach (var (element, data, pin) in _pins)
        {
            if (!ReferenceEquals(element.DataContext, data) || BoundsOf(element) is not { } r)
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
            _lavish?.OpenNoteCommand.Execute(row);
    }
}
