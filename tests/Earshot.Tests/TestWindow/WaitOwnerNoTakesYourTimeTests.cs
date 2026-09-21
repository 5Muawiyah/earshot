using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Wait-Owner's "No" button already sent nothing (PromptButton's own SendsReply: false), but a
// click on it did nothing else either: no line, no acknowledgement, nothing to say the click was
// even seen. A real click that leaves the screen looking exactly as it did a moment before reads
// like the button did not work. Real MainForm, a real sandboxed 13-GraceWindow.ps1, real button
// clicks: proves the caller (StepPanel's own click handler), not a presenter fact in isolation.
[TestClass]
public sealed class WaitOwnerNoTakesYourTimeTests
{
    [TestMethod]
    public void NoAtAWaitOwnerStepShowsAPlainTakeYourTimeLineAndSendsNothing()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("13"));
            form.ClickStartForTests();

            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            int? preconditionsSeq = form.CurrentPromptSeqForTests;
            Assert.IsNotNull(preconditionsSeq, "Show-Preconditions never arrived.");

            // StepPanel disables its buttons for 800 ms after a prompt appears (absorbs a click
            // meant for the previous prompt), so PerformClick is a no-op until that has elapsed.
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));

            // Answer "ready to start?" so the script reaches its first Wait-Owner step.
            form.ClickCurrentPromptButtonForTests();
            MainFormTestHarness.PumpUntil(
                () => form.CurrentPromptSeqForTests is not null && form.CurrentPromptSeqForTests != preconditionsSeq,
                TimeSpan.FromSeconds(30));
            int? waitOwnerSeq = form.CurrentPromptSeqForTests;
            Assert.IsNotNull(waitOwnerSeq, "the first Wait-Owner step (\"Connect the AirPods...\") never arrived.");

            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));

            // The second button is Wait-Owner's own "No" (PromptPresenter.PresentWaitOwner:
            // Buttons = [Yes, No], No carrying SendsReply: false).
            form.ClickPromptButtonForTests(1);

            StringAssert.Contains(form.StepPanelAcknowledgementTextForTests.ToLowerInvariant(), "take your time");
            Assert.AreEqual(waitOwnerSeq, form.CurrentPromptSeqForTests,
                "the prompt must still be the same one: a click on No must never advance to a new prompt.");
        });
    }
}
