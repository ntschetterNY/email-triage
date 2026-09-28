using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Automation;

namespace EmailTriage.Outlook;

/// <summary>
/// Presses "Teams Meeting" on an open Outlook meeting window. Outlook's object
/// model has no way to add a Teams meeting: the Teams add-in (or Outlook's own
/// online-meeting support) only does it from its ribbon button, so this finds
/// that button through UI Automation, as a screen reader would, and invokes it.
///
/// Best effort by nature: the button's name is localized, and it is missing
/// when Teams is not installed or the organization has turned it off. The
/// caller says so plainly when nothing was pressed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TeamsButton
{
    /// <summary>How long to wait for the ribbon, and the add-in's button on it, to appear.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The inspector's window handle, or zero. Must run on the thread that
    /// owns the inspector (the store's STA).
    /// </summary>
    public static IntPtr WindowOf(object inspector)
    {
        try
        {
            if (inspector is IOleWindow ole && ole.GetWindow(out var hwnd) == 0 && hwnd != IntPtr.Zero)
                return hwnd;
        }
        catch
        {
            // Not every Outlook build hands IOleWindow out of process; fall back to the caption.
        }

        try
        {
            var caption = (string)((dynamic)inspector).Caption;
            return string.IsNullOrEmpty(caption) ? IntPtr.Zero : FindWindow(InspectorClass, caption);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Presses the Teams button on the window. True if one was found and pressed.</summary>
    public static bool TryPress(IntPtr window, CancellationToken ct)
    {
        if (window == IntPtr.Zero) return false;

        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                if (Find(AutomationElement.FromHandle(window)) is { } button && Press(button)) return true;
            }
            catch (ElementNotAvailableException)
            {
                return false; // the window was closed
            }
            catch
            {
                // The ribbon is still being built; look again.
            }

            Thread.Sleep(Poll);
        }

        return false;
    }

    private static AutomationElement? Find(AutomationElement window)
    {
        var buttons = window.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton)));

        AutomationElement? best = null;
        foreach (AutomationElement b in buttons)
        {
            var name = b.Current.Name ?? "";
            if (!name.Contains("Teams Meeting", StringComparison.OrdinalIgnoreCase)) continue;

            // "Teams Meeting Options" and the like change settings; they do not add one.
            if (name.Contains("option", StringComparison.OrdinalIgnoreCase)) continue;
            if (!b.Current.IsEnabled) continue;

            // "Teams Meeting" itself beats "New Teams Meeting" and other wordings.
            if (name.Equals("Teams Meeting", StringComparison.OrdinalIgnoreCase)) return b;
            best ??= b;
        }

        return best;
    }

    private static bool Press(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return true;
        }

        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            var t = (TogglePattern)toggle;
            if (t.Current.ToggleState != ToggleState.On) t.Toggle();
            return true;
        }

        return false;
    }

    /// <summary>The window class of every Outlook inspector.</summary>
    private const string InspectorClass = "rctrl_renwnd32";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
}
