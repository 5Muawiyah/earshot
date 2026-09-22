using System.Linq;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// the layout's Result section, its last bullet: "End of Run all: a summary (how many
// worked, did not work, could not tell) and the list of tests not run yet with the plain reason
// for each." Investigation (MainForm.cs, BuildRunAllEndSummarySentence): the per-test reason list
// already existed before this commit touched anything - it is not a gap. BuildRunAllEndSummarySentence
// already appends one "<number> (<name>): <reason>" entry per row RunAllSummary.Classify puts in
// NotRunYet, after the counts sentence, and RunAllPresentationTests.ALockedRowIsSkippedRatherThanHaltingTheWholeSequence
// already exercises one such entry (row 15's own Locked reason). This test is the explicit proof
// this task asked for, checked against the unmodified baseline too (it needs no new member this
// commit adds), so it is recorded here as already-green rather than red-then-green.
[TestClass]
public sealed class RunAllEndSummaryReasonListTests
{
    [TestMethod]
    public void TheEndOfSequenceSummaryNamesTheCountsAndEachNotYetRunRowsOwnPlainReason()
    {
        using var sandbox = new TempFolder();
        string windowStateRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest-gui");
        string liveTestRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // Row 15 is Locked in a fresh sandbox (no administrator prompt check has ever run);
        // jumping straight to it (the same fixture
        // RunAllPresentationTests.ALockedRowIsSkippedRatherThanHaltingTheWholeSequence uses)
        // reaches the end of the sequence without needing every earlier item genuinely run or
        // faked first, and leaves every row up to and including 15 without settled evidence, so
        // each one is a genuine "not yet run" row for this summary to name. 15 is no longer the
        // last item (17 and 18 follow it), so 17 and 18 are given a settled, clean pass here:
        // otherwise AdvanceRunAll would start a real child for 17 once 15 is skipped, which this
        // fast, offline test is not set up to do.
        // A second-half-only criterion, not a generic "c1": StateDeriver reads which half a
        // result.json belongs to from the criteria it carries, and a result with none of 17's
        // second-half markers reads as only the first half having run, which halts waiting for a
        // restart rather than advancing past it.
        ResultJsonFixture.WriteTo(System.IO.Path.Combine(liveTestRoot, "20260921T000000Z", "17-handback-on-shutdown", "result.json"),
            new ResultJsonFixture("17-handback-on-shutdown", "pass").WithCriterion("nodes-after-boot", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-21T00:00:00.000Z").Build());
        ResultJsonFixture.WriteTo(System.IO.Path.Combine(liveTestRoot, "20260921T010000Z", "18-handback-on-sleep", "result.json"),
            new ResultJsonFixture("18-handback-on-sleep", "pass").WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-21T01:00:00.000Z").Build());

        int rowFifteenIndex = RunAllOrder.Items.ToList().FindIndex(i => i.RowNumber == "15");
        Assert.AreNotEqual(-1, rowFifteenIndex, "row 15 was not found in Run all's own order.");
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(RunAllFile.Key).ToArray(),
            StoppedAtIndex = rowFifteenIndex,
        });

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsFalse(form.RunAllActiveForTests, "skipping the last, Locked item must reach the end of the sequence, not stay halted.");

            string summary = form.RunAllStatusTextForTests;
            StringAssert.Contains(summary, "were not run yet", "the end-of-Run-all summary must name how many tests were never run, not only list them.");
            StringAssert.Contains(summary, "15 (", "row 15's own not-yet-run entry must be named by its number.");
            StringAssert.Contains(summary, Copy.LockedDetail, "row 15's own plain reason (Locked) must be named, not only its count.");
        });
    }
}
