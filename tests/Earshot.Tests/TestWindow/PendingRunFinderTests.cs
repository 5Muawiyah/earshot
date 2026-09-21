using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// test-gui.md section 9.2: "A run is pending when its folder holds resume.txt, its result.json is
// a first-half result by the markers, and there is no gui-set-aside.txt." Every case writes real
// files to a scratch live-test root and reads them back through EvidenceStore/PendingRunFinder,
// the same disk-only path the window uses.
[TestClass]
public sealed class PendingRunFinderTests
{
    private string _liveTestRoot = null!;

    [TestInitialize]
    public void CreateScratchLiveTestRoot()
    {
        _liveTestRoot = Path.Combine(Path.GetTempPath(), "earshot-pendingrun-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_liveTestRoot);
    }

    [TestCleanup]
    public void DeleteScratchLiveTestRoot()
    {
        if (Directory.Exists(_liveTestRoot))
        {
            Directory.Delete(_liveTestRoot, recursive: true);
        }
    }

    private string TestFolder(string stamp, string testId)
    {
        string folder = Path.Combine(_liveTestRoot, stamp, testId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [TestMethod]
    public void AFirstHalfResultWithResumeTxtAndNoSetAsideIsPending()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion("default-config", "pass")
            .WithCriterion("blocked-before-power-cycle", "pass")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder");

        PendingRun? pending = PendingRunFinder.Find(spec, _liveTestRoot);

        Assert.IsNotNull(pending);
        Assert.AreEqual(spec.TestId, pending!.TestId);
        Assert.AreEqual(folder, pending.Folder);
        Assert.AreEqual("20260920T120000Z", pending.Stamp);
    }

    [TestMethod]
    public void ASetAsideFirstHalfIsNotPending()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion("default-config", "pass")
            .WithCriterion("blocked-before-power-cycle", "pass")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder");
        File.WriteAllText(Path.Combine(folder, "gui-set-aside.txt"), "set aside");

        Assert.IsNull(PendingRunFinder.Find(spec, _liveTestRoot));
    }

    [TestMethod]
    public void AFirstHalfWithNoResumeTxtIsNotPending()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion("default-config", "pass")
            .WithCriterion("blocked-before-power-cycle", "pass")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);

        Assert.IsNull(PendingRunFinder.Find(spec, _liveTestRoot));
    }

    [TestMethod]
    public void ASecondHalfResultIsNotPendingEvenWithAResumeTxtLeftBeside()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "pass").WithCriterion("ACCEPTANCE", "pass").Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder");

        Assert.IsNull(PendingRunFinder.Find(spec, _liveTestRoot));
    }

    [TestMethod]
    public void NoRunFolderAtAllIsNotPending()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        Assert.IsNull(PendingRunFinder.Find(spec, _liveTestRoot));
    }

    [TestMethod]
    public void TheNewestPendingStampWinsWhenMoreThanOneQualifies()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion("default-config", "pass")
            .WithCriterion("blocked-before-power-cycle", "pass")
            .Build();

        string older = TestFolder("20260918T090000Z", spec.TestId);
        ResultJsonFixture.WriteTo(Path.Combine(older, "result.json"), json);
        File.WriteAllText(Path.Combine(older, "resume.txt"), "placeholder");

        string newer = TestFolder("20260920T120000Z", spec.TestId);
        ResultJsonFixture.WriteTo(Path.Combine(newer, "result.json"), json);
        File.WriteAllText(Path.Combine(newer, "resume.txt"), "placeholder");

        PendingRun? pending = PendingRunFinder.Find(spec, _liveTestRoot);

        Assert.IsNotNull(pending);
        Assert.AreEqual("20260920T120000Z", pending!.Stamp);
    }

    // M1: a killed second half leaves resume.txt from the first half still on disk and
    // result.json still holding the first half's own (stale) data, since the script never reached
    // Complete-LiveTestRun to overwrite it. Before this fix, PendingRunFinder had no idea the
    // second half had ever been attempted and killed, so it read exactly like an untouched
    // first-half pass and offered "Carry on with the second half" again over a row StateDeriver
    // itself (the killed-marker check in Derive) already reads as Unknown.
    [TestMethod]
    public void AKilledSecondHalfIsNotPendingEvenThoughTheStaleFirstHalfResultStillReadsAsOne()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion("default-config", "pass")
            .WithCriterion("blocked-before-power-cycle", "pass")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder");
        File.WriteAllText(Path.Combine(folder, "gui-killed.txt"), "2026-09-20T12:05:00.000Z");

        Assert.IsNull(PendingRunFinder.Find(spec, _liveTestRoot),
            "a killed second half must never be offered as 'Carry on' again.");
    }

    [TestMethod]
    public void Test10sFirstHalfFindingOnlyMarkerIsPendingToo()
    {
        TestRowSpec spec = TestRowSpecFixtures.Test10Variant1();
        string folder = TestFolder("20260920T120000Z", spec.TestId);
        string json = new ResultJsonFixture(spec.TestId, "inconclusive")
            .WithFinding("stateBeforeRestart", "connected")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "placeholder");

        PendingRun? pending = PendingRunFinder.Find(spec, _liveTestRoot);

        Assert.IsNotNull(pending);
        Assert.AreEqual(spec.TestId, pending!.TestId);
    }
}
