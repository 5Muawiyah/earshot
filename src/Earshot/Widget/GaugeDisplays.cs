using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Widget;

// One connected display, as the gauge's display choice and the full-screen rule see it. Every rectangle is in
// physical screen pixels.
//
// Id is what the setting stores. It is the monitor's device interface name, the path Windows registers for
// GUID_DEVINTERFACE_MONITOR, which EnumDisplayDevices returns in DeviceID when asked with
// EDD_GET_DEVICE_INTERFACE_NAME. It names the monitor and the output it is plugged into, not its place in the
// enumeration, so it survives a restart and a change of which display is primary. Two monitors of one model
// differ only in the last part of the path (the output's id), which is why the whole path is kept. When Windows
// gives no interface name for a display, Id falls back to "gdi:" plus the GDI device name, which is stable for
// as long as the display keeps its number; StableId says which it is.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw
// https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-display_devicew
internal sealed record DisplayInfo(
    string Id,
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary,
    int Dpi,
    nint Handle = 0,
    bool StableId = true);

// What the gauge's display choice reads from Windows. SystemDisplaySource is the real one; tests supply their own.
internal interface IDisplaySource
{
    DisplayReading Read();
}

// A reading of the displays: the list (empty only when Windows could not be asked at all) and, when a step failed,
// the first raw outcome. A display whose own steps failed is still listed where it can be, with the fallback
// named in the outcome.
internal sealed record DisplayReading(IReadOnlyList<DisplayInfo> Displays, StepOutcome? Problem = null);

// The plain names the settings page and the log use for a display. Never a device name the owner cannot
// recognise: "Display 2", with its resolution on the settings page.
internal static class DisplayNames
{
    private const string GdiPrefix = @"\\.\DISPLAY";

    // The display's own number. The GDI device name ("\\.\DISPLAY2") carries the number Windows gave the display
    // and Settings shows as the display's own; when a name does not carry one, the display's place in the list.
    public static int Number(DisplayInfo display, IReadOnlyList<DisplayInfo> all)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(all);
        if (display.DeviceName.StartsWith(GdiPrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(display.DeviceName.AsSpan(GdiPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
        {
            return n;
        }

        int index = 0;
        foreach (DisplayInfo d in all)
        {
            index++;
            if (ReferenceEquals(d, display) || d.Id == display.Id)
            {
                return index;
            }
        }

        return index + 1;
    }

    public static string Short(DisplayInfo display, IReadOnlyList<DisplayInfo> all) =>
        "Display " + Number(display, all).ToString(CultureInfo.InvariantCulture);

    // "Display 2 (1920 x 1080)".
    public static string Long(DisplayInfo display, IReadOnlyList<DisplayInfo> all) =>
        Short(display, all) + " (" + display.Bounds.Width.ToString(CultureInfo.InvariantCulture) + " x " +
        display.Bounds.Height.ToString(CultureInfo.InvariantCulture) + ")";

    // The display a rectangle of the desktop sits on, by its bounds, or null when none has those bounds.
    public static DisplayInfo? Of(Rectangle monitorBounds, IReadOnlyList<DisplayInfo> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        foreach (DisplayInfo d in all)
        {
            if (d.Bounds == monitorBounds)
            {
                return d;
            }
        }

        return null;
    }
}

// Why the gauge is not on the display the owner chose.
internal enum DisplayFallbackReason
{
    None,
    NotConnected,        // the chosen display is not connected
    TaskbarNotShown,     // the chosen display is connected, but shows no taskbar (the owner turned it off for other displays)
}

// Which display the gauge's taskbar is read from. Pure.
internal static class GaugeDisplayChoice
{
    // The setting's value for the main display.
    public const string MainDisplay = "";

    // The display for a stored choice: the primary display for "" (the default), the display with that Id when it
    // is connected, else the primary display with NotConnected. Null only when there is no display at all.
    public static (DisplayInfo? Display, DisplayFallbackReason Fallback) Resolve(string? chosenId, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        DisplayInfo? primary = null;
        foreach (DisplayInfo d in displays)
        {
            if (d.IsPrimary)
            {
                primary = d;
                break;
            }
        }

        primary ??= displays.Count > 0 ? displays[0] : null;
        if (string.IsNullOrEmpty(chosenId))
        {
            return (primary, DisplayFallbackReason.None);
        }

        foreach (DisplayInfo d in displays)
        {
            if (string.Equals(d.Id, chosenId, StringComparison.OrdinalIgnoreCase))
            {
                return (d, DisplayFallbackReason.None);
            }
        }

        return (primary, DisplayFallbackReason.NotConnected);
    }
}

// One entry of the settings page's display list.
internal sealed record DisplayOption(string Id, string Label);

// The display list the settings page cycles through, and the words around it. Pure.
internal static class GaugeDisplayOptions
{
    public const string MainLabel = "Main display";
    public const string NotConnectedLabel = "Not connected";

    // "Main display" first, then every connected display by its number.
    public static IReadOnlyList<DisplayOption> Build(IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var options = new List<DisplayOption> { new(GaugeDisplayChoice.MainDisplay, MainLabel) };
        foreach (DisplayInfo d in displays.OrderBy(x => DisplayNames.Number(x, displays)))
        {
            options.Add(new DisplayOption(d.Id, DisplayNames.Long(d, displays)));
        }

        return options;
    }

    // What the row shows for the stored choice: its entry's label, or "Not connected" when no entry has that id.
    public static string LabelFor(IReadOnlyList<DisplayOption> options, string chosenId)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (DisplayOption o in options)
        {
            if (string.Equals(o.Id, chosenId ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return o.Label;
            }
        }

        return NotConnectedLabel;
    }

    // The choice after the current one, wrapping round; the first entry when the current one is not in the list.
    public static string Next(IReadOnlyList<DisplayOption> options, string chosenId)
    {
        ArgumentNullException.ThrowIfNull(options);
        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Id, chosenId ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return options[(i + 1) % options.Count].Id;
            }
        }

        return options.Count > 0 ? options[0].Id : GaugeDisplayChoice.MainDisplay;
    }
}

// A top-level window that might be a secondary taskbar, as the reader sees it: Shell_SecondaryTrayWnd, with
// the monitor it is on (the nearest one, so a taskbar slid off screen by auto-hide still belongs to its own).
internal readonly record struct TaskbarWindowCandidate(nint Handle, Rectangle Bounds, bool Visible, nint Monitor);

internal static class SecondaryTaskbarPicker
{
    // The taskbar window to read for a display: the first visible one on that display's monitor.
    public static TaskbarWindowCandidate? Pick(IEnumerable<TaskbarWindowCandidate> candidates, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(display);
        foreach (TaskbarWindowCandidate c in candidates)
        {
            if (c.Visible && c.Monitor == display.Handle && !c.Bounds.IsEmpty)
            {
                return c;
            }
        }

        return null;
    }
}

// The foreground window as the full-screen rule needs it: what it is (class only), its rectangle, the monitor it
// is on and that monitor's plain name.
internal sealed record ForegroundWindowReading(WindowIdentity? Identity, Rectangle Bounds, Rectangle MonitorBounds, string DisplayLabel);

// Whether a full-screen signal is about the display the gauge is on. The signals themselves (the appbar's
// ABN_FULLSCREENAPP, SHQueryUserNotificationState) are global: neither carries a monitor, and the appbar message
// goes to every appbar, so a full-screen game on one display raises them for the gauge on another. They say that a
// full-screen application exists; this says whether it covers the gauge.
//
// A full-screen window is one that is on the gauge's monitor (MonitorFromWindow) and whose rectangle covers that
// monitor's full bounds. With one display that can only be the gauge's own, so the signal stands alone, as it
// always did. With several, a foreground window that cannot be read is treated as covering: the gauge hides, as
// before, and the next read decides.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
// https://learn.microsoft.com/en-us/windows/win32/shell/abn-fullscreenapp
internal static class FullScreenRule
{
    public static bool CoversGaugeDisplay(int displayCount, ForegroundWindowReading? foreground, Rectangle gaugeDisplay)
    {
        if (displayCount <= 1 || foreground is null)
        {
            return true;
        }

        return foreground.MonitorBounds == gaugeDisplay && Covers(foreground.Bounds, gaugeDisplay);
    }

    // True when window contains every point of the display's bounds.
    public static bool Covers(Rectangle window, Rectangle display) =>
        !display.IsEmpty && window.Left <= display.Left && window.Top <= display.Top &&
        window.Right >= display.Right && window.Bottom >= display.Bottom;
}
