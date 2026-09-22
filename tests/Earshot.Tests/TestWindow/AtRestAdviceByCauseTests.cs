using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The red-banner advice used to be the same words regardless of cause: "click the Earshot icon
// once so your AirPods go back to your phone." That is only ever right when the AirPods are
// actually known connected to this computer (a trusted "no"): for every other Red cause (unknown,
// unreadable, or a kill), nothing here has read where the AirPods are at all, and a left click
// would CONNECT them to this computer instead of sending them back to the phone. Shown on Home,
// in Run all's own recovery step, and on the Result view (the last of those already only ever
// reaches this text for a trusted "no", since a per-row "no" is never recorded without one).
[TestClass]
public sealed class AtRestAdviceByCauseTests
{
    [TestMethod]
    public void ATrustedNoShowsTheClickAdviceOnHomeAndInRunAllsRecoveryStep()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            Assert.AreEqual(Copy.AtRestNo, form.BannerTextForTests);
            StringAssert.Contains(form.BannerTextForTests, "playing from this computer");
            StringAssert.Contains(form.BannerTextForTests, "Click the Earshot icon once");
            Assert.IsFalse(form.BannerTextForTests.Contains("Do not click", StringComparison.Ordinal));

            form.ClickRunAllForTests();
            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests);
            StringAssert.Contains(form.RunAllRestoreAdviceTextForTests, Copy.AtRestNo);
        });
    }

    [TestMethod]
    public void AnUnreadableCauseShowsTheDoNotClickAdviceOnHomeAndInRunAllsRecoveryStep()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // An older clean pass, then a newer run folder that never wrote a result.json at all (a
        // half that started and never finished): Banner.Compute reads this as an unreadable newer
        // folder, Red, cause UnknownOrUnreadable, never a trusted "no".
        string olderFolder = Path.Combine(liveTestRoot, "20260920T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(olderFolder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "yes").Build());
        Directory.CreateDirectory(Path.Combine(liveTestRoot, "20260921T000000Z", "02-disconnect"));

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            Assert.AreEqual(Copy.AtRestNoUnknownCause, form.BannerTextForTests);
            StringAssert.Contains(form.BannerTextForTests, "Put your AirPods in their case");
            StringAssert.Contains(form.BannerTextForTests, "Do not click the Earshot icon");
            Assert.IsFalse(form.BannerTextForTests.Contains("Click the Earshot icon once so they go back", StringComparison.Ordinal));

            form.ClickRunAllForTests();
            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests);
            StringAssert.Contains(form.RunAllRestoreAdviceTextForTests, Copy.AtRestNoUnknownCause);
        });
    }

    // Both texts say the same thing about how the warning actually clears, and neither promises a
    // click alone does it (nothing here ever re-reads the device after one, so it never could).
    [TestMethod]
    public void NeitherTextPromisesAClickAloneClearsTheWarning()
    {
        Assert.IsFalse(Copy.AtRestNo.Contains("if the warning is still here", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Copy.AtRestNoUnknownCause.Contains("if the warning is still here", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(Copy.AtRestNo, "The warning clears when a test ends with this computer leaving your AirPods alone.");
        StringAssert.Contains(Copy.AtRestNoUnknownCause, "The warning clears when a test ends with this computer leaving your AirPods alone.");
    }

    // The Result view's own leftAtRest line for a row's own "no" always uses the trusted-cause
    // text: a per-row "no" is only ever recorded once a concrete node state is read, so there is
    // no "unknown cause" version of it to pick between.
    [TestMethod]
    public void TheResultViewsOwnNoLineIsAlwaysTheTrustedCauseText()
    {
        Assert.AreEqual(Copy.AtRestNo, Copy.LeftAtRestText("no", null));
    }
}
