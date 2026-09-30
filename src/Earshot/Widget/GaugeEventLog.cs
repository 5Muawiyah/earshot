using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Widget;

// What the gauge writes to the log every time it shows, moves, hides or is put back on top, so a day of use
// leaves the cause of any flicker in the file. One line per event; every line starts "Gauge " and names its
// reason. The log adds the time to each line, so a line carries none of its own.
//
// A window is only ever named by its class, never its title (a title can carry a document name or a chat
// participant). A window that belongs to Explorer says so after the class.
//
// The wording is pinned by GaugeEventLogTests; a person searching the log for one of these has to be able to
// rely on it.
internal static class GaugeEventLog
{
    public const string Prefix = "Gauge ";

    // Why the gauge came back on screen.
    public const string ShownFirstLayout = "first layout";
    public const string ShownTaskbarBack = "taskbar back";
    public const string ShownFullScreenClosed = "full-screen app closed";
    public const string ShownFreeSpaceBack = "free space back";
    public const string ShownReadRecovered = "read recovered";
    public const string ShownUncovered = "taskbar uncovered";
    public const string ShownWindowRecreated = "window recreated";

    public static string Shown(Rectangle bounds, string reason) => "Gauge shown at " + Rect(bounds) + " (" + reason + ").";

    public static string Moved(Rectangle bounds) => "Gauge moved to " + Rect(bounds) + ".";

    public static string Hidden(string reason) => "Gauge hidden (" + reason + ").";

    public static string HiddenNoTaskbar() => Hidden("NoTaskbar");

    public static string HiddenReadFailed(string step, StepOutcome outcome, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Hidden("ReadFailed " + step + " " + outcome.CodeName + " (" + Number(outcome.Code) + ") after " +
            Number(consecutiveFailures) + " consecutive failures");
    }

    public static string HiddenNoFreeSpace() => Hidden("NoFreeSpace");

    public static string HiddenCovered(WindowIdentity? window) => Hidden("Covered by " + Describe(window));

    // fullScreenDisplay: the plain name of the display the full-screen window is on, given only while more than one
    // display is connected (with one there is nothing to tell apart), else null.
    public static string HiddenNotificationState(string quns, string? fullScreenDisplay = null) =>
        Hidden("NotificationState " + quns + OnDisplay(fullScreenDisplay));

    public static string HiddenFullScreenNotified(string? fullScreenDisplay = null) =>
        Hidden("FullScreenNotified" + OnDisplay(fullScreenDisplay));

    // A full-screen signal that is about another display: the gauge stays. Classes only.
    public static string FullScreenOnOtherDisplay(string signal, WindowIdentity? window, string windowDisplay, string gaugeDisplay) =>
        "Gauge stays shown: " + signal + " is about " + Describe(window) + " on " + windowDisplay +
        ", not the gauge's display (" + gaugeDisplay + ").";

    // The chosen display is not the one the gauge is on.
    public static string DisplayNotConnected() => "Gauge display: the chosen display is not connected, so the main display's taskbar is used.";

    public static string DisplayTaskbarNotShown() => "Gauge display: the chosen display shows no taskbar, so the main display's taskbar is used.";

    public static string DisplayBack(string display) => "Gauge display: the chosen display is back (" + display + ").";

    public static string DisplayProblem(StepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return "Gauge display: " + outcome.Step + " " + outcome.CodeName + " (" + Number(outcome.Code) + ")" +
            (outcome.Detail is null ? "" : " " + outcome.Detail);
    }

    private static string OnDisplay(string? display) => string.IsNullOrEmpty(display) ? "" : ", full-screen window on " + display;

    public static string HiddenWindowFailed(StepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Hidden("WindowFailed " + outcome.CodeName + " (" + Number(outcome.Code) + ")");
    }

    public static string HiddenSettingOff() => Hidden("SettingOff");

    public static string KeptAfterFailedRead(Rectangle bounds, string step, StepOutcome outcome, int failures, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return "Gauge kept at " + Rect(bounds) + " after a failed read (" + step + " " + outcome.CodeName + " (" +
            Number(outcome.Code) + "), " + Number(failures) + " of " + Number(tolerance) + ").";
    }

    public static string RaisedAfterForegroundChange(WindowIdentity? over, string foregroundClass) =>
        "Gauge raised: " + Describe(over) + " was over it after a foreground change to " + Token(foregroundClass) + ".";

    public static string RaisedAfterShellWindow(WindowIdentity? over, string windowClass, bool shown) =>
        "Gauge raised: " + Describe(over) + " was over it after Explorer " + (shown ? "showed" : "hid") + " a window of class " + Token(windowClass) + ".";

    public static string RaisedOnRecheck(WindowIdentity? over) => "Gauge raised: " + Describe(over) + " was over it on a recheck.";

    public static string RaisedByPoll(WindowIdentity? over) => "Gauge raised: the poll found " + Describe(over) + " over it.";

    public static string LeftUnder(string overClass) =>
        "Gauge left under " + Token(overClass) + " after a foreground change: not the taskbar.";

    public static string LeftUnderOtherwise(string overClass) =>
        "Gauge left under " + Token(overClass) + ": not the taskbar.";

    public static string RaiseCapReached(int raises, int seconds) =>
        "Gauge raise limit reached (" + Number(raises) + " in " + Number(seconds) + " s); the next check will try again.";

    public static string PlacementFailed(PlacementFailure failure) => "Gauge placement found no room: " + failure + ".";

    // "x,y WxH" in physical pixels.
    public static string Rect(Rectangle r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y} {r.Width}x{r.Height}");

    // A window's class, with "(Explorer)" after it when Explorer owns it; "an unknown window" when there is
    // nothing to say.
    public static string Describe(WindowIdentity? window) => window is { } w
        ? Token(w.ClassName) + (w.BelongsToExplorer ? " (Explorer)" : "")
        : "an unknown window";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A class name, cut to letters, digits and . _ : $ # - so no character in it can end the sentence or
    // start another field.
    private static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "?";
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '$' or '#' or '-' ? c : '_');
        }

        return sb.ToString();
    }
}
