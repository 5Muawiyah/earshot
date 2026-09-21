using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A second half that dies together with the window (a forced session end, a power cut) never
// reaches KillActiveRun or MarkUnknownAndReset, so it leaves no gui-killed.txt: the first half's
// own resume.txt and stale result.json are all that is left, exactly the shape of a genuinely
// still-pending first half. HasStaleRunStartedMarker (EvidenceStore, from gui-run-started.txt)
// closes that gap: read here as a pure fact on RunEvidence, the same way HasKilledMarker
// (KilledMarkerTests.cs) already is, so this row must be Unknown, never "Waiting", the same as a
// kill.
[TestClass]
public sealed class RunStartedMarkerStateDeriverTests
{
    private static RunEvidence Evidence(string stamp, ParsedResult? result, bool hasResumeFile, bool staleRunStartedMarker) => new()
    {
        Stamp = stamp,
        Folder = @"C:\nowhere\" + stamp,
        Result = result,
        HasResumeFile = hasResumeFile,
        HasStaleRunStartedMarker = staleRunStartedMarker,
    };

    private static ParsedResult FirstHalfResult(TestRowSpec spec) => new()
    {
        Test = spec.TestId,
        Overall = "inconclusive",
        Criteria = new[] { new CriterionRecord(spec.FirstHalfOnlyCriteriaIds[0], "criterion text", "pass", string.Empty) },
        Findings = Array.Empty<FindingRecord>(),
        StepCount = 1,
    };

    [TestMethod]
    public void AStaleRunStartedMarkerIsUnknownEvenThoughResumeTxtLooksLikeAnOrdinaryWait()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        var runs = new List<RunEvidence>
        {
            Evidence("20260920T120000Z", FirstHalfResult(spec), hasResumeFile: true, staleRunStartedMarker: true),
        };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
        Assert.IsFalse(state.IsGreen);
        Assert.AreNotEqual(RowStateKind.WaitingForShutDown, state.Kind);
    }

    // Without the marker, the very same evidence is an ordinary, legitimate wait: the marker
    // (never the mere presence of resume.txt) is what makes the difference.
    [TestMethod]
    public void TheSameEvidenceWithoutTheMarkerIsAnOrdinaryWait()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        var runs = new List<RunEvidence>
        {
            Evidence("20260920T120000Z", FirstHalfResult(spec), hasResumeFile: true, staleRunStartedMarker: false),
        };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.WaitingForShutDown, state.Kind);
    }

    [TestMethod]
    public void AStaleRunStartedMarkerOutranksAnOlderGenuinePassOnRecord()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        var olderPass = new ParsedResult
        {
            Test = spec.TestId,
            Overall = "pass",
            Criteria = new[] { new CriterionRecord(spec.SecondHalfOnlyCriteriaIds[0], "criterion text", "pass", string.Empty) },
            Findings = Array.Empty<FindingRecord>(),
            StepCount = 1,
        };
        var runs = new List<RunEvidence>
        {
            Evidence("20260920T120000Z", FirstHalfResult(spec), hasResumeFile: true, staleRunStartedMarker: true),
            Evidence("20260919T090000Z", olderPass, hasResumeFile: false, staleRunStartedMarker: false),
        };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
    }
}
