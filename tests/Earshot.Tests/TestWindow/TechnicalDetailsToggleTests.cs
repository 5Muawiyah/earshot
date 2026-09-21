using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// "Show technical details" is off by default, remembered across opens, and gates every
// script-authored string this window can show: StepPanel's raw prompt text and raw
// preconditions/actions, ResultPanel's failure table, and the row detail's Title/Settles lines.
// Real MainForm, a real sandboxed 01-A2dpOneShot.ps1, real clicks: proves the caller (StepPanel and
// MainForm's own toggle handler), not a presenter fact in isolation.
[TestClass]
public sealed class TechnicalDetailsToggleTests
{
    [TestMethod]
    public void TechnicalDetailsStartsOffAndHidesTheRawPreconditionsAndScriptWords()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsFalse(form.ShowTechnicalDetailsForTests, "off by default");

            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");

            StepPanel panel = form.StepPanelForTests;
            Assert.IsFalse(panel.TechnicalDetailsVisibleForTests, "the technical box must be hidden by default");
            Assert.IsTrue(panel.ListItemsForTests.Count > 0, "the plain bullet list must still be shown");
            foreach (string line in panel.ListItemsForTests)
            {
                StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex("nodes|Handsfree"));
            }
        });
    }

    [TestMethod]
    public void TogglingOnShowsTheRawTechnicalBlockImmediatelyForTheCurrentPrompt()
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
            Assert.IsNotNull(form.CurrentPromptSeqForTests);

            StepPanel panel = form.StepPanelForTests;
            Assert.IsFalse(panel.TechnicalDetailsVisibleForTests);

            form.ClickTechnicalDetailsCheckBoxForTests();

            Assert.IsTrue(form.ShowTechnicalDetailsForTests);
            Assert.IsTrue(panel.TechnicalDetailsVisibleForTests, "flipping the toggle must redraw the panel that is on screen right now");
            StringAssert.Contains(panel.TechnicalDetailsTextForTests, "Earshot tasks exist");
        });
    }

    [TestMethod]
    public void TechnicalDetailsIsRememberedAcrossAFreshWindow()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form => form.ClickTechnicalDetailsCheckBoxForTests());
        MainFormTestHarness.Run(sandbox.Path, form => Assert.IsTrue(form.ShowTechnicalDetailsForTests, "a second window must read the same remembered choice"));
    }

    [TestMethod]
    public void RowDetailHidesTitleAndSettlesUntilTechnicalDetailsIsOn()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("02"));
            StringAssert.DoesNotMatch(form.RowDetailTextForTests, new System.Text.RegularExpressions.Regex("Settles:"));

            form.ClickTechnicalDetailsCheckBoxForTests();
            Assert.IsTrue(form.SelectRowForTests("02"));
            StringAssert.Contains(form.RowDetailTextForTests, "Settles:");
        });
    }
}
