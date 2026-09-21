using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// PowerCycleGateResult.StartNoted (a restart where a full shut down was needed, or an unreadable
// event log on 08 or 09) used to be indistinguishable from a clean Start: StartResumedSecondHalf
// built and started the child the same way either way, with no warning and nothing to click.
// PowerCycleGateTests.cs already pins the gate's own decision in isolation; this drives the real
// MainForm reaction to it. RunPowerCycleProbe always reads this machine's own real event log, so
// the verdict itself cannot be chosen from a test: ShowNotedStartWarningForTests calls the same
// production method a real StartNoted result would, with a verdict of this test's choosing.
[TestClass]
public sealed class NotedStartRequiresDeliberateClickTests
{
    [TestMethod]
    public void ANotedStartShowsItsWarningAndStartsNothingUntilTheDeliberateClick()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string scriptPath = Path.Combine(repoRoot, "tools", "live-tests", "08-AcceptancePowerCycle.ps1");
        string realExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string warning = PowerCycleGate.NotedWarning(PowerCycleRequirement.FullShutDown, PowerCycleVerdict.Unknown);

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("08"));
            var row = DisplayRow.Flatten(Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json")))
                .First(r => r.Number == "08");
            var instruction = new ResumeInstruction
            {
                ScriptPath = scriptPath,
                ExePath = realExePath,
                RunRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest", "20260921T000000Z"),
            };

            form.ShowNotedStartWarningForTests(host, row, instruction, warning);

            Assert.IsTrue(form.NotedStartWarningVisibleForTests, "the noted-start warning was never shown.");
            StringAssert.Contains(form.NotedStartWarningTextForTests, "could not be read");
            Assert.IsNull(form.ActiveRunnerForTests, "a noted start must never start anything before the deliberate click.");

            form.ClickNotedStartButtonForTests();

            Assert.IsFalse(form.NotedStartWarningVisibleForTests, "the warning must be withdrawn once it has been acted on.");
            Assert.IsNotNull(form.ActiveRunnerForTests, "the deliberate click did not start the noted run.");
        });
    }

    [TestMethod]
    public void SelectingADifferentRowWithdrawsAPendingNotedStartWarning()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string realExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("08"));
            var row = DisplayRow.Flatten(Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json")))
                .First(r => r.Number == "08");
            var instruction = new ResumeInstruction
            {
                ScriptPath = Path.Combine(repoRoot, "tools", "live-tests", "08-AcceptancePowerCycle.ps1"),
                ExePath = realExePath,
                RunRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest", "20260921T000000Z"),
            };

            form.ShowNotedStartWarningForTests(host, row, instruction, "a warning");
            Assert.IsTrue(form.NotedStartWarningVisibleForTests);

            Assert.IsTrue(form.SelectRowForTests("01"));

            // The real proof is this: the button is withdrawn (invisible), which is itself what
            // stops a click reaching ProceedWithNotedStart (Control.PerformClick is a no-op on an
            // invisible control). The click below is only a defensive extra, never load-bearing.
            Assert.IsFalse(form.NotedStartWarningVisibleForTests, "a warning belonging to row 08 must not survive selecting a different row.");
            form.ClickNotedStartButtonForTests();
            Assert.IsNull(form.ActiveRunnerForTests, "a withdrawn warning's button must not start anything.");
        });
    }
}
