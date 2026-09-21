using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Banner.cs's own class comment used to say rows "stay locked ... until a newer Restore run
// records leftAtRest of yes or not-applicable", as though Restore were the only run that could
// clear it. Banner.Compute only ever looks at the newest readable result's own leftAtRest,
// whichever test wrote it: any test that ends at rest clears the red banner, not only Restore.
// The three places a row is force-marked Unknown made the same false claim in the owner's own
// words ("until Restore has run"); Copy.RowUnknownUntilItRunsAgain fixes that too.
[TestClass]
public sealed class AnyTestEndingAtRestClearsTheBannerTests
{
    [TestMethod]
    public void ACleanPassOnADifferentTestThanTheOneThatWasKilledClearsTheRedBanner()
    {
        using var root = new TempFolder();

        // An earlier run of test 04 was killed: red on its own. Banner orders an unreadable or
        // killed folder by the newest real write time of anything in it (never the stale result's
        // own idea of when it finished), so the kill marker's own file time is backdated here to
        // match the fixture's own narrative rather than the moment this test actually runs.
        string killedFolder = Path.Combine(root.Path, "20260920T090000Z", "04-block-and-reboot");
        WriteResult(root, "20260920T090000Z", "04-block-and-reboot", "pass", "yes");
        var killedAtUtc = new DateTime(2026, 9, 20, 9, 5, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(killedFolder, "gui-killed.txt"), "2026-09-20T09:05:00.000Z");
        File.SetLastWriteTimeUtc(Path.Combine(killedFolder, "gui-killed.txt"), killedAtUtc);
        File.SetLastWriteTimeUtc(Path.Combine(killedFolder, "result.json"), killedAtUtc);
        Assert.AreEqual(BannerLevel.Red, Banner.Compute(root.Path).Level, "Fixture sanity: the kill alone must show red.");

        // A newer, clean run of a different test (never Restore, "00") ends at rest.
        WriteResult(root, "20260920T100000Z", "01-a2dp-oneshot", "pass", "yes");

        Assert.AreEqual(BannerLevel.None, Banner.Compute(root.Path).Level,
            "Any test that ends at rest must clear the banner, not only a run of Restore (00).");
    }

    [TestMethod]
    public void RowUnknownUntilItRunsAgainNeverClaimsOnlyRestoreClearsIt()
    {
        Assert.IsFalse(Copy.RowUnknownUntilItRunsAgain.Contains("until Restore has run", StringComparison.Ordinal),
            "The row clears once its own test runs again, not only once Restore runs.");
        StringAssert.Contains(Copy.RowUnknownUntilItRunsAgain, "runs again");
    }

    private static void WriteResult(TempFolder root, string stamp, string testId, string overall, string leftAtRest)
    {
        string folder = Path.Combine(root.Path, stamp, testId);
        string json = new ResultJsonFixture(testId, overall).WithCriterion("c1", overall).WithFinding("leftAtRest", leftAtRest).Build();
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);
    }
}
