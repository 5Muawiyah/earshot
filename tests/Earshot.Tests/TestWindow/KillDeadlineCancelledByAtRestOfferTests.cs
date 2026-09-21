using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// OnStopClicked's abort-and-wait path arms a 60 s kill deadline while a prompt is pending, on the
// assumption the script may be stuck. But aborting one prompt does not always stop the script:
// the shim's own throw on "A <seq>" unwinds into the script's own catch/finally, and
// Complete-LiveTestRun's at-rest closing check still runs there, which can genuinely ask a new
// question (offering to block the nodes) from the very same, still-alive process. A new prompt
// arriving is proof the process answered, not proof it is hung, so the deadline armed for the
// old one must never survive it. Real MainForm, a real sandboxed child, real button clicks:
// nothing here ever lets a real kill-confirmation MessageBox appear, which would block the test.
[TestClass]
public sealed class KillDeadlineCancelledByAtRestOfferTests
{
    [TestMethod]
    public void ANewPromptArrivingAfterAnAbortClearsTheArmedKillDeadline()
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
            int? firstSeq = form.CurrentPromptSeqForTests;
            Assert.IsNotNull(firstSeq, "the first prompt (Show-Preconditions) never arrived.");

            // Stop while this prompt is pending: OnStopClicked's abort-and-wait path, which arms
            // the 60 s kill deadline rather than killing outright.
            form.ClickStopForTests();
            Assert.IsTrue(form.HasKillDeadlineForTests, "Stop while a prompt is pending must arm the kill deadline.");

            // The abort unwinds into the script's own finally, whose at-rest closing check offers
            // to block the nodes (they start Allowed for 01-a2dp-oneshot|first): a genuinely new
            // prompt, from the same process, proving it is still alive and cooperating.
            MainFormTestHarness.PumpUntil(
                () => form.CurrentPromptSeqForTests is not null && form.CurrentPromptSeqForTests != firstSeq,
                TimeSpan.FromSeconds(30));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "no further prompt arrived after the abort.");
            Assert.AreNotEqual(firstSeq, form.CurrentPromptSeqForTests, "the same prompt seq came back; this is not a new prompt.");

            Assert.IsFalse(form.HasKillDeadlineForTests,
                "a new prompt after the abort proves the process is alive; the old kill deadline must be withdrawn.");
        });
    }
}
