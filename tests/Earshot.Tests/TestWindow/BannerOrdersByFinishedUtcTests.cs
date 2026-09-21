using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// B3 (review round 1): Banner.Compute used to pick "newest" by the run folder's own stamp, not
// by when the result was actually written. A resumed second half writes into its first half's
// (older) stamp folder, so a genuinely more recent no/unknown there could hide behind an
// unrelated test's newer-stamped but chronologically earlier pass.
[TestClass]
public sealed class BannerOrdersByFinishedUtcTests
{
    [TestMethod]
    public void AResumedSecondHalfsNotAtRestOutranksAnOlderStampedButLaterFinishedPass()
    {
        using var root = new TempFolder();

        // 04's second half writes into its own first half's stamp, 19 September, but actually
        // finishes on 21 September: a real power-cycle test resumed two days later.
        string secondHalfFolder = Path.Combine(root.Path, "20260919T000000Z", "04-block-and-reboot");
        string secondHalfJson = new ResultJsonFixture("04-block-and-reboot", "pass")
            .WithCriterion("persisted", "pass")
            .WithFinding("leftAtRest", "no")
            .WithFinishedUtc("2026-09-21T09:00:00.000Z")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(secondHalfFolder, "result.json"), secondHalfJson);

        // An unrelated test's fresh, clean pass, stamped later in folder-creation order but
        // actually finished a full day earlier.
        string otherFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        string otherJson = new ResultJsonFixture("01-a2dp-oneshot", "pass")
            .WithCriterion("c1", "pass")
            .WithFinding("leftAtRest", "yes")
            .WithFinishedUtc("2026-09-20T09:00:00.000Z")
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(otherFolder, "result.json"), otherJson);

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreEqual(BannerLevel.Red, banner.Level,
            "the resumed second half finished later (21 Sept) than the other test's stamp-newer pass (20 Sept, finished 20 Sept), so it must be the one the banner reads.");
    }

    [TestMethod]
    public void AnUnreadableRunNewerByFinishedUtcThanTheChosenOneStillShowsTheBanner()
    {
        using var root = new TempFolder();

        string goodFolder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(goodFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-20T09:00:00.000Z").Build());

        // A folder stamped earlier, but which never got a readable result.json: still "newer"
        // than nothing at all, and ordered by its own stamp since it has no finishedUtc.
        Directory.CreateDirectory(Path.Combine(root.Path, "20260921T000000Z", "02-disconnect"));

        BannerState banner = Banner.Compute(root.Path);

        Assert.AreEqual(BannerLevel.Red, banner.Level);
    }
}
