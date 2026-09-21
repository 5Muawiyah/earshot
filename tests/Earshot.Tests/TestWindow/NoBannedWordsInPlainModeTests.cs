using System.Reflection;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The point of the whole plain-words job: with technical details off, nothing this window can
// show contains a word from the plain-words guide's own banned list. Checks the actual data every
// on-screen surface is built from (wording.json's own plain lines, plain meanings, how-to steps and
// note-choice labels; every RowStateKind's plain label and every qualifier's plain translation;
// every Copy string and method that is shown whether the toggle is on or off), rather than driving
// a real render of every one of the sixteen scripts' every prompt, which would need a real
// PowerShell process per prompt and take far too long to run on every gate. Technical-only Copy
// members (Copy.Passed, Copy.Inconclusive, Copy.RowText and the rest of the pre-existing technical
// vocabulary) are outside this test's scope on purpose: they are shown only behind the toggle, so
// "with technical details off" never reaches them, and TechnicalDetailsToggleTests and
// StepPanelHowToRenderingTests already prove the toggle itself hides them.
//
// A fake at a boundary proves everything except the boundary: every check above is static, reading
// the data files and Copy.cs directly, and never actually drove MainForm's own status line or any
// other string it builds on the fly, which is exactly where three leaks (a TestId, a raw exit code
// and a raw path) used to live regardless of the toggle. NoBannedWordsInPlainModeThroughARealRun
// (bottom of this file) is the one execution that closes that class of defect: it drives the real,
// headless MainForm through a practice run of test 01, a failing practice run of test 11 and test
// 08's own hand-off, and scans every label it actually shows.
[TestClass]
public sealed class NoBannedWordsInPlainModeTests
{
    // Exactly the list named in the owner's instruction. " page " and " boot" keep their
    // surrounding spaces so "package"/"pages" and "reboot"/"bootstrap" are not false hits; " boot"
    // is allowed only inside the literal menu name "Block at boot", checked separately below.
    private static readonly string[] Banned =
    {
        "node", "service", "profile", "A2DP", "Hands-Free", "Handsfree", "HFP", "endpoint", "driver", "registry",
        "SYSTEM", "elevat", "UAC", "gate", "probe", "paging", " page ", "criterion", "inconclusive", "exit code",
        "json", "run root", "console", "parameter", "variant", "sandbox", "plan B", "idle grace", "one-shot",
        "filter", "container", "GUID", "persist", " boot", "at rest",

        // The second plain pass's own additions.
        "exit", ".ps1", "power cycle", "cold start", "ACCEPTANCE",
    };

    // "this PC" is enforced everywhere this commit actually owns and has made clean (Copy.cs, and
    // wording.json's own entries for tests 01, 08 and 11, the three scenarios
    // NoBannedWordsInPlainModeThroughARealRun drives below) but deliberately left out of the
    // shared Banned array above: wording.json still has "this PC" left in several other tests'
    // precondition, instruction and consequence sentences, a separate leftover-words sweep this
    // commit was not asked to make. Adding it to Banned would fail
    // NoWordingEntrysPlainTextContainsABannedWord (which scans every test's entries, not only the
    // three this commit touches) for text nobody has fixed yet.
    private static readonly string[] BannedIncludingThisPc = Banned.Concat(new[] { "this PC" }).ToArray();

    // Real on-screen names that legitimately contain a would-be hit ("Block at boot" contains
    // " boot"; "Exit" is a real Earshot tray menu item, plain-words.md's own exception for a menu
    // name kept verbatim, and test 12's instruction names the same click in its ordinary verb
    // form). Checked by removing each allowed phrase from the text before scanning for banned
    // words, so nothing else in the same sentence is given a free pass by sitting near it.
    private static readonly string[] AllowListedPhrases =
    {
        "Block at boot",

        // A real Windows navigation path (Settings > System > Display > Scale), named this way
        // since before the plain-words pass; a writer polishing this line can drop it in favour of
        // plain click-by-click steps (a how-to block), but it is a real on-screen path, not jargon.
        "Settings, System, Display, Scale",

        "choose Exit from the Earshot menu",
        "exit it from the menu",
    };

    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    [TestMethod]
    public void NoWordingEntrysPlainTextContainsABannedWord()
    {
        var problems = new List<string>();
        foreach (WordingEntry entry in LoadWording())
        {
            CheckText(entry.Plain, entry.Test + " " + entry.Kind + " plain", problems);
            if (entry.PlainMeaning is not null)
            {
                CheckText(entry.PlainMeaning, entry.Test + " " + entry.Kind + " plainMeaning", problems);
            }

            foreach (WordingChoice choice in entry.Choices)
            {
                CheckText(choice.Label, entry.Test + " " + entry.Kind + " choice label", problems);
            }

            foreach (HowToBlock block in entry.HowTo)
            {
                foreach (string step in block.Steps)
                {
                    CheckText(step, entry.Test + " " + entry.Kind + " how-to '" + block.Name + "'", problems);
                }
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void NoPlainRowStateLabelOrQualifierContainsABannedWord()
    {
        var problems = new List<string>();
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            CheckText(Copy.PlainBaseText(kind), "row state " + kind, problems, BannedIncludingThisPc);
        }

        foreach (string qualifier in new[] { "on an earlier build", "second half only on record", "shut down not confirmed", "run order not confirmed" })
        {
            CheckText(Copy.PlainQualifier(qualifier), "qualifier '" + qualifier + "'", problems, BannedIncludingThisPc);
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void NoAlwaysVisibleCopyStringContainsABannedWord()
    {
        var problems = new List<string>();
        foreach ((string name, string value) in AlwaysVisibleCopyStrings())
        {
            CheckText(value, "Copy." + name, problems, BannedIncludingThisPc);
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    // Every Copy member that is shown whether the toggle is on or off: this window's own words,
    // never the script's, so none of it is gated by the technical-details toggle. Constants read
    // by reflection (so a new one is covered automatically); the handful of methods are called
    // with representative arguments, since a banned word could just as easily be baked into a
    // format string as into a constant.
    private static IEnumerable<(string Name, string Value)> AlwaysVisibleCopyStrings()
    {
        var technicalOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Copy.NotRun), nameof(Copy.WaitingForShutDown), nameof(Copy.WaitingForRestart), nameof(Copy.Passed),
            nameof(Copy.Failed), nameof(Copy.Inconclusive), nameof(Copy.Unknown), nameof(Copy.StoppedBeforeAnyStep),
            nameof(Copy.Locked),
        };

        foreach (FieldInfo field in typeof(Copy).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string) || technicalOnly.Contains(field.Name))
            {
                continue;
            }

            if (field.GetValue(null) is string value)
            {
                yield return (field.Name, value);
            }
        }

        yield return (nameof(Copy.AtRestOnPurpose), Copy.AtRestOnPurpose("Because a live test needed it enabled."));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.FullShutDown));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.Restart));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.AnyStart));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("on"));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("off"));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("unknown"));
        yield return (nameof(Copy.RunAllStoppedForPowerCycle), Copy.RunAllStoppedForPowerCycle("08"));
        yield return (nameof(Copy.RunAllStoppedForFailure), Copy.RunAllStoppedForFailure("08"));
        yield return (nameof(Copy.RunAllProgressLine), Copy.RunAllProgressLine(1, 19, "Connect with one click"));
        yield return (nameof(Copy.RunAllSummarySentence), Copy.RunAllSummarySentence(3, 1, 1, 1));
        yield return (nameof(Copy.PlainCheckSummarySentence), Copy.PlainCheckSummarySentence(4, 0, 0));
        yield return (nameof(Copy.PlainCheckSummarySentence), Copy.PlainCheckSummarySentence(5, 2, 1));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("yes", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("no", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("no-on-purpose", "Because a live test needed it enabled."));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("not-applicable", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("unknown", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText(null, null));

        // The second plain pass's own status-line builders (MainForm._statusLabel /
        // _runAllStatusLabel), called with representative arguments the same way the methods above
        // already are.
        yield return (nameof(Copy.PlainRunningStatus), Copy.PlainRunningStatus("Connect with one click", isResume: false));
        yield return (nameof(Copy.PlainRunningStatus), Copy.PlainRunningStatus("Full shut down and start", isResume: true));
        yield return (nameof(Copy.PlainFinishedStatus), Copy.PlainFinishedStatus("Connect with one click", Copy.PlainPassed));
        yield return (nameof(Copy.PlainFinishedStatus), Copy.PlainFinishedStatus("Battery reading, connected or not", Copy.PlainFailed));
        yield return (nameof(Copy.PlainRunCrashedStatus), Copy.PlainRunCrashedStatus("Connect with one click"));
        yield return (nameof(Copy.PlainNoReadableResultStatus), Copy.PlainNoReadableResultStatus("Connect with one click"));
        yield return (nameof(Copy.PlainWaitingForPowerCycleStatus), Copy.PlainWaitingForPowerCycleStatus(PowerCycleRequirement.FullShutDown, "Full shut down and start"));
        yield return (nameof(Copy.PlainWaitingForPowerCycleStatus), Copy.PlainWaitingForPowerCycleStatus(PowerCycleRequirement.Restart, "Full shut down and start"));
        yield return (nameof(Copy.PlainWaitingForPowerCycleStatus), Copy.PlainWaitingForPowerCycleStatus(PowerCycleRequirement.AnyStart, "Full shut down and start"));
        yield return (nameof(Copy.SilentForMinutesStatus), Copy.SilentForMinutesStatus(15));
    }

    private static void CheckText(string text, string source, List<string> problems, string[]? bannedWords = null)
    {
        string scanned = text;
        foreach (string allowed in AllowListedPhrases)
        {
            scanned = scanned.Replace(allowed, string.Empty, StringComparison.Ordinal);
        }

        foreach (string banned in bannedWords ?? Banned)
        {
            if (scanned.Contains(banned, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(source + " contains banned word '" + banned.Trim() + "': \"" + text + "\"");
            }
        }
    }

    // The one execution in this file: a real, headless MainForm, driven through a practice run of
    // test 01 to a clean pass, a practice run of test 11 to a genuine failure (the sandbox's
    // default fake-input case, "one", recomputes 11 to overall "fail" without anything faked here),
    // and test 08's own first half to its hand-off screen, collecting every label the window
    // actually shows at every prompt and result and scanning all of it. Before the status-line fix
    // this failed on "Running 01-a2dp-oneshot...", a TestId; after it, it passes.
    [TestClass]
    public sealed class NoBannedWordsInPlainModeThroughARealRun
    {
        [TestMethod]
        public void APracticeRunOfTestOneNeverShowsATestIdScriptNameExitCodeOrPath()
        {
            string host = PowerShell51.ExecutablePath();
            if (!File.Exists(host))
            {
                Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
            }

            using var sandbox = new TempFolder();
            var problems = new List<string>();
            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                DriveRowAnsweringFirstButtonThroughout(form, "01", problems, stopAtHandOff: false, TimeSpan.FromSeconds(120));

                Assert.IsNull(form.ActiveRunnerForTests, "the practice run of test 01 never finished within the deadline.");
                Assert.IsTrue(form.ResultPanelVisibleForTests, "test 01 finished without ever showing a result.");
                CollectResultLabels(form, "01 result", problems);
            });

            Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void AFailingPracticeRunOfTestElevenNeverShowsATestIdScriptNameExitCodeOrPath()
        {
            string host = PowerShell51.ExecutablePath();
            if (!File.Exists(host))
            {
                Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
            }

            using var sandbox = new TempFolder();
            var problems = new List<string>();
            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                DriveRowAnsweringFirstButtonThroughout(form, "11", problems, stopAtHandOff: false, TimeSpan.FromSeconds(120));

                Assert.IsNull(form.ActiveRunnerForTests, "the practice run of test 11 never finished within the deadline.");
                Assert.IsTrue(form.ResultPanelVisibleForTests, "test 11 finished without ever showing a result.");
                CollectResultLabels(form, "11 result", problems);

                // This scenario is only proof of the fail path if it genuinely failed: the sandbox's
                // default fake-input case ("one") recomputes 11 to overall "fail" without anything
                // faked by this test, so this is a real failing run, not a synthetic one.
                Assert.AreEqual(
                    Copy.PlainFailed, form.RowStateTextForTests("11"),
                    "test 11 did not genuinely fail against the sandbox's default fake-input case; this scenario " +
                    "no longer proves the fail path this test exists to cover.");
            });

            Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void TestEightsFirstHalfHandOffNeverShowsATestIdScriptNameExitCodeOrPath()
        {
            string host = PowerShell51.ExecutablePath();
            if (!File.Exists(host))
            {
                Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
            }

            using var sandbox = new TempFolder();
            var problems = new List<string>();
            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                DriveRowAnsweringFirstButtonThroughout(form, "08", problems, stopAtHandOff: true, TimeSpan.FromSeconds(180));

                Assert.IsTrue(form.HandOffVisibleForTests, "test 08's first half never reached its hand-off screen.");
                CheckText(form.StatusTextForTests, "08 hand-off status", problems, BannedIncludingThisPc);
                CheckText(form.HandOffTextForTests, "08 hand-off text", problems, BannedIncludingThisPc);
            });

            Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        }

        // Answers the first (affirmative) button at every prompt, the same convention
        // RowStateRefreshesAfterARunTests.cs already relies on for a clean pass of row 01, and
        // which this file's own AFailingPracticeRunOfTestEleven test above relies on for a genuine
        // fail of row 11: PromptPresenter's own Buttons ordering puts the affirmative answer
        // first. Collects every label's text at every prompt along the way, technical details left
        // off (the default), and stops either when the run ends (stopAtHandOff false) or the
        // moment the hand-off screen appears (stopAtHandOff true, test 08's own first half).
        private static void DriveRowAnsweringFirstButtonThroughout(
            MainForm form, string rowNumber, List<string> problems, bool stopAtHandOff, TimeSpan deadlineFromNow)
        {
            Assert.IsTrue(form.SelectRowForTests(rowNumber), "\"" + rowNumber + "\" was not found in the real manifest.");
            form.ClickStartForTests();

            DateTime deadline = DateTime.UtcNow + deadlineFromNow;
            while (DateTime.UtcNow < deadline)
            {
                MainFormTestHarness.PumpUntil(
                    () => form.CurrentPromptSeqForTests is not null || form.ActiveRunnerForTests is null || form.HandOffVisibleForTests,
                    TimeSpan.FromSeconds(30));

                if (stopAtHandOff && form.HandOffVisibleForTests)
                {
                    return;
                }

                if (form.ActiveRunnerForTests is null)
                {
                    return;
                }

                if (form.CurrentPromptSeqForTests is null)
                {
                    continue;
                }

                CollectPromptLabels(form, rowNumber, problems);

                // StepPanel disables its buttons for 800 ms after a prompt appears (absorbs a click
                // meant for the previous prompt); PerformClick is a no-op until that has elapsed.
                MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));
                form.ClickCurrentPromptButtonForTests();
            }

            Assert.Fail("row " + rowNumber + " did not finish (or, for 08, reach its hand-off) within " + deadlineFromNow + ".");
        }

        // Every label StepPanel can show for the current prompt, technical details off: the status
        // line, the heading, the plain instruction/question/checklist line, the plain checklist
        // items, the how-to steps, and the acknowledgement line (Wait-Owner's "No").
        private static void CollectPromptLabels(MainForm form, string rowNumber, List<string> problems)
        {
            string context = "row " + rowNumber + " prompt";
            CheckText(form.StatusTextForTests, context + " status", problems, BannedIncludingThisPc);

            StepPanel stepPanel = form.StepPanelForTests;
            CheckText(stepPanel.HeadingTextForTests, context + " heading", problems, BannedIncludingThisPc);
            CheckText(stepPanel.PlainLineTextForTests, context + " plain line", problems, BannedIncludingThisPc);
            foreach (string item in stepPanel.ListItemsForTests)
            {
                CheckText(item, context + " checklist item", problems, BannedIncludingThisPc);
            }

            CheckText(stepPanel.HowToStepsTextForTests, context + " how-to steps", problems, BannedIncludingThisPc);
            CheckText(stepPanel.AcknowledgementTextForTests, context + " acknowledgement", problems, BannedIncludingThisPc);
        }

        // Every label ResultPanel can show once a run ends, technical details off: the status
        // line, the plain summary sentence, the plain per-check lines, and the left-at-rest line.
        // The saved-record path is jargon on purpose (ResultPanel now hides it off the toggle) and
        // is checked for that, not scanned for banned words, since it is a real Windows path.
        private static void CollectResultLabels(MainForm form, string rowNumber, List<string> problems)
        {
            string context = "row " + rowNumber + " result";
            CheckText(form.StatusTextForTests, context + " status", problems, BannedIncludingThisPc);

            ResultPanel resultPanel = form.ResultPanelForTests;
            Assert.IsFalse(
                resultPanel.EvidenceVisibleForTests,
                context + ": the saved record's path must never show with technical details off.");
            CheckText(resultPanel.PlainSummaryTextForTests, context + " summary", problems, BannedIncludingThisPc);
            CheckText(resultPanel.PlainLinesTextForTests, context + " check lines", problems, BannedIncludingThisPc);
            CheckText(resultPanel.AtRestTextForTests, context + " at-rest line", problems, BannedIncludingThisPc);
        }
    }
}
