using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Popup;

// SHQueryUserNotificationState as it returned: the HRESULT and, when that succeeded, a QUNS_* value.
internal readonly record struct NotificationStateReading(int HResult, int State);

// What the card presenter reads from Windows for each card. The system implementation is below; tests
// supply their own.
internal interface ICardEnvironment
{
    // Cursor, displays and taskbar, in physical pixels.
    PlacementScene ReadScene();

    // The effective DPI of a display.
    int DpiFor(DisplayArea display);

    NotificationStateReading QueryNotificationState();

    CardPalette ReadPalette();
}

// Reads the desktop for the card. UI thread only (Cursor and Screen are WinForms types).
//
// In a per-monitor DPI aware process Screen bounds, the cursor position and the SHAppBarMessage rectangle
// are all physical pixels, the same space SetWindowPos uses.
// https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows
//
// A failed read is logged once per distinct problem and placement falls back (the work area alone for a
// missing taskbar rectangle, the system DPI for a display DPI), so a card still appears.
internal sealed class SystemCardEnvironment : ICardEnvironment
{
    private readonly ILog _log;
    private readonly ThemeReader _theme;
    private string? _lastTaskbarProblem;
    private string? _lastDpiProblem;

    public SystemCardEnvironment(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _theme = new ThemeReader(log);
    }

    public PlacementScene ReadScene()
    {
        DisplayArea[] displays = Array.ConvertAll(Screen.AllScreens, s => new DisplayArea(s.Bounds, s.WorkingArea, s.Primary));

        // ABM_GETTASKBARPOS needs only cbSize and returns TRUE on success.
        // https://learn.microsoft.com/en-us/windows/win32/shell/abm-gettaskbarpos
        Rectangle? taskbar = null;
        bool autoHide = false;
        var position = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (Shell.SHAppBarMessage(Shell.ABM_GETTASKBARPOS, ref position) != 0)
        {
            taskbar = Rectangle.FromLTRB(position.rc.left, position.rc.top, position.rc.right, position.rc.bottom);

            // ABM_GETSTATE returns the autohide and always-on-top bits.
            // https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate
            var state = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            autoHide = (Shell.SHAppBarMessage(Shell.ABM_GETSTATE, ref state) & Shell.ABS_AUTOHIDE) != 0;
            Note(ref _lastTaskbarProblem, null);
        }
        else
        {
            // No taskbar, for example while Explorer restarts. SHAppBarMessage sets no error code.
            Note(ref _lastTaskbarProblem, "Card placement: SHAppBarMessage(ABM_GETTASKBARPOS) failed, so the card is placed from the work area alone.");
        }

        return new PlacementScene(Cursor.Position, displays, taskbar, autoHide);
    }

    public int DpiFor(DisplayArea display)
    {
        Rectangle b = display.Bounds;
        var rect = new RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom };
        nint monitor = Shell.MonitorFromRect(in rect, Shell.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return SystemDpi("MonitorFromRect found no monitor for the display at " + Describe(b));
        }

        // https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor
        int hr = Shell.GetDpiForMonitor(monitor, Shell.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        if (hr < 0 || dpiX == 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("get-dpi-for-monitor:card", hr);
            return SystemDpi(TrayReport.DescribeStep(step) + " for the display at " + Describe(b));
        }

        Note(ref _lastDpiProblem, null);
        return (int)dpiX;
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
    public NotificationStateReading QueryNotificationState()
    {
        int hr = Shell.SHQueryUserNotificationState(out int state);
        return new NotificationStateReading(hr, state);
    }

    public CardPalette ReadPalette() => CardTheme.Current(_theme);

    private int SystemDpi(string problem)
    {
        uint dpi = Shell.GetDpiForSystem();
        Note(ref _lastDpiProblem, "Card size: " + problem + ", so the system DPI (" + dpi.ToString(CultureInfo.InvariantCulture) + ") is used.");
        return dpi > 0 ? (int)dpi : CardPlacement.BaseDpi;
    }

    // Logs a problem when it differs from the last one of its kind; null clears it.
    private void Note(ref string? last, string? problem)
    {
        if (problem == last)
        {
            return;
        }

        last = problem;
        if (problem is not null)
        {
            _log.Warn(problem);
        }
    }

    private static string Describe(Rectangle r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y} {r.Width}x{r.Height}");
}
