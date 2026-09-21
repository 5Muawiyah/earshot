using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A kill leaves Unknown and the banner, never a pass. This
// class fails when the rule at the top of StateDeriver.Derive checking
// runsNewestFirst[0].HasKilledMarker is deleted, because an older genuine pass would otherwise
// keep showing through a kill that may have left the real device mid-way through a live step.
[TestClass]
public sealed class KilledMarkerTests
{
    private static RunEvidence Evidence(string stamp, bool killed, ParsedResult? result, string? readFailureReason = null) => new()
    {
        Stamp = stamp,
        Folder = @"C:\nowhere\" + stamp,
        Result = result,
        ReadFailureReason = readFailureReason,
        HasKilledMarker = killed,
    };

    private static ParsedResult Pass(string testId) => new()
    {
        Test = testId,
        Overall = "pass",
        Criteria = new[] { new CriterionRecord("only", "criterion text", "pass", string.Empty) },
        Findings = Array.Empty<FindingRecord>(),
        StepCount = 1,
    };

    [TestMethod]
    public void ANewestKilledRunIsUnknownEvenWithAnOlderGenuinePassOnRecord()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        var runs = new List<RunEvidence>
        {
            Evidence("20260920T120000Z", killed: true, result: null, readFailureReason: "no result.json"),
            Evidence("20260919T090000Z", killed: false, result: Pass(spec.TestId)),
        };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
        Assert.IsFalse(state.IsGreen);
    }

    [TestMethod]
    public void AnOlderKilledRunThatANewerCleanPassSupersedesIsJustHistory()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        var runs = new List<RunEvidence>
        {
            Evidence("20260920T120000Z", killed: false, result: Pass(spec.TestId)),
            Evidence("20260919T090000Z", killed: true, result: null, readFailureReason: "no result.json"),
        };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsTrue(state.IsGreen);
    }

    [TestMethod]
    public void ANewestKilledRunIsUnknownEvenWithNothingOlderAtAll()
    {
        TestRowSpec spec = TestRowSpecFixtures.OneHalf();
        var runs = new List<RunEvidence> { Evidence("20260920T120000Z", killed: true, result: null, readFailureReason: "no result.json") };

        DerivedRowState state = StateDeriver.Derive(spec, runs, null, null);

        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
    }
}
