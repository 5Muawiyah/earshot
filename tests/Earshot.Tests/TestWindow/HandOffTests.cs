using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// M12 and M7 (review round 1). Drives the real MainForm.ShowHandOff / OnRunFinished, not a copy
// of their logic: ShowHandOffForTests takes only a DisplayRow and a ParsedResult, both
// constructible directly, so the real production method runs inside a real (headless) form.
[TestClass]
public sealed class HandOffTests
{
    // M12: LiveTest.psm1's Complete-LiveTestRun writes the no-on-purpose reason into the
    // leftAtRest finding's own Detail. ShowHandOff used to pass null unconditionally, so this
    // recorded reason never reached the owner: the hand-off screen said "No reason was recorded."
    // even when one had been.
    [TestMethod]
    public void NoOnPurposeShowsTheRecordedReasonRatherThanSayingNoneWasRecorded()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        var rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        using var sandbox = new TempFolder();

        DisplayRow row04 = DisplayRow.Flatten(rows).First(r => r.Number == "04");
        var firstHalfResult = new ParsedResult
        {
            Test = row04.TestId,
            Overall = "pass",
            Criteria = new[]
            {
                new CriterionRecord("block", "The block is on.", "pass", string.Empty),
                new CriterionRecord("disabled-now", "The nodes are disabled now.", "pass", string.Empty),
            },
            Findings = new[] { new FindingRecord("leftAtRest", "no-on-purpose", "This half leaves the nodes enabled to prove the reboot persists the block, not this run.") },
            StepCount = 2,
        };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHandOffForTests(row04, firstHalfResult);

            Assert.IsTrue(form.HandOffVisibleForTests);
            StringAssert.Contains(form.HandOffTextForTests,
                "This half leaves the nodes enabled to prove the reboot persists the block, not this run.");
            Assert.IsFalse(form.HandOffTextForTests.Contains("No reason was recorded.", StringComparison.Ordinal),
                "the recorded reason must be shown, not the fallback for a missing one.");
        });
    }

    [TestMethod]
    public void NoOnPurposeWithNoDetailStillFallsBackHonestly()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        var rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        using var sandbox = new TempFolder();

        DisplayRow row04 = DisplayRow.Flatten(rows).First(r => r.Number == "04");
        var firstHalfResult = new ParsedResult
        {
            Test = row04.TestId,
            Overall = "pass",
            Criteria = new[] { new CriterionRecord("block", "The block is on.", "pass", string.Empty) },
            Findings = new[] { new FindingRecord("leftAtRest", "no-on-purpose", string.Empty) },
            StepCount = 1,
        };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHandOffForTests(row04, firstHalfResult);
            StringAssert.Contains(form.HandOffTextForTests, "No reason was recorded.");
        });
    }

    // M7: a declined start (No at Show-Preconditions) never reaches the point of writing
    // resume.txt, so there is nothing for the hand-off screen's "come back and this window will
    // pick up here" to be about. Real sandboxed run against the unchanged fakes, real button
    // clicks, not a fixture standing in for OnRunFinished's own decision.
    [TestMethod]
    public void ADeclinedStartShowsNoHandOffScreen()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("04"));
            form.ClickStartForTests();

            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(10));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "the first prompt (Show-Preconditions) never arrived.");

            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900)); // click safety delay
            form.ClickPromptButtonForTests(1); // "No"

            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(15));
            Assert.IsNull(form.ActiveRunnerForTests, "the declined run never finished.");

            Assert.IsFalse(form.HandOffVisibleForTests, "a declined start (no resume.txt) must never show the hand-off screen.");
            Assert.IsTrue(form.ResultPanelVisibleForTests, "a declined start must show the ordinary result panel instead.");
        });
    }
}
