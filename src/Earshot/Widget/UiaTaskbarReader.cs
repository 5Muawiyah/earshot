using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;

namespace Earshot.Widget;

// The real ITaskbarReader: UI Automation over Shell_TrayWnd for the occupied rectangles (D2), plus the
// appbar rectangle, DPI, notification state, and the two WindowFromPoint checks (covered, gauge centre).
// Read-only throughout; never touches a device. Runs on the UIA worker thread only (TaskbarWatcher owns
// it): the IUIAutomation object is created once, the first time Read is called on that thread, and never
// touched from anywhere else (F6).
internal sealed class UiaTaskbarReader : ITaskbarReader
{
    private const string ShellTrayWndClass = "Shell_TrayWnd";
    private const string StartButtonAutomationId = "StartButton";

    private IUIAutomation? _automation;

    // Test seam only: counts real constructions so WidgetRealSurfaceGuardTests can prove a test harness
    // never builds this class in place of a fake. Never read or reset in production.
    internal static int ConstructionCount;

    public UiaTaskbarReader()
    {
        Interlocked.Increment(ref ConstructionCount);
    }

    public ITaskbarReader.Result Read(ShownGauge? shownGauge)
    {
        nint trayHandle = NativeMethods.FindWindowW(ShellTrayWndClass, null);
        if (trayHandle == 0)
        {
            return Fail(TaskbarReadFailureStep.NoTaskbar, StepOutcomes.FromWin32("find-window:Shell_TrayWnd", 0, ok: false));
        }

        if (!TryReadTaskbarRect(out Rectangle taskbar, out bool autoHide, out StepOutcome? rectFailure))
        {
            return Fail(TaskbarReadFailureStep.TaskbarRect, rectFailure!);
        }

        if (!TryReadOccupants(trayHandle, taskbar, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? occupantsFailure))
        {
            return Fail(TaskbarReadFailureStep.Occupants, occupantsFailure!);
        }

        int hrQuns = Shell.SHQueryUserNotificationState(out int quns);
        if (hrQuns < 0)
        {
            return Fail(TaskbarReadFailureStep.Notification, StepOutcomes.FromHResult("sh-query-user-notification-state", hrQuns));
        }

        uint dpi = TaskbarDpi.Read(out _);
        Rectangle monitorBounds = MonitorBoundsFor(taskbar);
        TaskbarEdge edge = CardPlacement.EdgeOf(taskbar, monitorBounds);

        Point probe = ProbePoint(taskbar);
        nint atProbe = NativeMethods.WindowFromPoint(new POINT { x = probe.X, y = probe.Y });
        bool covered = atProbe != 0 && atProbe != trayHandle && IsMonitorSized(atProbe, monitorBounds);

        bool? gaugeCentreIsGauge = null;
        if (shownGauge is { } gauge)
        {
            Point centre = new(gauge.Bounds.X + (gauge.Bounds.Width / 2), gauge.Bounds.Y + (gauge.Bounds.Height / 2));
            nint atCentre = NativeMethods.WindowFromPoint(new POINT { x = centre.X, y = centre.Y });
            nint rootAtCentre = NativeMethods.GetAncestor(atCentre, NativeMethods.GA_ROOT);
            nint effective = rootAtCentre != 0 ? rootAtCentre : atCentre;
            gaugeCentreIsGauge = atCentre == gauge.Handle || effective == gauge.Handle;
        }

        var layout = new TaskbarLayout(
            trayHandle, taskbar, edge, autoHide, monitorBounds, occupied, startButton,
            (int)(dpi > 0 ? dpi : CardPlacement.BaseDpi), quns, covered, gaugeCentreIsGauge);
        return ITaskbarReader.Result.Ok(layout);
    }

    // Internal so a real-execution test (12.3) can exercise the COM path against a window of its own,
    // without going through Shell_TrayWnd.
    internal bool TryReadOccupants(nint hwnd, Rectangle containerRect, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure)
    {
        occupied = [];
        startButton = null;
        failure = null;

        if (_automation is null)
        {
            int hrCreate = ComActivation.Create(UiAutomation.CLSID_CUIAutomation, ComActivation.CLSCTX_INPROC_SERVER, out IUIAutomation? automation);
            if (hrCreate < 0 || automation is null)
            {
                failure = StepOutcomes.FromHResult("cocreateinstance:CUIAutomation", hrCreate);
                return false;
            }

            _automation = automation;
        }

        IUIAutomation automationRef = _automation;
        int hr = automationRef.ElementFromHandle(hwnd, out IUIAutomationElement? root);
        if (hr < 0 || root is null)
        {
            failure = StepOutcomes.FromHResult("uia:element-from-handle", hr);
            return false;
        }

        hr = automationRef.CreateTrueCondition(out IUIAutomationCondition? trueCondition);
        if (hr < 0 || trueCondition is null)
        {
            failure = StepOutcomes.FromHResult("uia:create-true-condition", hr);
            return false;
        }

        hr = automationRef.CreateCacheRequest(out IUIAutomationCacheRequest? cacheRequest);
        if (hr < 0 || cacheRequest is null)
        {
            failure = StepOutcomes.FromHResult("uia:create-cache-request", hr);
            return false;
        }

        hr = cacheRequest.AddProperty(UiAutomation.UIA_BoundingRectanglePropertyId);
        if (hr >= 0)
        {
            hr = cacheRequest.AddProperty(UiAutomation.UIA_IsOffscreenPropertyId);
        }

        if (hr >= 0)
        {
            hr = cacheRequest.AddProperty(UiAutomation.UIA_AutomationIdPropertyId);
        }

        if (hr < 0)
        {
            failure = StepOutcomes.FromHResult("uia:add-property", hr);
            return false;
        }

        // TreeScope_Descendants is FindAllBuildCache's own scope parameter here, not
        // cacheRequest.put_TreeScope: see that method's declaration in UiAutomation.cs for why. A local
        // probe against the real Shell_TrayWnd confirmed every occupant's three cached properties below
        // come back S_OK read this way.
        hr = root.FindAllBuildCache(UiAutomation.TreeScope_Descendants, trueCondition, cacheRequest, out IUIAutomationElementArray? found);
        if (hr < 0 || found is null)
        {
            failure = StepOutcomes.FromHResult("uia:find-all-build-cache", hr);
            return false;
        }

        hr = found.get_Length(out int count);
        if (hr < 0)
        {
            failure = StepOutcomes.FromHResult("uia:get-length", hr);
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (found.GetElement(i, out IUIAutomationElement? element) < 0 || element is null)
            {
                continue;
            }

            if (element.GetCachedPropertyValue(UiAutomation.UIA_BoundingRectanglePropertyId, out object? rectValue) < 0 ||
                rectValue is not double[] { Length: 4 } bounds)
            {
                // VT_EMPTY: the element is not currently displaying UI (F7). Not an occupant.
                continue;
            }

            var rect = new Rectangle(
                (int)Math.Round(bounds[0]), (int)Math.Round(bounds[1]),
                (int)Math.Round(bounds[2]), (int)Math.Round(bounds[3]));
            if (rect.Width <= 0 || rect.Height <= 0 || rect == containerRect)
            {
                // A frame container (Shell_TrayWnd, the input site, TaskbarFrame) whose rectangle equals
                // the taskbar's own: not an occupant.
                continue;
            }

            if (element.GetCachedPropertyValue(UiAutomation.UIA_IsOffscreenPropertyId, out object? offscreenValue) >= 0 &&
                offscreenValue is bool { } offscreen && offscreen)
            {
                continue;
            }

            occupied.Add(rect);

            if (element.GetCachedPropertyValue(UiAutomation.UIA_AutomationIdPropertyId, out object? idValue) >= 0 &&
                idValue is string id && string.Equals(id, StartButtonAutomationId, StringComparison.Ordinal))
            {
                startButton = rect;
            }
        }

        return true;
    }

    private static bool TryReadTaskbarRect(out Rectangle taskbar, out bool autoHide, out StepOutcome? failure)
    {
        taskbar = Rectangle.Empty;
        autoHide = false;
        failure = null;

        var position = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (Shell.SHAppBarMessage(Shell.ABM_GETTASKBARPOS, ref position) == 0)
        {
            // SHAppBarMessage documents no error code for this failure.
            failure = StepOutcomes.FromWin32("sh-app-bar-message:abm-gettaskbarpos", 0, "ABM_GETTASKBARPOS returned FALSE.", ok: false);
            return false;
        }

        taskbar = Rectangle.FromLTRB(position.rc.left, position.rc.top, position.rc.right, position.rc.bottom);

        var state = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        autoHide = (Shell.SHAppBarMessage(Shell.ABM_GETSTATE, ref state) & Shell.ABS_AUTOHIDE) != 0;
        return true;
    }

    private static Rectangle MonitorBoundsFor(Rectangle taskbar)
    {
        var rect = new RECT { left = taskbar.Left, top = taskbar.Top, right = taskbar.Right, bottom = taskbar.Bottom };
        nint monitor = Shell.MonitorFromRect(in rect, Shell.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return taskbar;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!Shell.GetMonitorInfo(monitor, ref info))
        {
            return taskbar;
        }

        return Rectangle.FromLTRB(info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right, info.rcMonitor.bottom);
    }

    // A point inside the taskbar, used only to ask what window is on top there (2.4). The taskbar's own
    // centre, not the gauge's candidate run: the reader does not know the placement yet (GaugePlacement
    // runs afterwards, on the layout this produces), so this is a general "is the bar covered" probe
    // rather than the exact free-run point the design narrative describes.
    private static Point ProbePoint(Rectangle taskbar) =>
        new(taskbar.Left + (taskbar.Width / 2), taskbar.Top + (taskbar.Height / 2));

    private static bool IsMonitorSized(nint hwnd, Rectangle monitorBounds)
    {
        nint root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        nint target = root != 0 ? root : hwnd;
        if (!NativeMethods.GetWindowRect(target, out RECT rect))
        {
            return false;
        }

        var bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        return bounds.Width >= monitorBounds.Width && bounds.Height >= monitorBounds.Height;
    }

    private static ITaskbarReader.Result Fail(TaskbarReadFailureStep step, StepOutcome outcome) =>
        ITaskbarReader.Result.Fail(new TaskbarReadFailure(step, outcome));
}
