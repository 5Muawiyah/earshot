using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Two ways the banner used to go silent with the nodes still enabled:
//
//   A killed run's folder was ordered by its stale first-half result's own finishedUtc, not by
//   when it was actually killed: a second half killed well after an older pass elsewhere still
//   read as older than that pass, so the kill never outranked it and the banner never noticed.
//
//   The administrator prompt rehearsal writes a hard-coded leftAtRest of not-applicable (it
//   touches no device node and proves nothing about rest), but Banner.Compute scanned every
//   TestId folder under the live test root with nothing excluding it, so a rehearsal run newer
//   than a real test's not-at-rest or unknown result could outrank and clear it.
[TestClass]
public sealed class BannerKilledRunAndRehearsalTests
{
    [TestMethod]
    public void AKilledSecondHalfOutranksAnOlderPassAndARehearsalBetweenThem()
    {
        using var root = new TempFolder();

        // 09's first half: amber, enabled on purpose, an ordinary and settled result.
        string folder = Path.Combine(root.Path, "20260920T000000Z", "09-shutdown-while-connected");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("09-shutdown-while-connected", "pass")
                .WithCriterion("connected-first", "pass")
                .WithFinding("leftAtRest", "no-on-purpose")
                .WithFinishedUtc("2026-09-20T00:00:00.000Z")
                .Build());

        // The rehearsal, run afterwards: not-applicable, which on its own would clear any banner.
        string rehearsalFolder = Path.Combine(root.Path, "20260920T010000Z", ElevationGate.RehearsalTestId);
        ResultJsonFixture.WriteTo(Path.Combine(rehearsalFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("prompt-appeared", "pass")
                .WithFinding("leftAtRest", "not-applicable")
                .WithFinishedUtc("2026-09-20T01:00:00.000Z")
                .Build());

        // 09's second half, started and killed: the kill marker is the newest thing written
        // anywhere under the live test root, well after the rehearsal, even though the stale
        // first-half result.json still sitting in this same folder carries the oldest
        // finishedUtc of the three.
        File.WriteAllText(Path.Combine(folder, "gui-killed.txt"), DateTimeOffset.UtcNow.ToString("o"));

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreNotEqual(BannerLevel.None, banner.Level,
            "a kill after the rehearsal, on a test left not-at-rest on purpose, must never read as though nothing were wrong.");
    }

    [TestMethod]
    public void ARehearsalNewerThanARealNotAtRestResultNeverClearsTheBanner()
    {
        using var root = new TempFolder();

        string folder = Path.Combine(root.Path, "20260920T000000Z", "09-shutdown-while-connected");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("09-shutdown-while-connected", "fail")
                .WithCriterion("not-paged-at-boot", "fail")
                .WithFinding("leftAtRest", "no")
                .WithFinishedUtc("2026-09-20T00:00:00.000Z")
                .Build());

        string rehearsalFolder = Path.Combine(root.Path, "20260920T010000Z", ElevationGate.RehearsalTestId);
        ResultJsonFixture.WriteTo(Path.Combine(rehearsalFolder, "result.json"),
            new ResultJsonFixture(ElevationGate.RehearsalTestId, "pass")
                .WithCriterion("prompt-appeared", "pass")
                .WithFinding("leftAtRest", "not-applicable")
                .WithFinishedUtc("2026-09-20T01:00:00.000Z")
                .Build());

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreEqual(BannerLevel.Red, banner.Level,
            "the rehearsal touches no device node and must never be able to clear a real test's not-at-rest result.");
    }
}
