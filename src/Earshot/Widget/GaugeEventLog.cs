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

    public static string HiddenNotificationState(string quns) => Hidden("NotificationState " + quns);

    public static string HiddenFullScreenNotified() => Hidden("FullScreenNotified");

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

    public static string RaisedByPoll(WindowIdentity? over) => "Gauge raised: the poll found " + Describe(over) + " over it.";

    public static string LeftUnder(string overClass) =>
        "Gauge left under " + Token(overClass) + " after a foreground change: not the taskbar.";

    public static string RaiseSkippedRateLimit() => "Gauge raise skipped: another was made under 250 ms ago.";

    public static string RaiseCapReached() => "Gauge raise cap reached; waiting for a poll to confirm.";

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
