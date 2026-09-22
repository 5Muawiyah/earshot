using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Run all's own presentation: the progress line while it runs, and a Locked row (only row 15,
// before the administrator prompt check has passed) being skipped over with its own plain reason
// rather than halting the whole sequence for it, since ComputeState still reads it as Locked
// afterwards, which is exactly what the end-of-sequence summary (RunAllSummary) reads too.
[TestClass]
public sealed class RunAllPresentationTests
{
    [TestMethod]
    public void StartingRunAllShowsAProgressLineNamingTheTestAndItsPosition()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is not null, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(form.ActiveRunnerForTests, "Run all never started its first item.");
            Assert.AreEqual("01", form.ActiveRowNumberForTests);

            Assert.IsTrue(form.RunAllProgressVisibleForTests, "no progress line was shown while Run all was under way.");
            Assert.AreEqual("Test 1 of " + RunAllOrder.Items.Count + ": Connect with one click", form.RunAllProgressTextForTests);
        });
    }

    [TestMethod]
    public void ALockedRowIsSkippedRatherThanHaltingTheWholeSequence()
    {
        using var sandbox = new TempFolder();
        string windowStateRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest-gui");
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // Row 15 is Locked in a fresh sandbox (no administrator prompt check has ever run).
        // Jumping straight to its index proves the skip alone, without needing every earlier item
        // genuinely run or faked first. 15 is no longer the last item (17 and 18 follow it), so
        // reaching the true end of the sequence from here also needs 17 and 18 to already read as
        // a clean, settled pass: otherwise AdvanceRunAll would start a real child for 17 once 15 is
        // skipped, which this fast, offline test is not set up to do.
        // A second-half-only criterion (see ManifestNameProvesTests's row 17 fixture), not a
        // generic "c1": StateDeriver reads which half a result.json belongs to from the criteria
        // it carries, and a result with none of 17's second-half markers reads as only the first
        // half having run, which halts waiting for a restart rather than advancing past it.
        ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260921T000000Z", "17-handback-on-shutdown", "result.json"),
            new ResultJsonFixture("17-handback-on-shutdown", "pass").WithCriterion("nodes-after-boot", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-21T00:00:00.000Z").Build());
        ResultJsonFixture.WriteTo(Path.Combine(liveTestRoot, "20260921T010000Z", "18-handback-on-sleep", "result.json"),
            new ResultJsonFixture("18-handback-on-sleep", "pass").WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-21T01:00:00.000Z").Build());

        int rowFifteenIndex = System.Linq.Enumerable.ToList(RunAllOrder.Items).FindIndex(i => i.RowNumber == "15");
        Assert.AreNotEqual(-1, rowFifteenIndex, "row 15 was not found in Run all's own order.");
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(RunAllOrder.Items, RunAllFile.Key)),
            StoppedAtIndex = rowFifteenIndex,
        });

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsNull(form.ActiveRunnerForTests, "a Locked row must never be started.");
            Assert.IsFalse(form.RunAllActiveForTests, "skipping the last, Locked item must reach the end of the sequence, not stay halted.");
            StringAssert.Contains(form.RunAllStatusTextForTests, "15");
            StringAssert.Contains(form.RunAllStatusTextForTests.ToLowerInvariant(), "permission box check");
        });
    }

    [TestMethod]
    public void StopHereEndsRunAllWithoutOfferingToCarryOn()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // A genuine failure on row 01 so Run all halts with both buttons offered.
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "fail").WithCriterion("c1", "fail").WithFinding("leftAtRest", "yes").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsTrue(form.CarryOnVisibleForTests, "Run all did not halt on the failed row.");
            Assert.IsTrue(form.RunAllStopHereVisibleForTests, "'Stop here' was not offered beside 'Carry on with the rest'.");

            form.ClickRunAllStopHereForTests();

            Assert.IsFalse(form.CarryOnVisibleForTests);
            Assert.IsFalse(form.RunAllStopHereVisibleForTests);
            Assert.IsFalse(form.RunAllActiveForTests);
            Assert.AreEqual(Copy.RunAllButtonLabel, form.RunAllButtonTextForTests, "Stop here must clear the stopped point, not leave 'Carry on with the tests' behind.");
        });
    }
}
