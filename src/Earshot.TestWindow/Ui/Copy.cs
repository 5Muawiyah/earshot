using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Every string the owner sees lives here. A test pins one part of that: the literal word "Passed"
// appears in this file and nowhere else, so a row cannot start claiming success by any other path
// than RowStateKind.Passed.
internal static class Copy
{
    // Off by default (TechnicalDetailsSettings): with it off, no script-authored string is shown
    // anywhere in the window; with it on, everything the window used to show unconditionally is
    // still shown, labelled "Technical details" wherever it appears.
    internal const string ShowTechnicalDetailsLabel = "Show technical details";

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

    // Plain row states (shown with technical details off): the same nine kinds, in the reader's
    // own words. RowPresenter.PlainText never makes Unknown, a failure or a qualified pass look
    // better than it is; PlainRowStateDistinctnessTests pins that every one of these, and every
    // plain qualifier below, is its own distinct text, never colliding with the clean "Worked".
    internal const string PlainNotRun = "Not run yet";
    internal const string PlainWaitingForShutDown = "Waiting for you to shut down and turn this computer back on";
    internal const string PlainWaitingForRestart = "Waiting for you to restart this computer";
    internal const string PlainPassed = "Worked";
    internal const string PlainFailed = "Did not work";
    internal const string PlainInconclusive = "Could not tell";
    internal const string PlainStoppedBeforeAnyStep = "Stopped before it started";
    internal const string PlainLocked = "Locked";

    // Never "Worked": Unknown is a fail-closed state, never a pass, and must never read as one.
    internal const string PlainUnknown = "We do not know";

    internal static string PlainBaseText(RowStateKind kind) => kind switch
    {
        RowStateKind.NotRun => PlainNotRun,
        RowStateKind.WaitingForShutDown => PlainWaitingForShutDown,
        RowStateKind.WaitingForRestart => PlainWaitingForRestart,
        RowStateKind.Passed => PlainPassed,
        RowStateKind.Failed => PlainFailed,
        RowStateKind.Inconclusive => PlainInconclusive,
        RowStateKind.StoppedBeforeAnyStep => PlainStoppedBeforeAnyStep,
        RowStateKind.Locked => PlainLocked,
        _ => PlainUnknown,
    };

    // Every qualifier StateDeriver ever writes (StateDeriverTests pins the technical set), in
    // plain words. An unrecognised qualifier falls back to its own words rather than vanishing:
    // a caveat the owner cannot read at all is worse than one shown in the script's own words.
    internal static string PlainQualifier(string qualifier) => qualifier switch
    {
        "on an earlier build" => "with an older copy of Earshot",
        "second half only on record" => "with only the second half on record",
        "shut down not confirmed" => "the shut down could not be confirmed",
        "run order not confirmed" => "we could not confirm which run was newest",
        _ => qualifier,
    };

    // The plain equivalent of RowText: the same "base, qualifier" shape, never shortened back to
    // the bare "Worked" for a qualified pass, so the amber states stay told apart from a clean one
    // here too.
    internal static string PlainRowText(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string baseText = PlainBaseText(state.Kind);
        return state.Qualifier is null ? baseText : baseText + ", " + PlainQualifier(state.Qualifier);
    }

    // Shown for a Locked row's detail.
    internal const string LockedDetail =
        "Locked. Run the permission box check first. It proves this window can raise the " +
        "Windows box that asks for permission, and read the answer, before anything real depends on it.";

    // The rehearsal control's own name and its permanently visible warning, beside its button,
    // never only in a tooltip. Two facts it must say plainly, in this window's own words: Windows
    // will raise a real prompt, and this is the elevated launch site's first execution in any form.
    internal const string RehearsalRowName = "Permission box check";

    internal const string RehearsalWarning =
        "This starts a real check. Windows will show a real box asking for permission, twice. " +
        "This is the first time this has ever been run, in any form. Only the owner runs this, " +
        "by hand, from this window; nothing else ever calls it.";

    internal const string RehearsalNeverInSandbox =
        "Not available in this practice window: this always runs for real and always raises a real " +
        "Windows permission box, so a practice window with no real device never runs it.";

    internal const string RehearsalUnlocksRows =
        "Passing this unlocks row 15. Rows 00 and 07 still never offer their own extra step in this " +
        "build; passing this only means the other way (ask whoever set Earshot up to run the command each row names) is trusted.";

    // The three places a row is marked Unknown by force (a missed exit, a forced stop, a failed
    // start) used to say it stayed Unknown "until Restore has run": false, since a fresh run of
    // the row's own test, once it reads a clean result, becomes the newer evidence and clears it
    // exactly the same way. Restore is one way to clear the banner (and so unlock every other
    // row), never the only way this one row's own Unknown clears.
    internal const string RowUnknownUntilItRunsAgain = "This row now reads Unknown until it runs again (or Restore runs).";

    // 00's uninstall variant and 07's plan B are real branches those scripts declare
    // (-OfferUninstall, -AllowPlanB) that this row can never reach, because this window never
    // passes either as true anywhere. No silently missing path: said plainly on the row itself,
    // in the reader's own words (plain-words.md's own "console route" line: say who to ask
    // rather than name the command, which only whoever set Earshot up would run anyway).
    internal const string RestoreUninstallOfferNotAvailable =
        "This row never offers to remove Earshot from here. That has to be done another way: " +
        "ask whoever set Earshot up.";

    internal const string PlanBNotAvailable =
        "This row never tries the other way that needs an extra Windows permission box, for the " +
        "missing third value. That has to be done another way: ask whoever set Earshot up.";

    // Test 14's own "Moving the pin off a protected device" half needs a second Bluetooth audio
    // device's address, and this window never offers a text box to type one into. It is chosen
    // from buttons built out of SpeakerCandidateFinder's own read of an earlier run's node
    // evidence; this is shown on the row instead when that read comes back empty, so the owner
    // knows what to do rather than finding a button row with nothing on it.
    internal const string SpeakerAddressNoCandidates =
        "No second Bluetooth audio device has been read yet, so this half cannot be tried: nothing " +
        "in an earlier run's own record of the Bluetooth connections showed one. Pair a second device " +
        "that can play sound (a speaker, for example), run 01 or 02 once with it nearby, then come " +
        "back to this row to choose it.";

    internal const string SpeakerAddressChoicePrompt =
        "Second device for this test, read from an earlier run's own list:";

    internal const string SpeakerAddressChoiceNone = "No second device";

    // The sandbox's fake machine (tools\live-tests\selftest\Fakes.psm1) has a starting state for
    // "10-shutdown-messages-v1" only: variants 2 to 5 have none, so a sandboxed run of one of them
    // fails immediately rather than settling anything about the row. Said here rather than left
    // for the owner to find out by starting one.
    internal const string Test10VariantNotSandboxTestable =
        "Not testable in this practice window: its fake machine has a starting state for the " +
        "first way of restarting only. Choose that one to try the practice window, or run this one for real.";

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
        "This computer is leaving your AirPods alone. Your AirPods' Bluetooth connection is blocked, " +
        "so this computer will not grab them off your phone the next time it starts.";

    // Corrected from live evidence: telling the owner to run Restore first was wrong for the
    // commonest cause. Restore turns the AirPods connection on, Windows then plays sound through
    // them, and the closing block is refused while that sound is live, so Restore ends red again
    // every time. What actually clears it is getting the AirPods off this computer first (a click
    // on the Earshot icon, or the AirPods back in their case) so nothing is using them when the
    // block runs; Restore is the fallback, tried only once that has not worked.
    internal const string AtRestNo =
        "This computer may grab your AirPods off your phone the next time it starts. " +
        "Before you shut down: check Earshot is running (its icon is near the clock), then click " +
        "the icon once so your AirPods go back to your phone. If the warning is still here after " +
        "that, run Restore, and say Yes to its last question while your AirPods are in their case.";

    internal const string AtRestUnknown =
        "We do not know. It could not be read whether your AirPods' Bluetooth connection is blocked, so this " +
        "computer must be treated as able to grab them off your phone, until Restore has run.";

    internal const string AtRestNotApplicable =
        "This does not apply here: Earshot is not set up, or Block at boot is off.";

    internal const string AtRestNoSuchFinding =
        "Not recorded. This run is older than this check. Treat it as not known.";

    // no-on-purpose carries its own reason, so it is built rather than fixed.
    internal static string AtRestOnPurpose(string reason) =>
        "Left able to grab them on purpose. " + reason + " Shut down as the test asks, or run Restore to put it back.";

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
    // "automatic" or "batch", and no "GUI" (this is a window, never named that to the owner). This
    // is the window's main control: one large button at the top of the list, the row list itself
    // staying secondary, for running a single test.
    internal const string RunAllButtonLabel = "Run all the tests";

    // Shown instead, once run-all.json records a stopped point to come back to: the same primary
    // button, relabelled, never a second control.
    internal const string RunAllCarryOnLabel = "Carry on with the tests";

    internal const string RunAllExplanation =
        "You will be asked at every step. It stops for every shut down and carries on when you come back.";

    internal const string RunAllCarryOnButtonLabel = "Carry on with the rest";

    internal const string RunAllStopHereButtonLabel = "Stop here";

    internal static string RunAllStoppedForPowerCycle(string testNumber) =>
        "Run all stopped for the shut down or restart in test " + testNumber + ". Carry on with the second half?";

    // The result panel's plain-mode summary, first: how many checks worked, how many did not, and
    // how many could not be told, each mentioned only when its own count is above zero. "All N
    // checks worked." is the one case with nothing else to add.
    internal static string PlainCheckSummarySentence(int total, int failedCount, int inconclusiveCount)
    {
        if (total == 0)
        {
            return "No checks were recorded.";
        }

        if (failedCount == 0 && inconclusiveCount == 0)
        {
            return "All " + total + " check" + (total == 1 ? "" : "s") + " worked.";
        }

        var parts = new List<string>();
        if (failedCount > 0)
        {
            parts.Add(failedCount + " of " + total + " check" + (total == 1 ? "" : "s") + " did not work.");
        }

        if (inconclusiveCount > 0)
        {
            parts.Add("We could not tell for " + inconclusiveCount + " check" + (inconclusiveCount == 1 ? "" : "s") + ".");
        }

        return string.Join(" ", parts);
    }

    internal static string RunAllStoppedForFailure(string testNumber) =>
        "Run all stopped at test " + testNumber + ". Read the result, then choose to carry on.";

    internal const string RunAllLockedItemSkipped =
        "This item is locked, so Run all stopped here rather than skip past it.";

    internal const string RunAllFinished = "Run all has reached the end of the list.";

    // Run all under a red or unknown banner never simply refuses: every other row stays locked
    // exactly as before, but row 00 Restore (the one start the gate already allows under red) is
    // offered first, and Run all only carries on with the rest once Restore itself records this
    // computer at rest. AtRestNo is shown first, as the deliberate step to act on before Restore
    // ever starts, since it is usually all that is needed and Restore alone often is not enough
    // (see AtRestNo's own note).
    internal const string RunAllRestoreAdviceHeading = "This computer is not known to be safe to shut down yet";

    internal const string RunAllRestoreContinueButtonLabel = "I have done that, run Restore now";

    internal const string RunAllRestoreRunning = "Running Restore first, to get this computer back to a known state.";

    internal const string RunAllRestoreSucceeded = "Restore finished and this computer is safe to shut down. Carrying on with the rest of the tests.";

    internal const string RunAllRestoreDidNotReachAtRest =
        "Restore finished, but this computer is still not known to be safe to shut down. Run all has stopped: " +
        "read the result below, try the steps above again, then start Run all again.";

    internal static string RunAllProgressLine(int oneBasedIndex, int total, string name) =>
        "Test " + oneBasedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " +
        total.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + name;

    // The end-of-run summary: how many worked, did not work, could not tell, and were not run
    // (each only mentioned when its own count is above zero), followed by the plain reason for
    // each row that was never run.
    internal static string RunAllSummarySentence(int worked, int didNotWork, int couldNotTell, int notRunYet)
    {
        var parts = new List<string>();
        if (worked > 0)
        {
            parts.Add(worked + " " + (worked == 1 ? "test" : "tests") + " worked");
        }

        if (didNotWork > 0)
        {
            parts.Add(didNotWork + " did not work");
        }

        if (couldNotTell > 0)
        {
            parts.Add("we could not tell for " + couldNotTell);
        }

        if (notRunYet > 0)
        {
            parts.Add(notRunYet + " " + (notRunYet == 1 ? "was" : "were") + " not run yet");
        }

        return parts.Count == 0 ? "Nothing has been run yet." : string.Join(", ", parts) + ".";
    }

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
