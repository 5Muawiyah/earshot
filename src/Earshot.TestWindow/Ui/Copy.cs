using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Every string the owner sees lives here. A test pins one part of that: the literal word "Passed"
// appears in this file and nowhere else, so a row cannot start claiming success by any other path
// than RowStateKind.Passed.
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

    // Shown for a Locked row's detail.
    internal const string LockedDetail =
        "Locked. Run the administrator prompt check first. It proves this window can raise the " +
        "Windows permission box and read the answer before anything real depends on it.";

    // The rehearsal control's own name and its permanently visible warning, beside its button,
    // never only in a tooltip. Two facts it must say plainly, in this window's own words: Windows
    // will raise a real prompt, and this is the elevated launch site's first execution in any form.
    internal const string RehearsalRowName = "Administrator prompt check";

    internal const string RehearsalWarning =
        "This starts a real check. Windows will raise a real administrator permission prompt, " +
        "twice. This is the first time this launch has ever been run, in any form. Only the " +
        "owner runs this, by hand, from this window; nothing else ever calls it.";

    internal const string RehearsalNeverInSandbox =
        "Not available in a sandbox window: this always runs for real and always raises a real " +
        "Windows prompt, so a development sandbox never runs it.";

    internal const string RehearsalUnlocksRows = "Passing this unlocks row 15, and offers 00's uninstall and 07's plan B.";

    // 00's uninstall variant and 07's plan B are real branches those scripts declare
    // (-OfferUninstall, -AllowPlanB) that this row can never reach, because this window never
    // passes either as true anywhere. No silently missing path: said plainly on the row itself.
    internal const string RestoreUninstallOfferNotAvailable =
        "This row never offers to uninstall Earshot: -OfferUninstall is not wired to a control " +
        "in this build. To run it, open a console and run: powershell -File " +
        "tools\\live-tests\\Run-LiveTests.ps1 -Test 00 -ExePath <path to Earshot.exe> -OfferUninstall";

    internal const string PlanBNotAvailable =
        "This row never tries the elevated plan B for the missing third value: -AllowPlanB is " +
        "not wired to a control in this build. To run it, open a console and run: powershell " +
        "-File tools\\live-tests\\Run-LiveTests.ps1 -Test 07 -ExePath <path to Earshot.exe> -AllowPlanB";

    // Test 14's own "Moving the pin off a protected device" half needs a second Bluetooth audio
    // device's address, and this window never offers a text box to type one into. It is chosen
    // from buttons built out of SpeakerCandidateFinder's own read of an earlier run's node
    // evidence; this is shown on the row instead when that read comes back empty, so the owner
    // knows what to do rather than finding a button row with nothing on it.
    internal const string SpeakerAddressNoCandidates =
        "No second Bluetooth audio device has been read yet, so this half cannot be tried: nothing " +
        "in an earlier run's own record of the Bluetooth nodes showed one. Pair a second device " +
        "with an A2DP sink (a speaker, for example), run 01 or 02 once with it nearby, then come " +
        "back to this row to choose it.";

    internal const string SpeakerAddressChoicePrompt =
        "Second device for the protected-device half, read from an earlier run's own node list:";

    internal const string SpeakerAddressChoiceNone = "No second device";

    // The sandbox's fake machine (tools\live-tests\selftest\Fakes.psm1) has a starting state for
    // "10-shutdown-messages-v1" only: variants 2 to 5 have none, so a sandboxed run of one of them
    // fails immediately rather than settling anything about the row. Said here rather than left
    // for the owner to find out by starting one.
    internal const string Test10VariantNotSandboxTestable =
        "Not testable in a sandbox window: the sandbox's fake machine has a starting state for " +
        "variant 1 only. Choose variant 1 to try the sandbox, or run this one for real.";

    // Derived from result.json only: a step with elevated true, ran false and an error. Two
    // independent messages, never guessed at from anything the window itself observed while the
    // step ran.
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
    // states are always told apart from a clean one.
    internal static string RowText(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string baseText = BaseText(state.Kind);
        return state.Qualifier is null ? baseText : baseText + ", " + state.Qualifier;
    }

    // The leftAtRest copy, shown after every run. AtRestUnknown is written to say plainly, in its
    // own words, that the machine must be treated as left in the state Earshot exists to prevent:
    // an unread node state is never shown as though it were a yes.
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

    // The hand-off screen. Tests 04, 15 and 05 say restart; each variant of 10 shows the script's
    // own instruction, so HandOffRestart and HandOffAnyStart draw the same shape as
    // HandOffShutDown rather than quoting text that was never given.
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

    // A first-half result.json, whatever it says, is never the test's pass. Test 10's own first
    // half never records a criterion (only a finding), so it always recomputes to "inconclusive"
    // by design; that is not a failure. Only overall "fail" (which a stopped-early "run"
    // criterion also forces, since a failing criterion always recomputes the whole result to
    // fail) means the hand-off screen should say shutting down now would not be a valid run.
    internal static bool FirstHalfGenuinelyFailed(string overall) => overall == "fail";

    // 08's own words for its fastStartupAtPowerDown finding: HiberbootEnabled read 0 on this PC
    // on 2026-09-20, so today it would say "Fast Startup is off on this PC, so this run tests a
    // cold start."
    internal static string FastStartupSentence(string value) =>
        "Fast Startup is " + value + " on this PC, so this run tests " +
        (value switch
        {
            "off" => "a cold start",
            "on" => "a Fast Startup shut down",
            _ => "a start whose kind could not be confirmed",
        }) +
        ". The window never changes that setting.";

    // A test pins that none of these ever reads as running by itself: no "unattended",
    // "automatic" or "batch", and no "GUI" (this is a window, never named that to the owner).
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

    // Never conflates an unread state with a good one: "unknown" and a missing finding both
    // route through AtRestUnknown/AtRestNoSuchFinding, never AtRestYes.
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
