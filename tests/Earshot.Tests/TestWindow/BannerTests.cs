using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// T11 (test-gui.md section 13): each leftAtRest value, a missing finding, and an orphan folder
// give the banner and the row gating design.md section 4.6 describes.
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

    [TestMethod]
    public void AnOlderOrphanDoesNotOverrideANewerGoodResult()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(Path.Combine(root.Path, "20260919T000000Z", "02-disconnect"));
        WriteResult(root, "20260920T000000Z", "01-a2dp-oneshot", "yes");

        BannerState banner = Banner.Compute(root.Path);
        Assert.AreEqual(BannerLevel.None, banner.Level);
    }

    // T11 also pins the leftAtRest copy itself (design.md section 12; the coordinator's own
    // strengthening for "unknown"): "no" and "unknown" must each say plainly that the machine is
    // left in the state Earshot exists to prevent, and "unknown" must never read as "yes".
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
