using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// _currentPromptSeq (MainForm's own silence-watchdog state) was set by the
// first prompt and never cleared by anything but the run ending, so OnWatchdogTick's "no prompt
// pending" gate read false for the rest of any run: the watchdog could fire at most once, for the
// very first prompt, and never again. Drives the real MainForm, a real sandboxed child and a real
// button click (StepPanel.ClickFirstButtonForTests), testing the caller and not just the
// predicate. Time is moved by backdating _lastActivityUtc rather
// than by waiting for real minutes to pass, in both directions.
[TestClass]
public sealed class SilenceWatchdogTests
{
    [TestMethod]
    public void AReplyClearsThePendingPromptSoTheWatchdogCanFireAgain()
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
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "the first prompt (Show-Preconditions) never arrived.");

            // While a prompt is genuinely pending, silence must never be reported, however long it
            // has been: a prompt on screen is never a hang.
            form.SetLastActivityUtcForTests(DateTimeOffset.UtcNow.AddHours(-3));
            form.ForceWatchdogTickForTests();
            StringAssert.DoesNotMatch(form.StatusTextForTests, new System.Text.RegularExpressions.Regex("silent"),
                "the watchdog must not fire while a prompt is on screen, however long ago it appeared.");

            // StepPanel disables its buttons for 800 ms after a prompt appears, to absorb a click
            // meant for the previous prompt; PerformClick is a no-op on a disabled button
            // (Button.CanSelect is false while Enabled is false), so the wait must actually
            // elapse, pumped, first.
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));

            // The reply is a real button click through the real StepPanel, not a call that
            // bypasses it: this proves the caller,
            // not just the predicate in isolation.
            form.ClickCurrentPromptButtonForTests();

            // The clear happens synchronously inside StepPanel's own click handler
            // (ReplySent -> MainForm's subscription), before anything pumps a further BeginInvoke,
            // so this is not a race against the next prompt arriving.
            Assert.IsNull(form.CurrentPromptSeqForTests,
                "a reply must clear the pending prompt immediately, not only when the run ends.");

            // Now that no prompt is pending, backdating activity and ticking again must report
            // silence: this is the bug itself. Before the fix, _currentPromptSeq was still set to
            // the first prompt's own seq forever, so this assertion is what failed.
            form.SetLastActivityUtcForTests(DateTimeOffset.UtcNow.AddMinutes(-20));
            form.ForceWatchdogTickForTests();
            StringAssert.Contains(form.StatusTextForTests, "silent",
                "the watchdog never fires again after the first prompt is answered.");
        });
    }
}
