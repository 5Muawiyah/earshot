using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The red-banner advice used to change by cause, on the belief that a "no" always meant the
// AirPods were known playing from this computer, safe to advise a click for. LiveTest.psm1's own
// at-rest check writes leftAtRest "no" whenever the nodes read anything but Blocked, which
// includes a declined offer with the AirPods still on the phone: it never reads the actual
// Bluetooth connection, so a "no" can never be trusted that way. Every red cause now shows the
// exact same text, on Home, in Run all's own recovery step, and on the Result view, and it never
// advises a click at all.
[TestClass]
public sealed class AtRestAdviceByCauseTests
{
    [TestMethod]
    public void ATrustedNoCauseAndAnUnreadableCauseShowTheExactSameTextOnHomeAndInRunAllsRecoveryStep()
    {
        using var sandboxNo = new TempFolder();
        string liveTestRootNo = Path.Combine(sandboxNo.Path, "local", "Earshot", "livetest");
        string noFolder = Path.Combine(liveTestRootNo, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(noFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        string? noBannerText = null;
        string? noAdviceText = null;
        MainFormTestHarness.Run(sandboxNo.Path, form =>
        {
            form.ShowHomeViewForTests();
            noBannerText = form.BannerTextForTests;
            form.ClickRunAllForTests();
            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests);
            noAdviceText = form.RunAllRestoreAdviceTextForTests;
        });

        using var sandboxUnreadable = new TempFolder();
        string liveTestRootUnreadable = Path.Combine(sandboxUnreadable.Path, "local", "Earshot", "livetest");
        // An older clean pass, then a newer run folder that never wrote a result.json at all (a
        // half that started and never finished): a different red cause entirely.
        string olderFolder = Path.Combine(liveTestRootUnreadable, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "yes").Build());
        Directory.CreateDirectory(Path.Combine(liveTestRootUnreadable, "20260921T000000Z", "02-disconnect"));

        MainFormTestHarness.Run(sandboxUnreadable.Path, form =>
        {
            form.ShowHomeViewForTests();
            Assert.AreEqual(noBannerText, form.BannerTextForTests,
                "a trusted-looking \"no\" and an unreadable cause must show the exact same banner text: there is no cause a \"no\" can be trusted for.");

            form.ClickRunAllForTests();
            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests);
            StringAssert.Contains(form.RunAllRestoreAdviceTextForTests, noAdviceText!);
        });
    }

    [TestMethod]
    public void TheAdviceNeverContainsTheWordClick()
    {
        Assert.IsFalse(Copy.AtRestNo.Contains("click", StringComparison.OrdinalIgnoreCase),
            "the red-banner advice must never advise a click: a left click connects the AirPods to this computer, exactly wrong whenever they are already on the phone.");
    }

    [TestMethod]
    public void TheAdviceNamesRestoreByRowAndNeverPromisesAClickAloneClearsTheWarning()
    {
        StringAssert.Contains(Copy.AtRestNo, "case");
        StringAssert.Contains(Copy.AtRestNo, "Restore (row 00)");
        Assert.IsFalse(Copy.AtRestNo.Contains("if the warning is still here", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(Copy.AtRestNo, "The warning clears when a test ends with this computer leaving your AirPods alone.");
    }

    // The Result view's own leftAtRest line for a row's own "no" uses the exact same text.
    [TestMethod]
    public void TheResultViewsOwnNoLineUsesTheSameSingleText()
    {
        Assert.AreEqual(Copy.AtRestNo, Copy.LeftAtRestText("no", null));
    }
}
