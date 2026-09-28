using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;

namespace Earshot.Widget;

// The real ITaskbarReader: UI Automation over Shell_TrayWnd for the occupied rectangles, plus the appbar
// rectangle, DPI, notification state, and the two WindowFromPoint checks (covered, gauge centre).
// Read-only throughout; never touches a device. Runs on the UIA worker thread only (TaskbarWatcher owns
// it): the IUIAutomation object is created once, the first time Read is called on that thread, and never
// touched from anywhere else.
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

        if (!TryReadOccupants(trayHandle, taskbar, Earshot.Audio.ComRelease.Rcw, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? occupantsFailure))
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

    // Internal so UiaTaskbarReaderTests's real-execution test can call this directly against the real
    // Shell_TrayWnd, without going through Read's other steps (the appbar rectangle, notification state).
    // release frees every per-read COM object created below (root, trueCondition, cacheRequest, found, and
    // each per-element IUIAutomationElement inside TryReadElements) before this method returns, on every
    // path: production passes Earshot.Audio.ComRelease.Rcw (Marshal.ReleaseComObject), the same shared
    // helper CoreAudioEndpointSource and TopologyWalk already use for exactly this reason (their own
    // comment: their managed test doubles are not real COM objects, and Marshal.ReleaseComObject throws
    // ArgumentException against one). The cached _automation field above is the only UIA object this reader
    // keeps alive across reads, so it is never passed to release.
    internal bool TryReadOccupants(nint hwnd, Rectangle containerRect, Action<object> release, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure)
    {
        ArgumentNullException.ThrowIfNull(release);
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

        try
        {
            hr = automationRef.CreateTrueCondition(out IUIAutomationCondition? trueCondition);
            if (hr < 0 || trueCondition is null)
            {
                failure = StepOutcomes.FromHResult("uia:create-true-condition", hr);
                return false;
            }

            try
            {
                hr = automationRef.CreateCacheRequest(out IUIAutomationCacheRequest? cacheRequest);
                if (hr < 0 || cacheRequest is null)
                {
                    failure = StepOutcomes.FromHResult("uia:create-cache-request", hr);
                    return false;
                }

                try
                {
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
                    // cacheRequest.put_TreeScope: see that method's declaration in UiAutomation.cs for why. A
                    // local probe against the real Shell_TrayWnd confirmed every occupant's three cached
                    // properties below come back S_OK read this way.
                    hr = root.FindAllBuildCache(UiAutomation.TreeScope_Descendants, trueCondition, cacheRequest, out IUIAutomationElementArray? found);
                    if (hr < 0 || found is null)
                    {
                        failure = StepOutcomes.FromHResult("uia:find-all-build-cache", hr);
                        return false;
                    }

                    try
                    {
                        return TryReadElements(found, containerRect, release, out occupied, out startButton, out failure);
                    }
                    finally
                    {
                        release(found);
                    }
                }
                finally
                {
                    release(cacheRequest);
                }
            }
            finally
            {
                release(trueCondition);
            }
        }
        finally
        {
            release(root);
        }
    }

    // Split out from TryReadOccupants so a test can drive the per-element logic against a fake
    // IUIAutomationElementArray/IUIAutomationElement, without a real Shell_TrayWnd or a way to make the
    // real COM pipeline fail partway through an enumeration on demand. release frees each per-element
    // IUIAutomationElement GetElement returns, the same delegate TryReadOccupants passes through.
    internal static bool TryReadElements(IUIAutomationElementArray found, Rectangle containerRect, Action<object> release, out List<Rectangle> occupied, out Rectangle? startButton, out StepOutcome? failure)
    {
        ArgumentNullException.ThrowIfNull(release);
        occupied = [];
        startButton = null;
        failure = null;

        int hr = found.get_Length(out int count);
        if (hr < 0)
        {
            failure = StepOutcomes.FromHResult("uia:get-length", hr);
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            hr = found.GetElement(i, out IUIAutomationElement? element);
            if (hr < 0)
            {
                // Fails the whole read rather than skipping this element: an element whose properties
                // cannot be read is not known to be free space, so a gauge must not be placed as though it
                // were. Silently continuing here (the old behaviour) failed open.
                failure = StepOutcomes.FromHResult("uia:get-element", hr);
                return false;
            }

            if (element is null)
            {
                // A success code with no element is not documented to happen; treated as a failure for the
                // same fail-closed reason, rather than silently skipping a slot this occupant list cannot
                // account for.
                failure = StepOutcomes.FromHResult("uia:get-element:null-element", 0, "GetElement returned S_OK with a null element.", ok: false);
                return false;
            }

            try
            {
                hr = element.GetCachedPropertyValue(UiAutomation.UIA_BoundingRectanglePropertyId, out object? rectValue);
                if (hr < 0)
                {
                    failure = StepOutcomes.FromHResult("uia:get-cached-property:bounding-rectangle", hr);
                    return false;
                }

                if (rectValue is not double[] { Length: 4 } bounds)
                {
                    // VT_EMPTY on a successful read: the element is not currently displaying UI (documented
                    // behaviour of the BoundingRectangle property, not a failure). Not an occupant.
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

                hr = element.GetCachedPropertyValue(UiAutomation.UIA_IsOffscreenPropertyId, out object? offscreenValue);
                if (hr < 0)
                {
                    failure = StepOutcomes.FromHResult("uia:get-cached-property:is-offscreen", hr);
                    return false;
                }

                if (offscreenValue is bool { } offscreen && offscreen)
                {
                    continue;
                }

                occupied.Add(rect);

                hr = element.GetCachedPropertyValue(UiAutomation.UIA_AutomationIdPropertyId, out object? idValue);
                if (hr < 0)
                {
                    failure = StepOutcomes.FromHResult("uia:get-cached-property:automation-id", hr);
                    return false;
                }

                if (idValue is string id && string.Equals(id, StartButtonAutomationId, StringComparison.Ordinal))
                {
                    startButton = rect;
                }
            }
            finally
            {
                release(element);
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

    // A point inside the taskbar, used only to ask what window is on top there. The taskbar's own
    // centre, not the gauge's candidate run: the reader does not know the placement yet (GaugePlacement
    // runs afterwards, on the layout this produces), so this is a general "is the bar covered" probe
    // rather than a point inside wherever the gauge will actually be placed.
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
