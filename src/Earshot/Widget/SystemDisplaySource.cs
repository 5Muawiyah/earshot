using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Interop;

namespace Earshot.Widget;

// The secondary taskbar windows that could be read, and the first failed read (with its raw code), or null.
internal sealed record SecondaryTaskbarReading(IReadOnlyList<TaskbarWindowCandidate> Taskbars, StepOutcome? Problem = null);

// The real display source: EnumDisplayMonitors for the monitors, GetMonitorInfo for bounds, work area and device
// name, GetDpiForMonitor for each display's own scale, EnumDisplayDevices for the monitor's interface path. Safe from
// any thread; changes nothing.
internal sealed class SystemDisplaySource : IDisplaySource
{
    public DisplayReading Read()
    {
        if (!Shell.TryListMonitors(out List<nint> monitors))
        {
            // EnumDisplayMonitors documents no error code, so none is read.
            return new DisplayReading([], StepOutcomes.FromWin32("enum-display-monitors", 0, "EnumDisplayMonitors returned FALSE.", ok: false));
        }

        StepOutcome? problem = null;
        var list = new List<DisplayInfo>(monitors.Count);
        foreach (nint monitor in monitors)
        {
            if (!Shell.TryReadMonitor(monitor, out RECT bounds, out RECT work, out uint flags, out string device, out uint error))
            {
                problem ??= StepOutcomes.FromWin32("get-monitor-info:display", error, ok: false);
                continue;
            }

            int hr = Shell.GetDpiForMonitor(monitor, Shell.MDT_EFFECTIVE_DPI, out uint dpi, out _);
            if (hr < 0 || dpi == 0)
            {
                problem ??= StepOutcomes.FromHResult("get-dpi-for-monitor:display", hr);
                dpi = Shell.GetDpiForSystem();
            }

            // The interface path when Windows gives one, else "gdi:" plus the device name (see DisplayInfo).
            string path = Shell.MonitorInterfacePath(device);
            list.Add(new DisplayInfo(
                path.Length > 0 ? path : "gdi:" + device, device,
                Rectangle.FromLTRB(bounds.left, bounds.top, bounds.right, bounds.bottom),
                Rectangle.FromLTRB(work.left, work.top, work.right, work.bottom),
                (flags & Shell.MONITORINFOF_PRIMARY) != 0, dpi > 0 ? (int)dpi : 96, monitor, path.Length > 0));
        }

        return new DisplayReading(list, problem);
    }

    // Every visible or hidden secondary taskbar window, each with the monitor it is on. Safe from any thread. A window whose
    // rectangle could not be read is left out, and the first such failure is returned with its raw code, so a chosen display
    // whose taskbar was skipped for that reason is not mistaken for one that shows none.
    public static SecondaryTaskbarReading SecondaryTaskbars() =>
        Collect(
            after => Shell.FindWindowEx(0, after, "Shell_SecondaryTrayWnd", null),
            hwnd =>
            {
                bool ok = NativeMethods.GetWindowRect(hwnd, out RECT rect);
                return (ok, Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom), ok ? 0u : unchecked((uint)Marshal.GetLastPInvokeError()));
            },
            Shell.IsWindowVisible,
            hwnd => Shell.MonitorFromWindow(hwnd, Shell.MONITOR_DEFAULTTONEAREST));

    // The walk itself, with what it asks of Windows passed in: the next window after one (0 at the end), a window's rectangle
    // and the error code when it cannot be read, whether it is visible, and its monitor.
    internal static SecondaryTaskbarReading Collect(
        Func<nint, nint> next, Func<nint, (bool Ok, Rectangle Bounds, uint Error)> rectOf, Func<nint, bool> visible, Func<nint, nint> monitorOf)
    {
        var found = new List<TaskbarWindowCandidate>();
        StepOutcome? problem = null;
        nint hwnd = 0;
        while (true)
        {
            hwnd = next(hwnd);
            if (hwnd == 0)
            {
                return new SecondaryTaskbarReading(found, problem);
            }

            (bool ok, Rectangle bounds, uint error) = rectOf(hwnd);
            if (!ok)
            {
                problem ??= StepOutcomes.FromWin32("get-window-rect:Shell_SecondaryTrayWnd", error, ok: false);
                continue;
            }

            found.Add(new TaskbarWindowCandidate(hwnd, bounds, visible(hwnd), monitorOf(hwnd)));
        }
    }

    // The foreground window's class, rectangle and monitor, or null when there is no foreground window or its
    // monitor cannot be found.
    public static ForegroundWindowReading? ForegroundWindow(IReadOnlyList<DisplayInfo> displays)
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        nint root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        nint window = root != 0 ? root : hwnd;
        if (!NativeMethods.GetWindowRect(window, out RECT rect))
        {
            return null;
        }

        nint monitor = Shell.MonitorFromWindow(window, Shell.MONITOR_DEFAULTTONEAREST);
        DisplayInfo? display = null;
        foreach (DisplayInfo d in displays)
        {
            if (d.Handle == monitor)
            {
                display = d;
                break;
            }
        }

        if (display is null)
        {
            return null;
        }

        uint explorer = GaugeWindowIdentityReader.ProcessOf(NativeMethods.FindWindowW("Shell_TrayWnd", null));
        return new ForegroundWindowReading(
            GaugeWindowIdentityReader.Read(window, explorer), Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom),
            display.Bounds, DisplayNames.Short(display, displays));
    }

    // The work area of the display a card's anchor is on, over the displays connected now.
    public static Rectangle WorkAreaFor(Rectangle anchor)
    {
        DisplayReading reading = new SystemDisplaySource().Read();
        List<DisplayArea> areas = reading.Displays.Select(d => new DisplayArea(d.Bounds, d.WorkArea, d.IsPrimary)).ToList();
        return WidgetCardPlacement.WorkAreaFor(anchor, areas, Rectangle.Empty);
    }
}
