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
    // in the reader's own words (the word list's "console route" line: say who to ask
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

    // A single text for every red cause, never advising a click: LiveTest.psm1's own at-rest
    // check writes leftAtRest "no" whenever the nodes read anything but Blocked, which includes a
    // declined offer with the AirPods still on the phone; it never reads the actual Bluetooth
    // connection. There is no cause a "no" can be trusted to mean "the AirPods are known playing
    // from this computer", so a click can never safely be advised for any red cause: a left click
    // connects the AirPods here, exactly wrong whenever they are already on the phone. Never
    // promises a click alone clears the warning either: nothing here ever re-reads the device
    // after one, so it never could.
    internal const string AtRestNo =
        "Put your AirPods in their case. Then run Restore (row 00) and say Yes to its last " +
        "question. The warning clears when a test ends with this computer leaving your AirPods alone.";

    // The one cause this window ever names specifically (Banner.IsDuplicateRecordCause): two
    // saved records sharing the exact same number is a fact about the files on disk, not a guess
    // about the device, so it is safe to say plainly rather than folding it into AtRestNo's own
    // silence about cause. The same instruction and the same closing sentence either way: still
    // never a click, still never a promise that a click alone clears it.
    internal const string AtRestDuplicateRecord =
        "Two saved records for a test share the same number, which should never happen: one of " +
        "them has been copied or duplicated. Put your AirPods in their case. Then run Restore " +
        "(row 00) and say Yes to its last question. The warning clears when a test ends with this " +
        "computer leaving your AirPods alone.";

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

    // The two close-window confirmations (OnFormClosing): shown as a real Windows message box,
    // unconditionally, never behind the technical-details toggle, so they carry this window's own
    // plain words directly, this computer, never "this PC" or "at rest" itself.
    internal const string CloseWhileRunningConfirmation =
        "A test is still running. Closing now stops it, the same as Stop the test: no result is " +
        "saved, and this computer may grab your AirPods off your phone the next time it starts. Close anyway?";

    internal const string CloseNotAtRestConfirmation =
        "This computer may grab your AirPods off your phone the next time it starts. Close anyway?";

    // The at-rest banner shown at the top of Home. Banner.Compute only ever returns Red or Amber
    // here (None never reaches this: MainForm hides the banner outright for it), and it never says
    // which exact leftAtRest value caused Red: that collapsing is deliberate, since there is no
    // cause a "no" can be trusted to mean the AirPods are known connected to this computer (see
    // AtRestNo's own note), so every Red cause reads the same one text, except a duplicate record
    // (Banner.IsDuplicateRecordCause), a fact about the files on disk this window can say plainly.
    // Amber (left enabled on purpose, for a test still in progress) gets its own short plain line,
    // since "Left enabled on purpose." (Banner.cs's own internal caption) says nothing plain about
    // what "enabled" means or what to do about it.
    internal const string BannerAmberPlain =
        "This computer's AirPods connection was left able to grab them, on purpose, so a test still in " +
        "progress could finish. No action is needed yet; the test itself will say when to shut down.";

    internal static string BannerPlainText(BannerLevel level, bool isDuplicateRecordCause = false) => level switch
    {
        BannerLevel.Red => isDuplicateRecordCause ? AtRestDuplicateRecord : AtRestNo,
        BannerLevel.Amber => BannerAmberPlain,
        _ => string.Empty,
    };

    // The hand-off screen. Tests 04, 15 and 05 say restart; each variant of 10 shows the script's
    // own instruction, so HandOffRestart and HandOffAnyStart draw the same shape as
    // HandOffShutDown rather than quoting text that was never given.
    internal const string HandOffShutDown =
        "Now shut this computer down. Use Start, Power, Shut down. Do not choose Restart: a restart " +
        "does not count for this test and this window will not accept one. Keep listening on your " +
        "phone. Leave this computer off for ten seconds, start it, sign in, and open this window again. " +
        "It will pick up here.";

    internal const string HandOffRestart =
        "Now restart this computer. Use Start, Power, Restart. Keep listening on your phone. When it has " +
        "signed you back in, open this window again. It will pick up here.";

    internal const string HandOffAnyStart =
        "Now shut this computer down or restart it, either is fine for this test. Keep listening on your " +
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

    // 08's own words for its fastStartupAtPowerDown finding, in the owner's own rewrite
    // (the word list's "explain the one hard word in the same sentence" rule): the reader learns
    // what Fast Startup is in the same breath it is named, and this computer is never called "this
    // PC". HiberbootEnabled read 0 on this computer on 2026-09-20, so today it would read the
    // "off" branch below.
    internal static string FastStartupSentence(string value) => value switch
    {
        "off" =>
            "A setting called Fast Startup is switched off on this computer, which is fine. " +
            "This run starts this computer completely off, not half asleep. The window never changes that setting.",
        "on" =>
            "A setting called Fast Startup is switched on, on this computer, which is fine too. " +
            "This run starts this computer from Fast Startup's own quicker way of shutting down, not completely off. " +
            "The window never changes that setting.",
        _ =>
            "It could not be read whether a setting called Fast Startup is switched on or off on this computer, " +
            "so the kind of start this run tested could not be confirmed. The window never changes that setting.",
    };

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

    // A failed or inconclusive check used to be listed as a plain, affirmative-reading sentence
    // with nothing marking it as bad, so the sense of the whole list depended on colour alone.
    // Every check line under a failed or inconclusive result is now led by a marker and this
    // prefix, so the sense holds even in black and white.
    internal static string PlainCheckLinePrefix(string outcome) =>
        outcome == "fail" ? "✗ Did not work: " : "? Could not tell: ";

    internal static string RunAllStoppedForFailure(string testNumber) =>
        "Run all stopped at test " + testNumber + ". Read the result, then choose to carry on.";

    internal const string RunAllLockedItemSkipped =
        "This item is locked, so Run all stopped here rather than skip past it.";

    internal const string RunAllFinished = "Run all has reached the end of the list.";

    // Run all under a red or unknown banner never simply refuses: every other row stays locked
    // exactly as before, but row 00 Restore (the one start the gate already allows under red) is
    // offered first, and Run all only carries on with the rest once Restore itself records this
    // computer at rest. AtRestNo is shown first, as the one step to act on: put the AirPods in
    // their case, then run Restore, the same advice for every cause.
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

    // The Step view's own progress line, shown above every
    // prompt for both an ordinary single-test start and Run all: "Test N of M", a middle dot, then
    // the row's own name, matching the spec's own literal example ("Test 3 of 20 · Connect with
    // one click"). Kept apart from RunAllProgressLine's own colon-separated shape above, rather
    // than reshaping that one to match: RunAllPresentationTests already pins its text verbatim, and
    // this is a different control (StepPanel's own progress label, not _runAllProgressLabel, which
    // still only shows while Run all itself is under way) so the two lines were never one sentence
    // duplicated in two places to begin with.
    internal static string StepProgressLine(int oneBasedIndex, int total, string name) =>
        "Test " + oneBasedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " +
        total.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + name;

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

    // The status line, in plain words. Every one of these has a technical twin MainForm still
    // shows with technical details on (a TestId, a script name, an exit code or a raw path);
    // these never carry any of the four, following the same shape the owner asked for: "Running:
    // Connect with one click", "Finished: ...", "Waiting for you to shut the computer down".
    internal static string PlainRunningStatus(string testName, bool isResume) =>
        "Running: " + testName + (isResume ? ", the second half" : string.Empty);

    // "Finished" never says more than RowPresenter/PlainBaseText already say for the same row,
    // so a qualified pass, a failure or Unknown can never read better here than it does in the
    // list: verdictText is always exactly Copy.PlainRowText's own output for the row that just
    // finished.
    internal static string PlainFinishedStatus(string testName, string verdictText) =>
        "Finished: " + testName + ", " + verdictText + ".";

    internal static string PlainRunCrashedStatus(string testName) =>
        testName + ": the test stopped unexpectedly. Show technical details to see why.";

    internal const string PlainUnreadableMessageStatus =
        "The test sent something this window could not read. Show technical details to see what.";

    internal static string PlainNoReadableResultStatus(string testName) =>
        testName + ": nothing readable was saved for this run. Show technical details to see why.";

    internal static string PlainWaitingForPowerCycleStatus(PowerCycleRequirement requirement, string testName) => requirement switch
    {
        PowerCycleRequirement.FullShutDown => testName + ": waiting for you to shut the computer down.",
        PowerCycleRequirement.Restart => testName + ": waiting for you to restart the computer.",
        _ => testName + ": waiting for you to shut the computer down or restart it.",
    };

    internal const string PlainExeChoiceNotUsedStatus = "That could not be used. Show technical details to see why.";

    internal const string PlainExeChosenStatus = "Earshot has been found on this computer.";

    // Windows PowerShell 5.1's own fixed install path is technical; the word list's
    // "console route" line ("say who to ask") covers exactly this: nothing here can be started
    // without it, and only whoever set Earshot up can fix that.
    internal const string PlainPowerShellMissingStatus =
        "A program this window needs could not be found on this computer, so nothing could be started. " +
        "Ask whoever set Earshot up for help.";

    internal const string PlainCouldNotContinueStatus =
        "This test could not carry on from where it stopped. Ask whoever set Earshot up for help, or start it again from the beginning.";

    internal const string NotedStartNeedsDeliberateClick = "This start needs a deliberate click before it counts as tried.";

    // The button beside NotedStartNeedsDeliberateClick's own warning: was hardcoded inline in
    // MainForm rather than routed through here, so the banned-words sweep never scanned it.
    internal const string NotedStartButtonLabel = "Start anyway";

    // Shown alongside a row's own plain detail (never gated by technical details, the same as
    // Copy.RestoreUninstallOfferNotAvailable): "variant" is jargon, so this names the fact plainly
    // instead of the script's own word for it.
    internal const string WaitsOnWindowsUpdatePlain =
        "This way of restarting waits for Windows Update to offer a restart. It may take a while to appear.";

    // Was hardcoded with the row's own TestId (technical) and shown unconditionally, even with
    // technical details off; testName is the row's own plain Name instead, the same pattern
    // PlainRunCrashedStatus already uses for a start that fails outright.
    internal static string PlainCouldNotStartStatus(string testName) =>
        testName + ": this test could not be started. Show technical details to see why.";

    internal const string StopSentWaitingStatus = "Stop sent. Waiting up to 60 s for the test to finish on its own.";

    internal static string SilentForMinutesStatus(int minutes) =>
        "This test has been silent for " + minutes + " minutes. Keep waiting, or Stop the test.";

    // The stop confirmation box (a real Windows MessageBox, shown whether technical details is on
    // or off, since it is a safety warning, never gated): this computer, never "this PC", and
    // never "at rest" itself.
    internal const string StopConfirmationWarning =
        "Stopping it now means no result is written, and this computer may still be able to grab your AirPods off your phone.";

    // Home view (the second pass): the window's own front page. Title, three
    // plain sentences, then the one large primary button (Copy.RunAllButtonLabel/RunAllCarryOnLabel,
    // unchanged, just re-homed here).
    internal const string HomeTitle = "Earshot tests";

    internal const string HomeIntroWhatEarshotDoes =
        "Earshot stops this computer grabbing your AirPods off your phone when it starts up, and lets " +
        "you connect or disconnect them with one click on its icon near the clock.";

    internal const string HomeIntroWhatTheseTestsAreFor =
        "These tests check that this computer is doing that properly.";

    internal const string HomeIntroHowThisWindowHelps =
        "This window will say exactly what to do at each step, and Not sure is always an acceptable answer.";

    // Under the primary button: how many tests Run all covers, and how many of those need a full
    // shut down and start again partway through. Both counts are read from the real manifest
    // (RunAllOrder.Items, the same list RunAllProgressLine already counts against), never a typed
    // number, so this line can never drift from what Run all actually does.
    internal static string HomeRunAllCountLine(int totalTests, int fullShutDownCount)
    {
        string testsWord = totalTests == 1 ? "test" : "tests";
        string shutDownSentence = fullShutDownCount switch
        {
            0 => "None of them need this computer shut down partway through.",
            1 => "One of them needs this computer shut down and started again partway through; the window will say so when it is time.",
            _ => fullShutDownCount + " of them need this computer shut down and started again partway through; the window will say so when it is time.",
        };

        return totalTests.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + testsWord +
            " in all. Some need your AirPods and your phone nearby. " + shutDownSentence;
    }

    // Shown only while run-all.json records a stopped point to come back to: which test Carry on
    // with the tests will pick up at, read from the same record UpdateRunAllButtonLabel already
    // reads for the button's own label.
    internal static string HomeCarryOnPickupLine(string testName) => "It will pick up at: " + testName + ".";

    internal const string ChooseOneTestLinkLabel = "Choose one test";

    internal const string MoreSectionShowLabel = "More";

    internal const string MoreSectionHideLabel = "Less";

    // The exe chooser's own new plain label (was "Choose Earshot.exe...").
    internal const string FindEarshotButtonLabel = "Find Earshot on this computer";

    internal const string PracticeDataLabel = "Practice data:";

    // The collapsed More section's own two-sentence teaser for the permission box check; the fuller
    // Copy.RehearsalWarning is shown only once the section is open.
    internal const string RehearsalTeaser =
        "This runs a one-time safety check of the Windows permission box. Only the owner should run it, by hand, once.";

    // List view ("Choose one test").
    internal const string ListBackButtonLabel = "Back";

    // The list's own start control, in the two shapes it can read: a fresh row, or one with a
    // pending second half waiting to be carried on.
    internal const string StartThisTestButtonLabel = "Start this test";

    internal const string CarryOnSecondHalfButtonLabel = "Carry on with the second half";

    // The row states shown as text are already plain (Copy.PlainBaseText); this is the small
    // symbol shown beside each one, so colour is never the only signal. A qualified pass (an
    // unconfirmed shut down, an earlier build, and the rest) never gets the tick: RowText/
    // PlainRowText already refuse to shorten it back to the bare word, and the symbol beside it
    // must not carry the pass mark either, or amber would read as green at a glance.
    internal const string QualifiedPassSymbol = "○";

    internal static string RowStateSymbol(RowStateKind kind, string? qualifier = null) => kind switch
    {
        RowStateKind.Passed => qualifier is null ? "✓" : QualifiedPassSymbol,
        RowStateKind.Failed => "✗",
        RowStateKind.Locked => "■",
        RowStateKind.WaitingForShutDown => "»",
        RowStateKind.WaitingForRestart => "»",
        RowStateKind.Inconclusive => "?",
        RowStateKind.Unknown => "?",
        _ => "•",
    };

    // The Result view's own single verdict line (the layout's Result section): the
    // four sentences quoted there, verbatim, chosen from the same RowStateKind MainForm already
    // derived for this row (ComputeState/StateDeriver), never re-derived here. Any kind other than
    // the three named good-faith outcomes (Passed, Failed, Inconclusive) reads as the fourth,
    // deliberately harshest sentence: a result the reader must never mistake for a pass, matching
    // PlainBaseText's own fail-closed default (=> PlainUnknown) for the same reason.
    internal const string ResultVerdictPassed = "This test worked.";
    internal const string ResultVerdictFailed = "This test did not work.";
    internal const string ResultVerdictInconclusive = "We could not tell.";
    internal const string ResultVerdictUnknown = "We do not know what happened, and that is not a pass.";

    // A pass carrying a qualifier (an earlier build, a second half only on record, an unconfirmed
    // shut down, and the rest) is never allowed to read as this clean a sentence: RowText/
    // PlainRowText already refuse to shorten a qualified pass back to the bare word on the row
    // itself, and the Result view's own single verdict line must say the same thing, not just the
    // row underneath it.
    internal const string ResultVerdictQualifiedPassed = "This test worked, but it does not count yet.";

    internal static string ResultVerdictSentence(RowStateKind kind, string? qualifier = null) => kind switch
    {
        RowStateKind.Passed => qualifier is null ? ResultVerdictPassed : ResultVerdictQualifiedPassed,
        RowStateKind.Failed => ResultVerdictFailed,
        RowStateKind.Inconclusive => ResultVerdictInconclusive,
        _ => ResultVerdictUnknown,
    };

    // The verdict sentence with its own symbol (Copy.RowStateSymbol, the same one the row list
    // already shows beside its state text, qualifier included), so the Result view's headline
    // never relies on the sentence's own words alone either, and a qualified pass's amber
    // sentence is never shown beside the plain pass tick.
    internal static string ResultVerdictLine(RowStateKind kind, string? qualifier = null) =>
        RowStateSymbol(kind, qualifier) + " " + ResultVerdictSentence(kind, qualifier);

    // The qualifier's own plain reason, shown as the Result view's second line under a qualified
    // pass, one sentence per qualifier named in the fix (the reviewer's own two examples, verbatim)
    // plus every other qualifier StateDeriver ever writes (PlainQualifier's own switch, tested by
    // ResultVerdictQualifierLineCoversEveryPlainQualifierTests so a new qualifier can never go
    // silently unmapped here while still being named on the row).
    internal static string ResultVerdictQualifierReason(string qualifier) => qualifier switch
    {
        "shut down not confirmed" => "the computer was not shut down completely, so this run cannot settle the question",
        "on an earlier build" => "this was an older copy of Earshot",
        "second half only on record" => "only the second half of this test is on record",
        "run order not confirmed" => "we could not confirm which run of this test was the newest",
        "restart half not run" => "the restart half of this test was not run",
        _ => PlainQualifier(qualifier),
    };

    // qualifier can itself be several reasons StateDeriver joined with "; " (CombineQualifier):
    // every one of them is said, not only the first, so the Result view never drops a caveat the
    // row itself still carries.
    internal static string ResultVerdictQualifierLine(string qualifier)
    {
        ArgumentNullException.ThrowIfNull(qualifier);
        IEnumerable<string> sentences = qualifier.Split("; ").Select(part => UpperFirst(ResultVerdictQualifierReason(part)) + ".");
        return string.Join(" ", sentences);
    }

    private static string UpperFirst(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // Result view, plain mode: a raw folder and file path is jargon, kept
    // behind technical details (ResultPanel's own EvidenceLabel, unchanged); this is what replaces
    // it, plus the button beside it that actually opens the folder.
    internal const string ResultRecordSavedLine = "A record of this run has been saved.";

    internal const string OpenFolderButtonLabel = "Open the folder";

    // Result view's own navigation, outside Run all: Run all's halt already offers
    // RunAllCarryOnButtonLabel/RunAllStopHereButtonLabel instead of this.
    internal const string BackToStartButtonLabel = "Back to the start";
}
