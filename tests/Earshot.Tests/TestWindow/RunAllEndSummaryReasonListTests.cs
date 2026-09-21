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

        // Row 15 is the last item in Run all's own order and is Locked in a fresh sandbox (no
        // administrator prompt check has ever run); jumping straight to it (the same fixture
        // RunAllPresentationTests.ALockedRowIsSkippedRatherThanHaltingTheWholeSequence uses) reaches
        // the end of the sequence without needing every earlier item genuinely run or faked first,
        // and leaves every row (15 included) without settled evidence, so every one of them is a
        // genuine "not yet run" row for this summary to name.
        int lastIndex = RunAllOrder.Items.Count - 1;
        Assert.AreEqual("15", RunAllOrder.Items[lastIndex].RowNumber);
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(RunAllFile.Key).ToArray(),
            StoppedAtIndex = lastIndex,
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
