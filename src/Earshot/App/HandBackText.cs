using System.Globalization;
using Earshot.Contracts;

namespace Earshot.App;

// Every line the hand-back writes to the log, in one place, so the fakes in the live-test harness and the
// scripts that parse them can be pinned against the exact same text this formats. No device name, address or
// container id ever appears in any of these: the AirPods are never named on the way out.
internal static class HandBackText
{
    private const string ShutdownPrefix = "Hand-back (shutdown): ";
    private const string SleepPrefix = "Hand-back (sleep): ";
    private const string ResumePrefix = "Hand-back (resume): ";

    public static string Prefix(HandBackTrigger trigger) => trigger == HandBackTrigger.SessionEnd ? ShutdownPrefix : SleepPrefix;

    // Written once, at the first session-end or suspend message reached while the setting is off, so a reader
    // can tell "off" from "never reached".
    public static string Off(HandBackTrigger trigger) => "Hand-back: off, so nothing runs for this " +
        (trigger == HandBackTrigger.SessionEnd ? "session end." : "suspend.");

    public static string Started(HandBackTrigger trigger, DateTimeOffset t0, string flagsOrReason, RenderState render, BlockState nodes, bool streamingHeld, bool? blockAtBoot) =>
        Prefix(trigger) + "started at " + Utc(t0) + " (" + flagsOrReason + "); render " + render + "; nodes " + nodes +
        "; streaming " + (streamingHeld ? "held" : "none") + "; Block at boot " + (blockAtBoot is { } on ? (on ? "on" : "off") : "unknown");

    public static string NothingToDisconnect(HandBackTrigger trigger) => Prefix(trigger) + "nothing to disconnect";

    // outcome: "S_OK" for a clean send, or the raw code name of the first step that was not S_OK / not attempted.
    public static string Disconnect(HandBackTrigger trigger, string outcome, bool confirmed, TimeSpan elapsed) =>
        Prefix(trigger) + "disconnect " + outcome + ", " +
        (confirmed ? "confirmed after " + Ms(elapsed) + " ms" : "not confirmed within " + Ms(elapsed) + " ms");

    public static string BlockSentAt(HandBackTrigger trigger, DateTimeOffset t1) => Prefix(trigger) + "block sent at " + Utc(t1);

    public static string BlockNotSent(HandBackTrigger trigger, string reason) => Prefix(trigger) + "block not sent: " + reason;

    // The block already queued at the query came back Failed or Partial (and is not one the gate may still land),
    // so it is sent once more before the reply returns.
    public static string QueryBlockRetried(HandBackTrigger trigger, string status) =>
        Prefix(trigger) + "the block queued at the query was " + status + ", so it is sent once more";

    public static string Finished(HandBackTrigger trigger, TimeSpan elapsed, string disconnectOutcome, string blockOutcome) =>
        Prefix(trigger) + "finished in " + Ms(elapsed) + " ms; disconnect " + disconnectOutcome + "; block " + blockOutcome;

    // stillRunning: any of "disconnect", "block", "streaming release", in that order, whichever had not returned
    // by the deadline. blockSentAt is null when the block was never sent.
    public static string CutShort(HandBackTrigger trigger, TimeSpan elapsed, IReadOnlyList<string> stillRunning, DateTimeOffset? blockSentAt) =>
        Prefix(trigger) + "cut short at " + Ms(elapsed) + " ms; still running: " + (stillRunning.Count == 0 ? "-" : string.Join(", ", stillRunning)) +
        "; block was " + (blockSentAt is { } t1 ? "sent at " + Utc(t1) : "not sent");

    public static string SessionEndingNoBlockAtQuery() =>
        "Session ending: no block issued at the query, because the AirPods are in use and Hand back is on; " +
        "the hand-back runs when Windows confirms the session is ending.";

    // The resume check's own evidence lines, the twin of the logon "connected at start-up" line.
    public static string ResumeConnected() => ResumePrefix + "connected at resume; the resume check did not hold";

    public static string ResumeBlocked() => ResumePrefix + "the nodes were enabled and not in use, so they are blocked now";

    public static string ResumeNothingToDo(string reason) => ResumePrefix + "nothing to do: " + reason;

    // Written once, at PBT_APMRESUMEAUTOMATIC, while the setting is off, so a reader can tell "off" from
    // "never reached" here too.
    public static string ResumeOff() => ResumePrefix + "off, so the resume check does not run";

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan t) => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}
