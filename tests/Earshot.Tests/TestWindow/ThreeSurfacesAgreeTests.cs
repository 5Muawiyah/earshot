using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The row's own plain state, the Result view's verdict line, and Run all's own end-of-sequence
// summary are three separate renderings of the very same DerivedRowState: all three must agree
// about whether a row actually settled the question it exists to settle, for a qualified pass, a
// fail, an unknown and a kill. Before this fix, RunAllSummary.Classify read only Kind, so a
// qualified pass (Kind Passed, a non-null Qualifier: an unconfirmed shut down, an earlier build,
// and the rest) counted as "worked" in "N tests worked" even though the row and the Result view
// both already refuse to call it a clean pass.
[TestClass]
public sealed class ThreeSurfacesAgreeTests
{
    [TestMethod]
    public void AQualifiedPassNeverReadsAsWorkedOnAnyOfTheThreeSurfaces()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        RunEvidence run = BuildPassingEvidence(spec.TestId, leftAtRest: "yes", exe: @"C:\Old\Earshot.exe");

        DerivedRowState state = StateDeriver.Derive(spec, new[] { run }, chosenExePath: @"C:\New\Earshot.exe", chosenExeLastWriteUtc: DateTimeOffset.UtcNow);

        Assert.AreEqual(RowStateKind.Passed, state.Kind, "fixture sanity: the underlying result must itself be a pass.");
        Assert.IsNotNull(state.Qualifier, "fixture sanity: an earlier-build mismatch must produce a qualifier.");
        Assert.IsFalse(state.IsGreen, "a qualified pass must never read as a clean, fully-settled pass.");

        // The row.
        string rowText = Copy.PlainRowText(state);
        Assert.AreNotEqual(Copy.PlainPassed, rowText, "row: a qualified pass must not read as the bare \"Worked\".");

        // The Result view: the same rule ResultPanel.Show itself applies, checked directly against
        // the Copy functions ResultPanel.Show itself calls.
        string verdictLine = Copy.ResultVerdictLine(state.Kind, state.Qualifier);
        Assert.IsFalse(verdictLine.Contains(Copy.ResultVerdictPassed, StringComparison.Ordinal),
            "Result view: a qualified pass must not show the clean \"This test worked.\" sentence.");
        StringAssert.Contains(verdictLine, Copy.ResultVerdictQualifiedPassed);

        // Run all's own end-of-sequence summary.
        RunAllSummaryBucket bucket = RunAllSummary.Classify(state);
        Assert.AreNotEqual(RunAllSummaryBucket.Worked, bucket,
            "Run all's own summary: a qualified pass must not be counted among the tests that worked.");

        // All three agree: none of them calls this a settled, worked pass.
    }

    [TestMethod]
    public void AGenuineFailReadsAsDidNotWorkOnAllThreeSurfaces()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        RunEvidence run = BuildEvidence(spec.TestId, overall: "fail", criterionOutcome: "fail", leftAtRest: "yes");

        DerivedRowState state = StateDeriver.Derive(spec, new[] { run }, chosenExePath: null, chosenExeLastWriteUtc: null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);

        Assert.AreEqual(Copy.PlainFailed, Copy.PlainRowText(state));
        StringAssert.Contains(Copy.ResultVerdictLine(state.Kind, state.Qualifier), Copy.ResultVerdictFailed);
        Assert.AreEqual(RunAllSummaryBucket.DidNotWork, RunAllSummary.Classify(state));
    }

    [TestMethod]
    public void AnUnknownRowNeverReadsAsWorkedOnAnyOfTheThreeSurfaces()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        RunEvidence run = new()
        {
            Stamp = "20260921T000000Z",
            Folder = @"C:\nowhere\20260921T000000Z",
            Result = null,
            ReadFailureReason = "no result.json",
        };

        DerivedRowState state = StateDeriver.Derive(spec, new[] { run }, chosenExePath: null, chosenExeLastWriteUtc: null);
        Assert.AreEqual(RowStateKind.Unknown, state.Kind);

        Assert.AreEqual(Copy.PlainUnknown, Copy.PlainRowText(state));
        StringAssert.Contains(Copy.ResultVerdictLine(state.Kind, state.Qualifier), Copy.ResultVerdictUnknown);
        Assert.AreNotEqual(RunAllSummaryBucket.Worked, RunAllSummary.Classify(state));
    }

    [TestMethod]
    public void AKilledRowNeverReadsAsWorkedOnTheRowOrInRunAllsSummary()
    {
        // A kill has no ParsedResult of its own to show on the Result view (MainForm never even
        // switches to that view for one: MarkUnknownAndReset hides it outright), so only the row
        // and Run all's own summary are checked here.
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        RunEvidence run = new()
        {
            Stamp = "20260921T000000Z",
            Folder = @"C:\nowhere\20260921T000000Z",
            Result = null,
            ReadFailureReason = "no result.json",
            HasKilledMarker = true,
        };

        DerivedRowState state = StateDeriver.Derive(spec, new[] { run }, chosenExePath: null, chosenExeLastWriteUtc: null);
        Assert.AreEqual(RowStateKind.Unknown, state.Kind, "fixture sanity: a kill reads Unknown.");

        Assert.AreEqual(Copy.PlainUnknown, Copy.PlainRowText(state));
        Assert.AreNotEqual(RunAllSummaryBucket.Worked, RunAllSummary.Classify(state));
    }

    private static RunEvidence BuildPassingEvidence(string testId, string leftAtRest, string exe) =>
        BuildEvidence(testId, overall: "pass", criterionOutcome: "pass", leftAtRest: leftAtRest, exe: exe);

    private static RunEvidence BuildEvidence(string testId, string overall, string criterionOutcome, string leftAtRest, string? exe = null) =>
        new()
        {
            Stamp = "20260921T000000Z",
            Folder = @"C:\nowhere\20260921T000000Z",
            Result = new ParsedResult
            {
                Test = testId,
                Overall = overall,
                Criteria = new[] { new CriterionRecord("c1", "criterion text", criterionOutcome, string.Empty) },
                Findings = new[] { new FindingRecord("leftAtRest", leftAtRest, string.Empty) },
                StepCount = 1,
                Exe = exe,
                StartedUtc = exe is null ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
            },
        };
}
