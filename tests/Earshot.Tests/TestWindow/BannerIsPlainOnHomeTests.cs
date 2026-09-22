using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The at-rest banner on Home showed Banner.RedMessage/"Left enabled on purpose." directly,
// unconditionally: Banner.cs's own internal, technical text, never gated by "Show technical
// details" and never updated for the plain advice (Copy.AtRestNo) already shown correctly on the
// Result view's own leftAtRest line. Found by looking at a real screenshot of Home with a
// not-at-rest fixture on disk: the reddest, most safety-critical text in the whole window read
// "This PC may be left able to page the AirPods at the next start. Run Restore before you shut
// down." with technical details off, containing two banned words ("PC", "page"). Real MainForm, a
// real fixture on disk, no script or device involved.
[TestClass]
public sealed class BannerIsPlainOnHomeTests
{
    [TestMethod]
    public void TheRedBannerShowsTheCorrectedPlainAdviceWithTechnicalDetailsOff()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = System.IO.Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(
            System.IO.Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            Assert.IsTrue(form.BannerVisibleForTests, "the red banner never showed for this not-at-rest fixture.");
            Assert.IsFalse(form.ShowTechnicalDetailsForTests, "technical details must be off by default for this to prove anything.");

            Assert.AreEqual(
                Copy.AtRestNo, form.BannerTextForTests,
                "the banner must show this window's own plain advice, not Banner.cs's own internal technical message.");
            Assert.IsFalse(form.BannerTextForTests.Contains("PC", StringComparison.Ordinal), "the banner must never say \"PC\".");
            Assert.IsFalse(form.BannerTextForTests.Contains("page", StringComparison.OrdinalIgnoreCase), "the banner must never say \"page\".");
        });
    }

    [TestMethod]
    public void TheAmberBannerIsAlsoPlainWithTechnicalDetailsOff()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = System.IO.Path.Combine(liveTestRoot, "20260921T000000Z", "04-block-and-reboot");
        ResultJsonFixture.WriteTo(
            System.IO.Path.Combine(folder, "result.json"),
            new ResultJsonFixture("04-block-and-reboot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no-on-purpose").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            Assert.IsTrue(form.BannerVisibleForTests, "the amber banner never showed for this on-purpose fixture.");
            Assert.AreNotEqual("Left enabled on purpose.", form.BannerTextForTests,
                "the amber banner must not show Banner.cs's own internal technical caption with technical details off.");
        });
    }

    [TestMethod]
    public void TechnicalDetailsOnStillShowsBannerCsOwnTechnicalMessage()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = System.IO.Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = System.IO.Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(
            System.IO.Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ShowHomeViewForTests();
            form.ClickTechnicalDetailsCheckBoxForTests();
            Assert.IsTrue(form.ShowTechnicalDetailsForTests);
            Assert.AreEqual(Banner.RedMessage, form.BannerTextForTests,
                "with technical details on, the banner's own technical wording must still be available.");
        });
    }
}
