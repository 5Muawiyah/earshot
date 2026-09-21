using System.Drawing;
using System.Windows.Forms;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// the layout's Result view redesign: one large verdict line with a symbol, using the
// exact four sentences the spec quotes verbatim, sourced from the same DerivedRowState MainForm
// already computed for the row (never re-derived here); the raw evidence paths staying jargon
// behind technical details, replaced in plain mode by a saved-record line and a real "Open the
// folder" button (never Explorer itself during a test); and Run-all-aware buttons ("Carry on with
// the rest"/"Stop here") in place of a new "Back to the start" outside Run all. Real MainForm, real
// sandboxed scripts throughout, the same driving pattern RowStateRefreshesAfterARunTests and
// StepPanelLayoutTests already use.
[TestClass]
public sealed class ResultViewVerdictAndLayoutTests
{
    // Answers the first (affirmative) button at every prompt (PromptPresenter's own Buttons
    // ordering), the same convention RowStateRefreshesAfterARunTests relies on for test 01's clean
    // pass and NoBannedWordsInPlainModeTests relies on for test 11's genuine failure (the sandbox's
    // default "one" fake-input case recomputes 11 to overall "fail" without anything faked here).
    private static void DriveRowToResultAnsweringFirstButtonThroughout(MainForm form, string rowNumber)
    {
        Assert.IsTrue(form.SelectRowForTests(rowNumber), "\"" + rowNumber + "\" was not found in the real manifest.");
        form.ClickStartForTests();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (form.ActiveRunnerForTests is not null && DateTime.UtcNow < deadline)
        {
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null || form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(30));
            if (form.ActiveRunnerForTests is null)
            {
                break;
            }

            if (form.CurrentPromptSeqForTests is null)
            {
                continue;
            }

            // StepPanel disables its buttons for 800 ms after a prompt appears; PerformClick is a
            // no-op until that has elapsed.
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));
            form.ClickCurrentPromptButtonForTests();
        }

        Assert.IsNull(form.ActiveRunnerForTests, "row " + rowNumber + " did not finish within the deadline.");
        Assert.IsTrue(form.ResultPanelVisibleForTests, "row " + rowNumber + " finished without ever showing a result.");
    }

    // (a) A genuine failure of test 11: the exact "This test did not work." verdict line, no
    // clipped label anywhere on the Result view, the plain failure lines and the left-at-rest line
    // present, and no raw file path visible with technical details off.
    [TestMethod]
    public void ResultForAFailed11ShowsTheFailedVerdictWithNoClippedLabelAndNoRawPath()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            DriveRowToResultAnsweringFirstButtonThroughout(form, "11");

            Assert.AreEqual(
                Copy.PlainFailed, form.RowStateTextForTests("11"),
                "test 11 did not genuinely fail against the sandbox's default fake-input case; this scenario no longer proves the fail path.");

            ResultPanel panel = form.ResultPanelForTests;
            Application.DoEvents();

            Assert.AreEqual(Copy.ResultVerdictLine(RowStateKind.Failed), panel.VerdictTextForTests);
            StringAssert.Contains(panel.VerdictTextForTests, Copy.ResultVerdictFailed);

            Assert.IsFalse(string.IsNullOrWhiteSpace(panel.PlainLinesTextForTests), "the plain lines listing what did not work must be present.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(panel.AtRestTextForTests), "the left-at-rest line must be present in plain words.");

            Assert.IsFalse(panel.EvidenceVisibleForTests, "no raw file path may be visible with technical details off.");

            var offenders = new List<string>();
            CollectClippedLabels(panel, offenders);
            Assert.IsEmpty(offenders, "Clipped label(s) on Result for a failed 11:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        });
    }

    // (b) The plain-mode saved-record line and the "Open the folder" button both appear, and
    // clicking the button invokes the open-folder mechanism with the evidence folder, never a real
    // Explorer window during the test.
    [TestMethod]
    public void PlainModeShowsTheSavedRecordLineAndOpeningTheFolderNeverShellsOut()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            DriveRowToResultAnsweringFirstButtonThroughout(form, "01");

            ResultPanel panel = form.ResultPanelForTests;
            Assert.IsTrue(panel.PlainRecordSavedVisibleForTests, "the saved-record line must show with technical details off.");
            Assert.AreEqual(Copy.ResultRecordSavedLine, panel.PlainRecordSavedTextForTests);
            Assert.IsTrue(panel.OpenFolderButtonVisibleForTests, "the Open the folder button must show with technical details off.");

            string? opened = null;
            panel.OpenFolderForTests = path => opened = path;
            panel.ClickOpenFolderButtonForTests();

            Assert.IsNotNull(opened, "clicking Open the folder never invoked the open-folder mechanism.");
            StringAssert.Contains(opened!, "livetest");
        });
    }

    // (c) The four verdict sentences, verbatim. Passed and Failed are driven through a real
    // sandboxed run (the same "01 passes, 11 fails" pair as (a) and RowStateRefreshesAfterARunTests);
    // Inconclusive and Unknown are driven directly against ResultPanel (ResultPanelForTests), the
    // same "drive the real production method directly" pattern ShowHandOffForTests already uses for
    // a case the ordinary UI cannot conveniently reach: a genuine Inconclusive needs a fragile mid-
    // run interruption to reach headlessly, and Unknown is a forced-kill/silent-exit state
    // (MarkUnknownAndReset) that never shows the Result view through the real UI at all (it hides
    // ResultPanel outright; SilentChildExitReadsUnknownTests pins that). ResultPanel.Show itself
    // takes only a ResultPresentation and a RowStateKind, both constructible directly from a
    // fixture, so this proves the same rendering code path a real run would reach.
    [TestMethod]
    public void PassedShowsTheExactPassedVerdictSentence()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            DriveRowToResultAnsweringFirstButtonThroughout(form, "01");
            Assert.AreEqual(Copy.PlainPassed, form.RowStateTextForTests("01"));
            Assert.AreEqual(Copy.ResultVerdictLine(RowStateKind.Passed), form.ResultPanelForTests.VerdictTextForTests);
            StringAssert.Contains(form.ResultPanelForTests.VerdictTextForTests, Copy.ResultVerdictPassed);
        });
    }

    [TestMethod]
    public void FailedShowsTheExactFailedVerdictSentence()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            DriveRowToResultAnsweringFirstButtonThroughout(form, "11");
            Assert.AreEqual(Copy.PlainFailed, form.RowStateTextForTests("11"));
            StringAssert.Contains(form.ResultPanelForTests.VerdictTextForTests, Copy.ResultVerdictFailed);
        });
    }

    [TestMethod]
    public void InconclusiveShowsTheExactCouldNotTellVerdictSentence()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            var result = new ParsedResult
            {
                Test = "01",
                Overall = "inconclusive",
                Criteria = new[] { new CriterionRecord("c1", "criterion text", "inconclusive", "detail") },
                Findings = Array.Empty<FindingRecord>(),
                Errors = Array.Empty<string>(),
                StepCount = 1,
            };
            ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");

            ResultPanel panel = form.ResultPanelForTests;
            panel.Show(presentation, showTechnicalDetails: false, RowStateKind.Inconclusive);

            Assert.AreEqual(Copy.ResultVerdictLine(RowStateKind.Inconclusive), panel.VerdictTextForTests);
            StringAssert.Contains(panel.VerdictTextForTests, Copy.ResultVerdictInconclusive);
        });
    }

    [TestMethod]
    public void UnknownShowsTheExactHarshestVerdictSentence()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            // The shape a forced kill/silent exit leaves: no criteria at all, never a pass.
            var result = new ParsedResult
            {
                Test = "01",
                Overall = "fail",
                Criteria = Array.Empty<CriterionRecord>(),
                Findings = Array.Empty<FindingRecord>(),
                Errors = Array.Empty<string>(),
                StepCount = 0,
            };
            ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");

            ResultPanel panel = form.ResultPanelForTests;
            panel.Show(presentation, showTechnicalDetails: false, RowStateKind.Unknown);

            Assert.AreEqual(Copy.ResultVerdictLine(RowStateKind.Unknown), panel.VerdictTextForTests);
            StringAssert.Contains(panel.VerdictTextForTests, Copy.ResultVerdictUnknown);
        });
    }

    // (d) Run-all-aware buttons versus "Back to the start".
    [TestMethod]
    public void RunAllButtonsShowInsteadOfBackToTheStartWhenAResultHaltsRunAll()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "fail").WithCriterion("c1", "fail").WithFinding("leftAtRest", "yes").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsTrue(form.CarryOnVisibleForTests, "Run all did not halt on the failed row.");
            Assert.IsTrue(form.RunAllStopHereVisibleForTests);
            Assert.IsFalse(form.BackToStartVisibleForTests, "\"Back to the start\" must not show while Run all offers its own two buttons.");
        });
    }

    [TestMethod]
    public void BackToTheStartShowsInsteadOfRunAllButtonsForAnOrdinarySingleTestStart()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsFalse(form.RunAllActiveForTests, "sanity: this must be an ordinary single-test start, not Run all.");
            DriveRowToResultAnsweringFirstButtonThroughout(form, "01");

            Assert.IsTrue(form.BackToStartVisibleForTests, "\"Back to the start\" must show for an ordinary single-test result.");
            Assert.IsFalse(form.CarryOnVisibleForTests);
            Assert.IsFalse(form.RunAllStopHereVisibleForTests);

            form.ClickBackToStartForTests();
            Assert.AreEqual(MainForm.MainView.Home, form.CurrentViewForTests, "Back to the start must return to Home.");
        });
    }

    // Same technique as HomeViewNoClippedLabelsTests/StepPanelLayoutTests: Label.GetPreferredSize
    // is the exact calculation WinForms itself uses to size an AutoSize label, so asking it for the
    // size needed at this label's own current width is the direct definition of not clipped.
    private static void CollectClippedLabels(Control root, List<string> offenders)
    {
        if (!root.Visible)
        {
            return;
        }

        foreach (Control child in root.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            if (child is Label label && !string.IsNullOrEmpty(label.Text) && label.Width > 0 && label.Height > 0)
            {
                Size needed = label.GetPreferredSize(new Size(label.Width, 0));
                if (needed.Width > label.Width + 4 || needed.Height > label.Height + 4)
                {
                    string shortText = label.Text.Length > 40 ? string.Concat(label.Text.AsSpan(0, 40), "...") : label.Text;
                    offenders.Add(
                        (string.IsNullOrEmpty(label.Name) ? label.GetType().Name : label.Name) + " (\"" + shortText + "\"): needs " +
                        needed + " but has " + label.Size);
                }
            }

            CollectClippedLabels(child, offenders);
        }
    }
}
