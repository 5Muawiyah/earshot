using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Investigates the reported "a row that has just finished still reads Not run yet" complaint.
// Driving a real MainForm through a real sandboxed run end to end (below) disproves the literal
// symptom for a straightforward single run: PopulateRows() is already called from OnRunFinished
// and the list already stops reading "Not run yet" the moment a result.json lands. What real
// execution DOES find, in the same neighbourhood, is this: ComputeState always passed this
// window's own _exePath into StateDeriver's earlier-build check, even in a practice window, where
// the fake-device driver never runs that exe at all; it always records its own sandboxed stand-in
// path in result.json's own "exe" field instead (confirmed by reading the written result.json: it
// named "...\release\Earshot.exe", not the window's own chosen path). That mismatch made the
// earlier-build check fire on every single practice-window pass, so a row that had genuinely just
// passed cleanly always read "Worked, with an older copy of Earshot" rather than a plain "Worked"
// no reader could ever clear, since a practice window never runs a real build at all. Fixed by
// passing null for the chosen exe in a practice window (src\Earshot.TestWindow\Ui\MainForm.cs,
// ComputeState): StateDeriver.Derive already treats a null chosenExePath as "the check does not
// apply", exactly the way AllScriptsAndHalvesThroughWindowTests already calls it directly.
[TestClass]
public sealed class RowStateRefreshesAfterARunTests
{
    [TestMethod]
    public void ARowsListedStateChangesFromNotRunYetOnceItsRunEnds()
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
            Assert.AreEqual(
                Copy.PlainNotRun, form.RowStateTextForTests("01"),
                "row 01 must start out unrun for this test to prove anything.");

            form.ClickStartForTests();

            // Answer "yes" to every real prompt this row raises, the same way SandboxFakesSmokeTests
            // drives 01-A2dpOneShot.ps1 directly through ChildRunner: the first button is always the
            // affirmative one (PromptPresenter's own Buttons ordering), and the "one" fake-input case
            // is built to reach a clean pass when answered that way throughout.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (form.ActiveRunnerForTests is not null && DateTime.UtcNow < deadline)
            {
                MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null || form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(30));
                if (form.ActiveRunnerForTests is null)
                {
                    break;
                }

                if (form.CurrentPromptSeqForTests is null)
                {
                    continue;
                }

                // StepPanel disables its buttons for 800 ms after a prompt appears (absorbs a click
                // meant for the previous prompt); PerformClick is a no-op until that has elapsed.
                MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));
                form.ClickCurrentPromptButtonForTests();
            }

            Assert.IsNull(form.ActiveRunnerForTests, "the run against the fake device never finished within the deadline.");
            Assert.IsTrue(form.ResultPanelVisibleForTests, "the run finished without ever showing a result.");

            string state = form.RowStateTextForTests("01") ?? string.Empty;
            Assert.AreNotEqual(
                Copy.PlainNotRun, state,
                "row 01 still reads \"" + Copy.PlainNotRun + "\" in the list after its own run finished and a result.json was written.");
            Assert.AreEqual(
                Copy.PlainPassed, state,
                "row 01's run against the fake device answered yes throughout; a practice window never runs a real " +
                "build at all, so the earlier-build check must not apply and this must read a plain, unqualified pass.");
        });
    }
}
