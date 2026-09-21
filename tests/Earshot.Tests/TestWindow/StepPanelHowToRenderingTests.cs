using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// StepPanel renders a prompt's how-to blocks as a numbered list, in order, with the first named
// picture shown beside it; never hidden by the technical-details toggle, since these are this
// window's own plain steps, never the script's own words. Real MainForm, a real sandboxed
// 01-A2dpOneShot.ps1: test 01's own physical action ("Wear the AirPods and play something from
// your phone...") names airpods-in-and-phone-playing, the one this test proves against.
[TestClass]
public sealed class StepPanelHowToRenderingTests
{
    [TestMethod]
    public void Test01sPhysicalActionShowsItsHowToStepsInOrderAndItsPicture()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();

            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");

            StepPanel panel = form.StepPanelForTests;
            Assert.IsTrue(panel.HowToStepsVisibleForTests, "the how-to steps were not shown for test 01's preconditions screen.");
            StringAssert.Contains(panel.HowToStepsTextForTests, "1. Put the AirPods in your ears.");
            StringAssert.Contains(panel.HowToStepsTextForTests, "2. Play something on your phone, such as music or a video.");

            Assert.IsTrue(panel.HowToPictureVisibleForTests, "no picture was shown for airpods-in-and-phone-playing.");
            Assert.IsTrue(panel.HowToPictureLoadedForTests);
        });
    }

    [TestMethod]
    public void HowToStepsStayVisibleWhetherTechnicalDetailsIsOnOrOff()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));

            Assert.IsTrue(form.StepPanelForTests.HowToStepsVisibleForTests, "hidden with technical details off.");

            form.ClickTechnicalDetailsCheckBoxForTests();

            Assert.IsTrue(form.StepPanelForTests.HowToStepsVisibleForTests, "hidden with technical details on.");
        });
    }
}
