using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Each leftAtRest value, a missing finding, and an orphan folder give the banner and the row
// gating Banner.Compute describes.
[TestClass]
public sealed class BannerTests
{
    [TestMethod]
    public void NoEvidenceAtAllShowsNoBanner()
    {
        using var root = new TempFolder();
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, banner.Level);
        Assert.IsFalse(banner.RowsLockedExceptRestore);
    }

    [TestMethod]
    public void LeftAtRestYesShowsNoBanner()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "yes");
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, banner.Level);
    }

    [TestMethod]
    public void LeftAtRestNotApplicableShowsNoBanner()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "not-applicable");
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, banner.Level);
    }

    [TestMethod]
    public void LeftAtRestNoOnPurposeIsAmberAndBlocksNothing()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "09-shutdown-while-connected", "no-on-purpose");
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Amber, banner.Level);
        Assert.IsFalse(banner.RowsLockedExceptRestore);
    }

    [TestMethod]
    public void LeftAtRestNoIsRedAndLocksRows()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "no");
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, banner.Level);
        Assert.IsTrue(banner.RowsLockedExceptRestore);
    }

    [TestMethod]
    public void LeftAtRestUnknownIsRed()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "unknown");
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, banner.Level);
    }

    [TestMethod]
    public void NoLeftAtRestFindingAtAllIsRed()
    {
        using var root = new TempFolder();
        string folder = Path.Combine(root.Path, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").Build());
        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, banner.Level);
    }

    [TestMethod]
    public void AnOrphanFolderNewerThanTheNewestResultIsRed()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260919T000000Z", "01-a2dp-oneshot", "yes");

        // A newer stamp folder exists but never wrote a result.json: a half that started and
        // never finished, or a window that was closed before the child answered.
        Directory.CreateDirectory(Path.Combine(root.Path, "20260920T000000Z", "02-disconnect"));

        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, banner.Level, "A newer run with no result.json must not be shadowed by an older good one.");
    }

    // M1: a killed second half leaves the first half's own stale result.json behind, unrewritten,
    // since the script never reached Complete-LiveTestRun. That stale record can carry any
    // leftAtRest value the first half itself recorded (here, "yes", as a two-half test whose first
    // half already left the nodes blocked correctly would); before this fix, Banner.Compute had no
    // idea a kill had happened and read that stale "yes" as though it settled the question,
    // showing no banner at all over a machine no run has actually finished putting at rest.
    [TestMethod]
    public void AKilledRunShowsTheRedBannerEvenThoughItsStaleResultSaysYes()
    {
        using var root = new TempFolder();
        WriteResult(root, "20260920T000000Z", "04-block-and-reboot", "yes");
        File.WriteAllText(Path.Combine(root.Path, "20260920T000000Z", "04-block-and-reboot", "gui-killed.txt"),
            "2026-09-20T00:05:00.000Z");

        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.Red, banner.Level,
            "A killed run's stale result must never speak for it; the banner must not trust it.");
    }

    [TestMethod]
    public void AnOlderOrphanDoesNotOverrideANewerGoodResult()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(Path.Combine(root.Path, "20260919T000000Z", "02-disconnect"));
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "yes");

        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, banner.Level);
    }

    // Also pins the leftAtRest copy itself: "no" and "unknown" must each say plainly that the
    // machine is left in the state Earshot exists to prevent, and "unknown" must never read as
    // "yes".
    [TestMethod]
    public void NoAndUnknownEachSayTheMachineIsLeftInTheStateEarshotExistsToPrevent()
    {
        StringAssert.Contains(Copy.LeftAtRestText("no", null), "state Earshot");
        StringAssert.Contains(Copy.LeftAtRestText("no", null), "exists to prevent");
        StringAssert.Contains(Copy.LeftAtRestText("unknown", null), "state Earshot");
        StringAssert.Contains(Copy.LeftAtRestText("unknown", null), "exists to prevent");
    }

    [TestMethod]
    public void UnknownIsNeverShownAsYes()
    {
        string unknownText = Copy.LeftAtRestText("unknown", null);
        string yesText = Copy.LeftAtRestText("yes", null);
        Assert.AreNotEqual(yesText, unknownText);
        Assert.IsFalse(unknownText.Contains("nodes are blocked", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingFindingIsNeverShownAsYes()
    {
        string missingText = Copy.LeftAtRestText(null, null);
        string yesText = Copy.LeftAtRestText("yes", null);
        Assert.AreNotEqual(yesText, missingText);
    }

    private static void WriteResult(TempFolder root, string stamp, string testId, string leftAtRest)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        string json = new ResultJsonFixture(testId, "pass")
            .WithCriterion("c1", "pass")
            .WithFinding("leftAtRest", leftAtRest)
            .Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
    }
}
