using System.Windows;
using System.Windows.Controls;

namespace EmailTriage.App.Views;

/// <summary>
/// Lays out one day's meetings in the time grid: each child sits at its
/// <see cref="TopProperty"/> for its <see cref="BlockHeightProperty"/>, in
/// lane <see cref="LaneProperty"/> of <see cref="LanesProperty"/> equal
/// slices of the width, so overlapping meetings sit side by side and still
/// fit when the window is resized.
/// </summary>
public sealed class TimeGridPanel : Panel
{
    public static readonly DependencyProperty TopProperty = DependencyProperty.RegisterAttached(
        "Top", typeof(double), typeof(TimeGridPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty BlockHeightProperty = DependencyProperty.RegisterAttached(
        "BlockHeight", typeof(double), typeof(TimeGridPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty LaneProperty = DependencyProperty.RegisterAttached(
        "Lane", typeof(int), typeof(TimeGridPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty LanesProperty = DependencyProperty.RegisterAttached(
        "Lanes", typeof(int), typeof(TimeGridPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetTop(UIElement e) => (double)e.GetValue(TopProperty);
    public static void SetTop(UIElement e, double value) => e.SetValue(TopProperty, value);
    public static double GetBlockHeight(UIElement e) => (double)e.GetValue(BlockHeightProperty);
    public static void SetBlockHeight(UIElement e, double value) => e.SetValue(BlockHeightProperty, value);
    public static int GetLane(UIElement e) => (int)e.GetValue(LaneProperty);
    public static void SetLane(UIElement e, int value) => e.SetValue(LaneProperty, value);
    public static int GetLanes(UIElement e) => (int)e.GetValue(LanesProperty);
    public static void SetLanes(UIElement e, int value) => e.SetValue(LanesProperty, value);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 0 : available.Width;
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(width / Math.Max(1, GetLanes(child)), GetBlockHeight(child)));

        return new Size(width, double.IsInfinity(available.Height) ? 0 : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            var lanes = Math.Max(1, GetLanes(child));
            var lane = Math.Clamp(GetLane(child), 0, lanes - 1);
            var slice = final.Width / lanes;
            child.Arrange(new Rect(lane * slice, GetTop(child), slice, GetBlockHeight(child)));
        }
        return final;
    }
}
