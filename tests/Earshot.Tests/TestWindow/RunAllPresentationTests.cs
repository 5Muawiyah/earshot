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

        // Row 15 is the last item in Run all's own order and is Locked in a fresh sandbox (no
        // administrator prompt check has ever run): jumping straight to it proves the skip alone,
        // without needing every earlier item genuinely run or faked first.
        int lastIndex = RunAllOrder.Items.Count - 1;
        Assert.AreEqual("15", RunAllOrder.Items[lastIndex].RowNumber);
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(RunAllOrder.Items, RunAllFile.Key)),
            StoppedAtIndex = lastIndex,
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
