using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// PendingRunFinder already refuses to offer "Carry on" over a folder holding gui-killed.txt
// (its own first-half result is stale, since the killed script never reached
// Complete-LiveTestRun). A stale gui-run-started.txt (the window died with its child, so nothing
// ever wrote gui-killed.txt) must refuse the very same way, never offering to resume a run this
// window never actually saw end.
[TestClass]
public sealed class RunStartedMarkerPendingRunTests
{
    [TestMethod]
    public void AStaleRunStartedMarkerIsNeverOfferedAsAPendingRun()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = Path.Combine(root.Path, "20260920T000000Z", spec.TestId);
        WriteFirstHalfResult(folder, spec);
        File.WriteAllText(Path.Combine(folder, "gui-run-started.txt"), "2026-09-20T00:05:00.000Z");

        PendingRun? pending = PendingRunFinder.Find(spec, root.Path);

        Assert.IsNull(pending, "A folder abandoned mid-run (window and child both died) must never be offered as a pending second half.");
    }

    // The window's own currently active run is exempted by folder, the same as Banner.Compute:
    // its own marker, freshly written for the run in progress, must never make PendingRunFinder
    // refuse to find it.
    [TestMethod]
    public void TheCurrentlyActiveRunsOwnMarkerDoesNotBlockItFromBeingFound()
    {
        using var root = new TempFolder();
        TestRowSpec spec = TestRowSpecFixtures.Test08();
        string folder = Path.Combine(root.Path, "20260920T000000Z", spec.TestId);
        WriteFirstHalfResult(folder, spec);
        File.WriteAllText(Path.Combine(folder, "gui-run-started.txt"), "2026-09-20T00:05:00.000Z");

        PendingRun? pending = PendingRunFinder.Find(spec, root.Path, folder);

        Assert.IsNotNull(pending);
    }

    private static void WriteFirstHalfResult(string folder, TestRowSpec spec)
    {
        string json = new ResultJsonFixture(spec.TestId, "pass")
            .WithCriterion(spec.FirstHalfOnlyCriteriaIds[0], "pass")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "resume");
    }
}
