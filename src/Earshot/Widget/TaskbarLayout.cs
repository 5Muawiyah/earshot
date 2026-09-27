using Earshot.Contracts;
using Earshot.Popup;

namespace Earshot.Widget;

// One taskbar reading, as ITaskbarReader hands it to the controller. Every rectangle is in physical
// screen pixels. Occupied excludes the frame containers themselves (Shell_TrayWnd, the input site,
// TaskbarFrame): only leaf elements with a non-empty, on-screen rectangle. StartButton is the Start
// element's own rectangle when it was found; null when it was not (GaugePlacement then falls back to
// the first merged interval).
internal sealed record TaskbarLayout(
    nint TaskbarHandle,
    Rectangle Taskbar,
    TaskbarEdge Edge,
    bool AutoHide,
    Rectangle MonitorBounds,
    IReadOnlyList<Rectangle> Occupied,
    Rectangle? StartButton,
    int Dpi,
    int NotificationState,     // Shell.QUNS_*
    bool Covered,               // WindowFromPoint at the probe point returns a monitor-sized window that is not Shell_TrayWnd
    bool? GaugeCentreIsGauge); // null when no gauge is currently shown; else whether WindowFromPoint at its centre is the gauge

// Why a read failed, and the raw step. Every case maps to one of GaugeController's Hidden reasons.
internal enum TaskbarReadFailureStep { NoTaskbar, TaskbarRect, Occupants, Notification, Dpi, GaugeProbe }

internal sealed record TaskbarReadFailure(TaskbarReadFailureStep Step, StepOutcome Outcome);
