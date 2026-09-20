using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T1 (test-gui.md section 13): the fixture matrix over real temporary folders. Each test method
// here is named for the row in that list it proves. StateDeriver.Derive is exercised through
// EvidenceStore, exactly the pipeline the window itself uses, never by constructing a
// DerivedRowState directly.
[TestClass]
public sealed class StateDeriverTests
{
    private static IReadOnlyList<RunEvidence> Load(string root, string testId) => EvidenceStore.LoadEvidence(root, testId);

    [TestMethod]
    public void NoFolderAtAllIsNotRun()
    {
        using var root = new TempFolder();
        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.NotRun, state.Kind);
    }

    [TestMethod]
    public void EmptyRunFolderIsUnknown()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot"));
        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.Unknown, state.Kind);
        StringAssert.Contains(state.Reason, "no result.json");
    }

    [TestMethod]
    public void AGenuinePassIsGreen()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsTrue(state.IsGreen);
    }

    [TestMethod]
    public void AStoppedEarlyCriterionFails()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "fail").WithCriterion("run", "fail"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
        Assert.AreEqual("stopped early", state.Reason);
    }

    // A 'run' criterion recorded with a passing outcome (not how the shipped scripts write it
    // today, but the shape rule 5 exists to defend against) must still fail the row: this is the
    // one case where rule 5 does something the recompute rule (rule 4) alone would not.
    [TestMethod]
    public void ARunCriterionWithAPassingOutcomeStillFails()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("run", "pass"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
        Assert.AreEqual("stopped early", state.Reason);
    }

    // "First-half pass only (each two-half test)": with resume.txt present, the row waits.
    [TestMethod]
    public void FirstHalfPassWithResumeWaitsForTheRestart()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("block", "pass").WithCriterion("disabled-now", "pass"));
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "powershell -File x -Resume");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreEqual(RowStateKind.WaitingForRestart, state.Kind);
    }

    [TestMethod]
    public void FirstHalfPassOfAFullShutDownTestWaitsForTheShutDown()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "08-acceptance-power-cycle",
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("default-config", "pass").WithCriterion("blocked-before-power-cycle", "pass"));
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "powershell -File x -Resume");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test08(), Load(root.Path, "08-acceptance-power-cycle"), null, null);
        Assert.AreEqual(RowStateKind.WaitingForShutDown, state.Kind);
    }

    // 05's exception: no resume.txt means the owner declined the optional restart.
    [TestMethod]
    public void Test05WithNoResumeIsCompleteWithAQualifier()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "05-allow",
            new ResultJsonFixture("05-allow", "pass").WithCriterion("allow", "pass"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test05(), Load(root.Path, "05-allow"), null, null);
        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsFalse(state.IsGreen, "A first-half-only pass must never render as a clean green pass.");
        StringAssert.Contains(state.Qualifier, "restart half not run");
    }

    [TestMethod]
    public void SecondHalfPassWithFailedSnapshotFails()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("persisted", "pass").WithCriterion("stayed-on-phone", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"),
            new ResultJsonFixture("04-block-and-reboot", "fail").WithCriterion("block", "fail").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreEqual(RowStateKind.Failed, state.Kind);
        StringAssert.Contains(state.Reason, "snapshot did not pass");
    }

    [TestMethod]
    public void SecondHalfPassWithNoSnapshotIsAmber()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("persisted", "pass").WithCriterion("stayed-on-phone", "pass"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsFalse(state.IsGreen);
        StringAssert.Contains(state.Qualifier, "second half only on record");
    }

    [TestMethod]
    public void SecondHalfPassWithAGoodSnapshotIsGreen()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("persisted", "pass").WithCriterion("stayed-on-phone", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"),
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("block", "pass").WithCriterion("disabled-now", "pass").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsTrue(state.IsGreen);
    }

    [TestMethod]
    public void OlderExeIsAnEarlierBuild()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass")
                .WithExe(@"C:\Program Files\Earshot\Earshot.exe").WithStartedUtc("2026-09-19T00:00:00.000Z"));

        DerivedRowState state = StateDeriver.Derive(
            TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"),
            @"C:\Program Files\Earshot\Earshot.exe", DateTimeOffset.Parse("2026-09-20T00:00:00.000Z"));

        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsFalse(state.IsGreen);
        StringAssert.Contains(state.Qualifier, "on an earlier build");
    }

    [TestMethod]
    public void NewerExeAtTheSameStartedTimeIsStillAGreenPass()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass")
                .WithExe(@"C:\Program Files\Earshot\Earshot.exe").WithStartedUtc("2026-09-20T00:00:00.000Z"));

        DerivedRowState state = StateDeriver.Derive(
            TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"),
            @"C:\Program Files\Earshot\Earshot.exe", DateTimeOffset.Parse("2026-09-19T00:00:00.000Z"));

        Assert.IsTrue(state.IsGreen, "An exe last written before the run started is not a newer build.");
    }

    [TestMethod]
    public void OtherExePathIsAnEarlierBuild()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass")
                .WithExe(@"C:\elsewhere\Earshot.exe").WithStartedUtc("2026-09-20T00:00:00.000Z"));

        DerivedRowState state = StateDeriver.Derive(
            TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"),
            @"C:\Program Files\Earshot\Earshot.exe", null);

        Assert.IsFalse(state.IsGreen);
        StringAssert.Contains(state.Qualifier, "on an earlier build");
    }

    [TestMethod]
    [DataRow("restart")]
    [DataRow("unknown")]
    [DataRow("not-yet")]
    public void Test08WithoutAConfirmedPowerDownIsAmber(string verdict)
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "08-acceptance-power-cycle",
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("ACCEPTANCE", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-power-cycle.json"), "{\"verdict\":\"" + verdict + "\"}");

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test08(), Load(root.Path, "08-acceptance-power-cycle"), null, null);
        Assert.AreEqual(RowStateKind.Passed, state.Kind);
        Assert.IsFalse(state.IsGreen);
        StringAssert.Contains(state.Qualifier, "shut down not confirmed");
    }

    [TestMethod]
    public void Test08WithAConfirmedPowerDownIsGreen()
    {
        using var root = new TempFolder();
        string folder = WriteResult(root, "20260920T000000Z", "08-acceptance-power-cycle",
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("ACCEPTANCE", "pass"));
        File.WriteAllText(Path.Combine(folder, "gui-power-cycle.json"), "{\"verdict\":\"power-down\"}");
        File.WriteAllText(Path.Combine(folder, "gui-first-half.result.json"),
            new ResultJsonFixture("08-acceptance-power-cycle", "pass").WithCriterion("default-config", "pass").WithCriterion("blocked-before-power-cycle", "pass").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test08(), Load(root.Path, "08-acceptance-power-cycle"), null, null);
        Assert.IsTrue(state.IsGreen, "Qualifier was: " + state.Qualifier);
    }

    [TestMethod]
    public void DeclinedStartAfterAPassDoesNotReplaceTheVerdict()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass"));
        WriteResult(root, "20260921T000000Z", "01-a2dp-oneshot",
            new ResultJsonFixture("01-a2dp-oneshot", "inconclusive"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.IsTrue(state.IsGreen, "A later declined start must not erase an earlier pass.");
        StringAssert.Contains(state.HistoryNote, "21 September");
        StringAssert.Contains(state.HistoryNote, "stopped before any step");
    }

    [TestMethod]
    public void DeclinedStartOnlyIsStoppedBeforeAnyStep()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", new ResultJsonFixture("01-a2dp-oneshot", "inconclusive"));

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), Load(root.Path, "01-a2dp-oneshot"), null, null);
        Assert.AreEqual(RowStateKind.StoppedBeforeAnyStep, state.Kind);
    }

    [TestMethod]
    public void PendingRunSetAsideIsNotShownAsWaiting()
    {
        using var root = new TempFolder();
        string older = WriteResult(root, "20260919T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("persisted", "pass").WithCriterion("stayed-on-phone", "pass"));
        string newer = WriteResult(root, "20260920T000000Z", "04-block-and-reboot",
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("block", "pass").WithCriterion("disabled-now", "pass"));
        File.WriteAllText(Path.Combine(newer, "resume.txt"), "powershell -File x -Resume");
        File.WriteAllText(Path.Combine(newer, "gui-set-aside.txt"), "set aside");
        File.WriteAllText(Path.Combine(older, "gui-first-half.result.json"),
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("block", "pass").WithCriterion("disabled-now", "pass").Build());

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.Test04(), Load(root.Path, "04-block-and-reboot"), null, null);
        Assert.AreNotEqual(RowStateKind.WaitingForRestart, state.Kind, "A set-aside pending run must not drive the row to Waiting.");
        Assert.IsTrue(state.IsGreen, "The older, still-valid second half underneath the set-aside run must still be shown.");
        StringAssert.Contains(state.HistoryNote, "set aside");
    }

    // The real result.json shapes recorded on this machine (PROMPTING_RESPONSES.md, test 01
    // passed; MEMORY.md, "test 01 passed"). Where the real evidence is not present the fixture
    // proves nothing about this machine, so it is reported as such rather than skipped quietly.
    [TestMethod]
    public void TheRealTest01EvidenceOnThisMachineReadsWithoutError()
    {
        string liveTestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot", "livetest");
        IReadOnlyList<RunEvidence> evidence = Load(liveTestRoot, "01-a2dp-oneshot");
        if (evidence.Count == 0)
        {
            Assert.Inconclusive("No real evidence for 01-a2dp-oneshot was found under " + liveTestRoot + " on this machine.");
        }

        DerivedRowState state = StateDeriver.Derive(TestRowSpecFixtures.OneHalf(), evidence, null, null);
        Assert.AreNotEqual(RowStateKind.Unknown, state.Kind, "The real evidence on this machine could not be read: " + state.Reason);
    }

    private static string WriteResult(TempFolder root, string stamp, string testId, ResultJsonFixture fixture)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), fixture.Build());
        return folder;
    }
}
