using System.Globalization;

namespace Earshot.Widget.EarPause;

// Every line pause on leave writes to the log, in one place, so the live-test fakes and the scripts that parse
// them are pinned against the exact text this formats. No device name, address or container id appears in any of
// them; the app is named only by the id Windows Media Controls gives it, the same way auto-pause's line does.
internal static class PauseOnLeaveText
{
    public const string Prefix = "Pause on leave: ";

    // The reasons a leave did not pause anything. Each is a whole clause that follows "not paused: ".
    public const string ReasonOff = "Pause when AirPods leave this PC is off";
    public const string ReasonNoReading = "no reading of the AirPods audio was taken before they left";
    public const string ReasonNoneToPause = "no media session is playing";
    public const string ReasonChangeInFlight = "Earshot was changing the AirPods when they went away";

    public static string ReasonNotPlaying(RenderActivityState last) =>
        "this PC was not playing to them (last reading: " + (last == RenderActivityState.Playing ? "playing" : last == RenderActivityState.Silent ? "silent" : "unknown") + ")";

    public static string ReasonStaleReading(TimeSpan age) =>
        "the last reading of the AirPods audio was " + Ms(age) + " ms old";

    public static string ReasonUnreadable(string codes) => "the AirPods audio could not be read (" + codes + ")";

    public static string ReasonAmbiguous(int playing) =>
        playing.ToString(CultureInfo.InvariantCulture) + " media sessions are playing, so none was paused";

    public static string ReasonRefused(string app) => "Windows did not take the pause for " + app;

    public static string ReasonTooSlow(TimeSpan cap) => "reading and pausing took longer than " + Ms(cap) + " ms";

    // A leave Earshot did not start: the change was seen at seenAt, the pause finished after it, and the reading that
    // said this PC was playing to the AirPods was taken sampleAge before the change was seen.
    public static string LeftPaused(DateTimeOffset seenAt, string app, TimeSpan after, TimeSpan sampleAge) =>
        Prefix + "the AirPods left this PC (change seen at " + Utc(seenAt) + "). Paused " + app + " " + Ms(after) +
        " ms after the change was seen; this PC was playing to them at the last reading, " + Ms(sampleAge) + " ms before.";

    public static string LeftNotPaused(DateTimeOffset seenAt, string reason) =>
        Prefix + "the AirPods left this PC (change seen at " + Utc(seenAt) + "). Not paused: " + reason + ".";

    // A leave Earshot starts itself (why: Disconnect, hand-back at shut down, sleep or Exit, fast switch): paused
    // first, so the sound does not jump to the speakers when the AirPods go.
    public static string OwnPaused(string why, string app, TimeSpan took) =>
        Prefix + "before Earshot lets the AirPods go (" + why + "), paused " + app + " in " + Ms(took) + " ms; this PC was playing to them.";

    public static string OwnNotPaused(string why, string reason) =>
        Prefix + "before Earshot lets the AirPods go (" + why + "). Not paused: " + reason + ".";

    // The leave that follows an own pause or decision, already decided, so nothing is done twice.
    public static string AlreadyDecided(DateTimeOffset seenAt) =>
        Prefix + "the AirPods left this PC (change seen at " + Utc(seenAt) + "); already decided before Earshot let them go.";

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan t) => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}
