using Earshot.Contracts;
using Earshot.Popup;

namespace Earshot.Widget;

// One taskbar reading, as ITaskbarReader hands it to the controller. Every rectangle is in physical
// screen pixels. Occupied excludes the frame containers themselves (Shell_TrayWnd, the input site,
// TaskbarFrame): only leaf elements with a non-empty, on-screen rectangle. StartButton is the Start
// element's own rectangle when it was found; null when it was not. NotificationArea is the bounding box of
// the elements that belong to the notification area (the tray chevron, its icons, the clock), which the
// gauge is placed beside; null when none could be identified. CoveringWindow, WindowAtGaugeCentre and
// Foreground name a window by its class and whether Explorer owns it, only so the log can say what sat over
// the gauge or took the foreground.
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
    bool? GaugeCentreIsGauge,  // null when no gauge is currently shown; else whether WindowFromPoint at its centre is the gauge
    Rectangle? NotificationArea = null,        // the bounds of the notification area's own elements (the tray chevron, icons, clock); null when none was identified
    WindowIdentity? CoveringWindow = null,     // the window found at the probe point when Covered
    WindowIdentity? WindowAtGaugeCentre = null, // the window found at the shown gauge's centre when GaugeCentreIsGauge is false
    WindowIdentity? Foreground = null,         // the foreground window at the time of the read
    int DisplayCount = 1,                      // how many displays are connected; 1 keeps every full-screen signal as it always was
    ForegroundWindowReading? ForegroundWindow = null, // the foreground window's rectangle and display, for the full-screen rule
    bool IsSecondary = false,                  // the taskbar read is a secondary display's (Shell_SecondaryTrayWnd)
    string DisplayLabel = "",                  // the plain name of the display this taskbar is on, for the log
    DisplayFallbackReason DisplayFallback = DisplayFallbackReason.None, // why this is not the display the owner chose
    Earshot.Contracts.StepOutcome? DisplayProblem = null); // the first raw failure while reading the displays, or null

// Why a read failed, and the raw step. Every case maps to one of GaugeController's Hidden reasons.
internal enum TaskbarReadFailureStep { NoTaskbar, TaskbarRect, Occupants, Notification, Dpi, GaugeProbe, Exception }

internal sealed record TaskbarReadFailure(TaskbarReadFailureStep Step, StepOutcome Outcome);
