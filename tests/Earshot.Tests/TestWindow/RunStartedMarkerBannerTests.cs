using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A second half that dies together with the window leaves gui-run-started.txt behind with no
// gui-killed.txt: Banner.Compute must treat that exactly like a kill (AKilledRunShowsTheRedBanner...
// in BannerTests.cs), never trust the stale first-half result underneath it, and never mistake the
// window's own currently active run for a stale one.
[TestClass]
public sealed class RunStartedMarkerBannerTests
{
    [TestMethod]
    public void AStaleRunStartedMarkerShowsTheRedBannerEvenThoughItsStaleResultSaysYes()
    {
        using var root = new TempFolder();
        WriteFirstHalfResult(root, "20260920T000000Z", "04-block-and-reboot", "yes");
        File.WriteAllText(Path.Combine(root.Path, "20260920T000000Z", "04-block-and-reboot", "gui-run-started.txt"),
            "2026-09-20T00:05:00.000Z");

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreEqual(BannerLevel.Red, banner.Level,
            "A stale run-started marker (no gui-killed.txt, the window died with its child) must never let a stale result speak for it.");
    }

    // The window's own currently active run is exempted: its result folder is passed as
    // activeFolder, so its own (freshly written, entirely expected) marker is never misread as
    // abandoned.
    [TestMethod]
    public void TheCurrentlyActiveRunsOwnMarkerIsNeverTreatedAsStale()
    {
        using var root = new TempFolder();
        WriteFirstHalfResult(root, "20260920T000000Z", "04-block-and-reboot", "yes");
        string activeFolder = Path.Combine(root.Path, "20260920T000000Z", "04-block-and-reboot");
        File.WriteAllText(Path.Combine(activeFolder, "gui-run-started.txt"), "2026-09-20T00:05:00.000Z");

        BannerState banner = Banner.Compute(root.Path, activeFolder);

        Assert.AreEqual(BannerLevel.None, banner.Level,
            "The window's own in-flight run must not lock itself out just for being mid-run.");
    }

    private static void WriteFirstHalfResult(TempFolder root, string stamp, string testId, string leftAtRest)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        string json = new ResultJsonFixture(testId, "pass")
            .WithCriterion("block", "pass")
            .WithFinding("leftAtRest", leftAtRest)
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
        File.WriteAllText(Path.Combine(folder, "resume.txt"), "resume");
    }
}
