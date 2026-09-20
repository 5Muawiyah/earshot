using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T2 (test-gui.md section 13): one test per rule in section 6.2, each named for the rule it
// pins. Every one of these has been run once against a build with its own rule deleted, and gone
// red; the run and the exact output are recorded in the handover, not repeated here as a comment,
// because a comment cannot be executed. Restoring the rule turns the same test green again.
[TestClass]
public sealed class StateDeriverPinTests
{
    private static IReadOnlyList<RunEvidence> Load(string root, string testId) => EvidenceStore.LoadEvidence(root, testId);

    private static string WriteResult(TempFolder root, string stamp, string testId, ResultJsonFixture fixture)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), fixture.Build());
        return folder;
    }

    // Pin: rule 4, "recompute overall from the criteria... If the recomputed value differs from
    // overall, the state is Unknown." Deleting the recompute-and-compare check would let a
    // corrupted overall of "pass" through with a failed criterion underneath it.
    [TestMethod]
    public void PinRecomputedOverallMustAgreeWithTheStatedOverall()
    {
        using var folder = new TempFolder();
        string json = new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "fail").Build();
        ResultJsonFixture.WriteTo(folder.File("result.json"), json);

        (ParsedResult? result, string? reason) = EvidenceStore.TryReadResult(folder.File("result.json"), "01-a2dp-oneshot");
        Assert.IsNull(result, "A result.json whose stated overall disagrees with its own criteria must never parse as valid.");
        StringAssert.Contains(reason, "disagrees with itself");
    }

    // Pin: rule 5, "A criterion with id 'run' means the script stopped early: Failed." Written
    // with a passing outcome on the 'run' criterion so this pin cannot be satisfied by rule 4's
    // recompute check alone (see StateDeriverTests.ARunCriterionWithAPassingOutcomeStillFails).
    [TestMethod]
    public void PinARunCriterionAlwaysFails()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("run", "pass"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
        Assert.AreEqual("stopped early", state.Reason);
    }

    // Pin: "First half only: a first-half result.json, whatever it says, is never the test's
    // pass." A first-half pass alone must never be green.
    [TestMethod]
    public void PinAFirstHalfAloneIsNeverGreen()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("block", "pass").WithCriterion("disabled-now", "pass"));
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "powershell -File x -Resume");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreNotEqual(RowStateKind.Passed, state.Kind);
        Assert.IsFalse(state.IsGreen);
    }

    // Pin: "Two halves: Passed needs the second half's pass... Snapshot failed: the row is
    // Failed." Removing the snapshot check would let a second half pass on a build whose first
    // half, taken from the snapshot, actually failed.
    [TestMethod]
    public void PinASecondHalfPassNeverSurvivesAFailedFirstHalfSnapshot()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("persisted", "pass").WithCriterion("stayed-on-phone", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"),
            new ResultJsonFixture("04-block-and-reboot", "fail").WithCriterion("block", "fail").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
    }

    // Pin: "Earlier build: exe differs from the chosen exe path, or the exe's last write time is
    // later than startedUtc: 'Passed on an earlier build', amber."
    [TestMethod]
    public void PinAPassOnAnOlderExeIsNeverGreen()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass")
                .WithExe(@"C:\Program Files\Earshot\Earshot.exe").WithStartedUtc("2026-09-19T00:00:00.000Z"));

        DerivedRowState state = StateDeriver.Derive(
            TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"),
            @"C:\Program Files\Earshot\Earshot.exe", DateTimeOffset.Parse("2026-09-20T00:00:00.000Z"));

        Assert.IsFalse(state.IsGreen);
    }

    // Pin: "08 and 09: the power-cycle verdict for that run is not power-down: 'shut down not
    // confirmed', amber."
    [TestMethod]
    public void PinTest08NeverGoesGreenWithoutAConfirmedPowerDown()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "08-acceptance-power-cycle",
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("ACCEPTANCE", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"),
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("default-config", "pass").WithCriterion("blocked-before-power-cycle", "pass").Build());
        File.WriteAllText(Path.Combine(folder, "gui-power-cycle.json"), "{\"verdict\":\"restart\"}");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test08(), Load(root.Path, "08-acceptance-power-cycle"), null, null);
        Assert.IsFalse(state.IsGreen);
    }

    // Pin: "Only Passed is green... the word 'Passed' on its own appears for nothing else."
    [TestMethod]
    public void PinOnlyAnUnqualifiedPassedKindIsGreen()
    {
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            var state = new DerivedRowState { Kind = kind };
            bool shouldBeGreen = kind == RowStateKind.Passed;
            Assert.AreEqual(shouldBeGreen, state.IsGreen, kind + " must be green only when it is an unqualified Passed.");
        }

        var qualifiedPass = new DerivedRowState { Kind = RowStateKind.Passed, Qualifier = "on an earlier build" };
        Assert.IsFalse(qualifiedPass.IsGreen, "A qualified pass must never be green.");
    }
}
