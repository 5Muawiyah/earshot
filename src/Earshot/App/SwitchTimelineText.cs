using System.Globalization;
using Earshot.Contracts;

namespace Earshot.App;

// The one place the log lines of a measured switch are built, so the live test that reads them and the tests that
// pin them cannot drift from the application. Invariant culture, whole milliseconds, "-" for a figure that does not
// exist (a phase the path did not run, or a clock that failed). No device name, address or container id is ever
// part of a line. The shapes, byte for byte:
//
//   Switch to-pc: active after <ms> ms (trigger <t>, path <p>, queued <ms>, first-pass <ms>, status <ms>, allow <ms>, endpoints <ms>, connect <ms>, protection <ms>, total <ms>, accepted <utc>).
//   Switch to-pc: not active (outcome <OpStatus>, trigger <t>, path <p>, blocked-again <yes|no|not-needed>, total <ms>, accepted <utc>).
//   Switch to-phone: released after <ms> ms, at rest after <ms> ms (trigger <t>, queued <ms>, block <ms>, total <ms>, accepted <utc>).
//   Switch to-phone: released after <ms> ms, not at rest: <reason> (trigger <t>, queued <ms>, block <ms>, total <ms>, accepted <utc>).
//   Switch to-phone: not released (outcome <OpStatus>, at rest <yes|no: reason>, trigger <t>, total <ms>, accepted <utc>).
//   Switch <to-pc|to-phone>: cancelled (trigger <t>, total <ms>, accepted <utc>).
internal static class SwitchTimelineText
{
    // The words a released switch gives for not being at rest. Fixed, so a reader can match on them.
    public const string ReasonBlockAtBootOff = "Block at boot is off";
    public const string ReasonBlockDidNotTake = "the block did not take";
    public const string ReasonStatusUnreadable = "the boot block status could not be read";
    public const string ReasonNotSetUp = "not set up";

    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    // The log line for a finished switch.
    public static string Format(SwitchTimeline t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Connect ? FormatToPc(t) : FormatToPhone(t);
    }

    // True for a line that is a good outcome (Info), false for one worth a warning.
    public static bool IsGood(SwitchTimeline t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Cancelled)
        {
            return true;
        }

        return t.Connect ? t.ActiveReached : t.ReleasedSeen && t.AtRest;
    }

    public static string Trigger(SwitchTrigger trigger) => trigger switch
    {
        SwitchTrigger.Click => "click",
        SwitchTrigger.Menu => "menu",
        SwitchTrigger.ShortcutToggle => "shortcut-toggle",
        SwitchTrigger.ShortcutToPc => "shortcut-to-pc",
        SwitchTrigger.ShortcutToPhone => "shortcut-to-phone",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger."),
    };

    public static string Path(SwitchPath path) => path switch
    {
        SwitchPath.None => "-",
        SwitchPath.Already => "already",
        SwitchPath.Direct => "direct",
        SwitchPath.AllowFirst => "allow-first",
        SwitchPath.HandsFreeAssisted => "handsfree-assisted",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown path."),
    };

    private static string FormatToPc(SwitchTimeline t)
    {
        if (t.Cancelled)
        {
            return Cancelled("to-pc", t);
        }

        if (t.ActiveReached)
        {
            return "Switch to-pc: active after " + Ms(t.ActiveAfter) + " ms (trigger " + Trigger(t.Trigger) +
                   ", path " + Path(t.Path) +
                   ", queued " + Ms(t.Phase(SwitchPhase.Queued)) +
                   ", first-pass " + Ms(t.Phase(SwitchPhase.FirstPass)) +
                   ", status " + Ms(t.Phase(SwitchPhase.Status)) +
                   ", allow " + Ms(t.Phase(SwitchPhase.Allow)) +
                   ", endpoints " + Ms(t.Phase(SwitchPhase.Endpoints)) +
                   ", connect " + Ms(t.Phase(SwitchPhase.Connect)) +
                   ", protection " + Ms(t.Phase(SwitchPhase.Protection)) +
                   ", total " + Ms(t.Total) + ", accepted " + Utc(t) + ").";
        }

        return "Switch to-pc: not active (outcome " + Outcome(t) + ", trigger " + Trigger(t.Trigger) +
               ", path " + Path(t.Path) + ", blocked-again " + BlockedAgain(t.BlockedAgain) +
               ", total " + Ms(t.Total) + ", accepted " + Utc(t) + ").";
    }

    private static string FormatToPhone(SwitchTimeline t)
    {
        if (t.Cancelled)
        {
            return Cancelled("to-phone", t);
        }

        if (t.ReleasedSeen)
        {
            string rest = t.AtRest
                ? "at rest after " + Ms(t.AtRestAfter) + " ms"
                : "not at rest: " + (t.NotAtRestReason ?? ReasonBlockDidNotTake);
            return "Switch to-phone: released after " + Ms(t.ReleasedAfter) + " ms, " + rest +
                   " (trigger " + Trigger(t.Trigger) +
                   ", queued " + Ms(t.Phase(SwitchPhase.Queued)) +
                   ", block " + Ms(t.Phase(SwitchPhase.Block)) +
                   ", total " + Ms(t.Total) + ", accepted " + Utc(t) + ").";
        }

        string atRest = t.AtRest ? "yes" : "no: " + (t.NotAtRestReason ?? ReasonBlockDidNotTake);
        return "Switch to-phone: not released (outcome " + Outcome(t) + ", at rest " + atRest +
               ", trigger " + Trigger(t.Trigger) + ", total " + Ms(t.Total) + ", accepted " + Utc(t) + ").";
    }

    private static string Cancelled(string direction, SwitchTimeline t) =>
        "Switch " + direction + ": cancelled (trigger " + Trigger(t.Trigger) + ", total " + Ms(t.Total) + ", accepted " + Utc(t) + ").";

    private static string Outcome(SwitchTimeline t) =>
        t.Outcome is { } status ? status.ToString() : "-";

    private static string BlockedAgain(SwitchBlockedAgain value) => value switch
    {
        SwitchBlockedAgain.Yes => "yes",
        SwitchBlockedAgain.No => "no",
        _ => "not-needed",
    };

    // Whole milliseconds, truncated, so 1719.9 ms reads 1719 and a figure is never rounded up to look slower.
    private static string Ms(TimeSpan? span) =>
        span is { } value ? ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : "-";

    private static string Utc(SwitchTimeline t) =>
        t.AcceptedUtc.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);
}
