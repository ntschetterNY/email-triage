using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EmailTriage.App.ViewModels;
using EmailTriage.Core.Models;

namespace EmailTriage.App.Services;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var hasValue = value is not null;
        if (value is string s) hasValue = !string.IsNullOrWhiteSpace(s);
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Shows an element only when the app is on the named section.</summary>
public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
        => value is Section s && parameter is string name
           && s.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class SectionActiveConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
        => value is Section s && parameter is string name
           && s.ToString().Equals(name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class PriorityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        ActionPriority.High => new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)),
        ActionPriority.Low => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        _ => new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7)),
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Collapses an element when a collection is empty.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var any = value switch
        {
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => false,
        };
        if (Invert) any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Shows the inline editor overlay whenever an editor mode is active.</summary>
public sealed class EditorVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is EditorMode m && m != EditorMode.None
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Only the notes editor wants a multi-line box.</summary>
public sealed class EditorMultilineConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is EditorMode.Note;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Blockers and assignments need the extra "who" and "due" fields.</summary>
public sealed class EditorExtraFieldsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is EditorMode.Blocker or EditorMode.Assignment
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>
/// Labels the first editor field according to what is being captured, so the
/// same box can serve notes, blockers and assignments.
/// </summary>
public sealed class EditorPrimaryLabelConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        EditorMode.Note => "Notes",
        EditorMode.Blocker => "What is blocking it",
        EditorMode.Assignment => "Who  (e.g. Alice Smith <alice@corp.com>)",
        _ => "",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class EditorSecondaryLabelConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        EditorMode.Blocker => "Waiting on",
        EditorMode.Assignment => "What they need to do",
        _ => "",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
