using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Icons;

// Builds the tray icon for the current taskbar DPI and theme, and swaps it into a NotifyIcon.
//
// Size: GetSystemMetricsForDpi(SM_CXSMICON, taskbar DPI), the documented tray icon size. The taskbar
// DPI comes from SHAppBarMessage(ABM_GETTASKBARPOS), MonitorFromRect and GetDpiForMonitor, falling
// back to GetDpiForSystem. SystemInformation.SmallIconSize has no DPI parameter and is not used.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi
// https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor
//
// The size and ink are measured once and kept until the caller asks for a remeasure, which it does on
// TaskbarCreated, WM_SETTINGCHANGE and WM_DISPLAYCHANGE; a plain state change reuses them, so it makes
// no cross-process call to the shell.
//
// Swap: assign the new icon, then dispose the old one. NotifyIcon keeps using the handle of the icon
// assigned to it, including when it adds itself again after TaskbarCreated, so the icon in use is
// never disposed. After TaskbarCreated the caller forces a swap, because NotifyIcon re-adds the old
// icon at its old size.
// https://learn.microsoft.com/en-us/windows/win32/shell/taskbar
internal sealed class TrayIconFactory
{
    private readonly ILog _log;
    private readonly ThemeReader _theme;
    private IconKey? _applied;
    private int _size;
    private Color _ink;
    private bool _measured;
    private string? _lastSizeProblem;

    private readonly record struct IconKey(GlyphState State, int Size, int InkArgb);

    public TrayIconFactory(ILog log, ThemeReader theme)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(theme);
        _log = log;
        _theme = theme;
    }

    // Puts the icon for state into notifyIcon. remeasure reads the taskbar size and theme again; force
    // assigns a new icon even when nothing changed. Returns false when no new icon was assigned.
    public bool Apply(NotifyIcon notifyIcon, GlyphState state, bool remeasure, bool force)
    {
        ArgumentNullException.ThrowIfNull(notifyIcon);

        if (remeasure || !_measured)
        {
            _size = IconSize();
            _ink = _theme.Ink();
            _measured = true;
        }

        int px = _size;
        Color ink = _ink;
        var key = new IconKey(state, px, ink.ToArgb());
        if (!force && _applied == key && notifyIcon.Icon is not null)
        {
            return false;
        }

        Icon fresh;
        try
        {
            fresh = Create(px, state, ink);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            _log.Error("The tray icon could not be drawn at " + px.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " px for " + state + ". The previous icon stays.", ex);
            return false;
        }

        Icon? old = notifyIcon.Icon;
        notifyIcon.Icon = fresh;
        old?.Dispose();
        _applied = key;
        return true;
    }

    // A new icon; the caller disposes it.
    public static Icon Create(int px, GlyphState state, Color ink)
    {
        byte[] png = EarbudGlyph.RenderPng(px, state, ink);
        return IcoWriter.Load(IcoWriter.SingleFramePng(png, px), px);
    }

    public int IconSize()
    {
        uint dpi = TaskbarDpi(out string? problem);
        int metric = Shell.GetSystemMetricsForDpi(Shell.SM_CXSMICON, dpi);
        if (metric <= 0)
        {
            problem ??= "GetSystemMetricsForDpi(SM_CXSMICON, " + dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") returned 0";
        }

        if (problem != _lastSizeProblem)
        {
            _lastSizeProblem = problem;
            if (problem is not null)
            {
                _log.Warn("Tray icon size: " + problem + ".");
            }
        }

        return SizeFor(metric, dpi);
    }

    // SM_CXSMICON when the call worked; otherwise the 16 px small icon scaled by dpi / 96, which is
    // what GetSystemMetricsForDpi returns for every standard scale factor.
    internal static int SizeFor(int metric, uint dpi)
    {
        int size = metric > 0
            ? metric
            : (int)Math.Round(16.0 * (dpi == 0 ? 96 : dpi) / 96.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(size, EarbudGlyph.MinSize, EarbudGlyph.MaxSize);
    }

    private static uint TaskbarDpi(out string? problem)
    {
        problem = null;
        var bar = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (Shell.SHAppBarMessage(Shell.ABM_GETTASKBARPOS, ref bar) == 0)
        {
            // No taskbar, for example while Explorer restarts.
            problem = "SHAppBarMessage(ABM_GETTASKBARPOS) failed, so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        nint monitor = Shell.MonitorFromRect(in bar.rc, Shell.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            problem = "MonitorFromRect found no monitor for the taskbar, so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        int hr = Shell.GetDpiForMonitor(monitor, Shell.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        if (hr < 0 || dpiX == 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("get-dpi-for-monitor", hr);
            problem = "GetDpiForMonitor returned " + step.CodeName + ", so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        return dpiX;
    }
}
