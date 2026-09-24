using System.Text;
using System.Windows.Input;

namespace EmailTriage.App.Input;

/// <summary>
/// A single chord, e.g. `k`, `Shift+R`, `Ctrl+Enter`. Comparable and
/// round-trippable through the string form used in the config file.
/// </summary>
public readonly record struct KeyStroke(Key Key, ModifierKeys Modifiers)
{
    public static KeyStroke FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore a modifier pressed on its own.
        if (key is Key.LeftShift or Key.RightShift
                or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin)
            return default;

        return new KeyStroke(key, Keyboard.Modifiers);
    }

    public bool IsEmpty => Key == Key.None;

    /// <summary>
    /// Parses "shift+r", "ctrl+enter", "k". Unknown text yields an empty stroke
    /// rather than throwing, so one bad line in a config file cannot stop
    /// the app from starting.
    /// </summary>
    public static KeyStroke Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = ModifierKeys.None;
        Key key = Key.None;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "win": mods |= ModifierKeys.Windows; continue;
            }

            key = ParseKey(part);
            if (key == Key.None) return default;
        }

        return key == Key.None ? default : new KeyStroke(key, mods);
    }

    private static Key ParseKey(string token)
    {
        var name = token switch
        {
            "/" => "Oem2",
            "?" => "Oem2",
            "." => "OemPeriod",
            "," => "OemComma",
            "esc" => "Escape",
            "space" => "Space",
            "enter" or "return" => "Return",
            _ => token,
        };

        if (name.Length == 1 && char.IsLetter(name[0]))
            name = char.ToUpperInvariant(name[0]).ToString();

        if (name.Length == 1 && char.IsDigit(name[0]))
            name = "D" + name;

        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) ? key : Key.None;
    }

    public override string ToString()
    {
        if (IsEmpty) return "";

        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");

        sb.Append(Key switch
        {
            Key.Oem2 => "/",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (Key - Key.D0))).ToString(),
            _ => Key.ToString(),
        });

        return sb.ToString();
    }
}
