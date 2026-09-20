using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Every string the owner sees lives here (design.md architecture table). T4 (test-gui.md section
// 13) pins one part of that: the literal word "Passed" appears in this file and nowhere else, so
// a row cannot start claiming success by any other path than RowStateKind.Passed.
internal static class Copy
{
    internal const string NotRun = "Not run";
    internal const string WaitingForShutDown = "Waiting for the shut down";
    internal const string WaitingForRestart = "Waiting for the restart";
    internal const string Passed = "Passed";
    internal const string Failed = "Failed";
    internal const string Inconclusive = "Inconclusive";
    internal const string Unknown = "Unknown";
    internal const string StoppedBeforeAnyStep = "Stopped before any step";
    internal const string Locked = "Locked";

    internal static string BaseText(RowStateKind kind) => kind switch
    {
        RowStateKind.NotRun => NotRun,
        RowStateKind.WaitingForShutDown => WaitingForShutDown,
        RowStateKind.WaitingForRestart => WaitingForRestart,
        RowStateKind.Passed => Passed,
        RowStateKind.Failed => Failed,
        RowStateKind.Inconclusive => Inconclusive,
        RowStateKind.StoppedBeforeAnyStep => StoppedBeforeAnyStep,
        RowStateKind.Locked => Locked,
        _ => Unknown,
    };

    // section 10.2's own words, shown for a Locked row's detail.
    internal const string LockedDetail =
        "Locked. Run the administrator prompt check first. It proves this window can raise the " +
        "Windows permission box and read the answer before anything real depends on it.";

    // section 10.3: "Derived from result.json only: a step with elevated true, ran false and an
    // error." Two independent messages, never guessed at from anything the window itself
    // observed while the step ran.
    internal const string DeclinedElevatedPrompt =
        "You chose No on the Windows permission box, so that step did not run and nothing was " +
        "changed by it. The test is recorded as not settled.";

    internal const string EarshotNotInstalledAfterDeclinedReinstall =
        "Earshot is not installed now. Open Earshot from the release folder and set it up again before any other test.";

    // Whether test 15's own extra warning applies: its uninstall criterion passed but its
    // install-again criterion did not, so the machine may have been left with Earshot removed.
    internal static bool NeedsNotInstalledWarning(bool uninstallPassed, bool installAgainPassed) =>
        uninstallPassed && !installAgainPassed;

    // The text a row shows: the base word, plus its qualifier when it has one. A qualified pass
    // ("Passed, on an earlier build") is never shortened back to the bare word, so the amber
    // states in test-gui.md section 6.2 are always told apart from a clean one.
    internal static string RowText(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string baseText = BaseText(state.Kind);
        return state.Qualifier is null ? baseText : baseText + ", " + state.Qualifier;
    }

    // test-gui.md section 12's leftAtRest copy, shown after every run. AtRestUnknown is written
    // to say plainly, in its own words, that the machine must be treated as left in the state
    // Earshot exists to prevent: an unread node state is never shown as though it were a yes.
    internal const string AtRestYes =
        "Left at rest: yes. The AirPods nodes are blocked, so this PC will not page them at the next start.";

    internal const string AtRestNo =
        "NOT AT REST. This PC was left able to page the AirPods at the next start. That is the state Earshot " +
        "exists to prevent. Run Restore, or start Earshot, before you shut down.";

    internal const string AtRestUnknown =
        "NOT KNOWN. The node state could not be read, so this PC must be treated as left in the state Earshot " +
        "exists to prevent, until Restore has run.";

    internal const string AtRestNotApplicable =
        "At rest does not apply here: Earshot is not set up, or Block at boot is off.";

    internal const string AtRestNoSuchFinding =
        "Not recorded. This run is older than the at-rest check. Treat it as not known.";

    // no-on-purpose carries its own reason, so it is built rather than fixed.
    internal static string AtRestOnPurpose(string reason) =>
        "Left enabled on purpose. " + reason + " Shut down as the test asks, or run Restore to put it back.";

    // test-gui.md section 9.1's hand-off screen. HandOffShutDown is the spec's own literal text.
    // Section 9.1 does not print the restart or any-start wording in full ("Tests 04, 15 and 05
    // say restart; each variant of 10 shows the script's own instruction"), so HandOffRestart and
    // HandOffAnyStart draw the same shape from the same section rather than quoting text that was
    // never given.
    internal const string HandOffShutDown =
        "Now shut this PC down. Use Start, Power, Shut down. Do not choose Restart: a restart " +
        "does not count for this test and this window will not accept one. Keep listening on your " +
        "phone. Leave the PC off for ten seconds, start it, sign in, and open this window again. " +
        "It will pick up here.";

    internal const string HandOffRestart =
        "Now restart this PC. Use Start, Power, Restart. Keep listening on your phone. When it has " +
        "signed you back in, open this window again. It will pick up here.";

    internal const string HandOffAnyStart =
        "Now shut this PC down or restart it, either is fine for this test. Keep listening on your " +
        "phone. When it has signed you back in, open this window again. It will pick up here.";

    internal static string HandOffText(PowerCycleRequirement requirement) => requirement switch
    {
        PowerCycleRequirement.FullShutDown => HandOffShutDown,
        PowerCycleRequirement.Restart => HandOffRestart,
        _ => HandOffAnyStart,
    };

    internal const string FirstHalfFailedHandOff =
        "The first half did not pass, so shutting down now would not be a valid run. Read the " +
        "result below, then fix what it names before running this test again.";

    // section 6.2: "a first-half result.json, whatever it says, is never the test's pass." Test
    // 10's own first half never records a criterion (only a finding), so it always recomputes to
    // "inconclusive" by design; that is not a failure. Only overall "fail" (which a stopped-early
    // "run" criterion also forces, since a failing criterion always recomputes the whole result to
    // fail) means the hand-off screen should say shutting down now would not be a valid run.
    internal static bool FirstHalfGenuinelyFailed(string overall) => overall == "fail";

    // 08's own words for its fastStartupAtPowerDown finding (section 9.1): "On this PC
    // HiberbootEnabled read 0 on 2026-09-20, so today it would say: 'Fast Startup is off on this
    // PC, so this run tests a cold start.'"
    internal static string FastStartupSentence(string value) =>
        "Fast Startup is " + value + " on this PC, so this run tests a " +
        (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ? "cold start" : "Fast Startup shut down") +
        ". The window never changes that setting.";

    // test-gui.md section 11. T13 pins that none of these ever reads as running by itself: no
    // "unattended", "automatic" or "batch", and no "GUI" (this is a window, never named that to
    // the owner).
    internal const string RunAllButtonLabel = "Run all, step by step";

    internal const string RunAllExplanation =
        "You will be asked at every step. It stops for every shut down and carries on when you come back.";

    internal const string RunAllCarryOnButtonLabel = "I have read this. Carry on with Run all.";

    internal static string RunAllStoppedForPowerCycle(string testNumber) =>
        "Run all stopped for the shut down or restart in test " + testNumber + ". Carry on with the second half?";

    internal static string RunAllStoppedForFailure(string testNumber) =>
        "Run all stopped at test " + testNumber + ". Read the result, then choose to carry on.";

    internal const string RunAllLockedItemSkipped =
        "This item is locked, so Run all stopped here rather than skip past it.";

    internal const string RunAllFinished = "Run all has reached the end of the list.";

    // Never conflates an unread state with a good one (T11): "unknown" and a missing finding
    // both route through AtRestUnknown/AtRestNoSuchFinding, never AtRestYes.
    internal static string LeftAtRestText(string? leftAtRest, string? reason) => leftAtRest switch
    {
        "yes" => AtRestYes,
        "no" => AtRestNo,
        "no-on-purpose" => AtRestOnPurpose(string.IsNullOrEmpty(reason) ? "No reason was recorded." : reason),
        "not-applicable" => AtRestNotApplicable,
        "unknown" => AtRestUnknown,
        null => AtRestNoSuchFinding,
        _ => AtRestUnknown,
    };
}
