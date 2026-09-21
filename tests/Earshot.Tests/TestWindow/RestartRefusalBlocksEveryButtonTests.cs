using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A restart where 08's own full shut down requirement was not met is refused with no way past
// it (PowerCycleGateTests.cs pins the gate's own decision in isolation). This drives the real
// MainForm and proves the refusal actually blocks every one of the three buttons that could
// otherwise reach StartResumedSecondHalf: Start, the noted-start button (which a refusal never
// even makes visible), and Run all. PowerCycleVerdictOverrideForTests replaces only the real
// event log read, since RunPowerCycleProbe always reads this machine's own real history and the
// verdict here has to be Restart on purpose, not whatever this machine's history happens to say.
[TestClass]
public sealed class RestartRefusalBlocksEveryButtonTests
{
    [TestMethod]
    public void AResumed08WithARestartVerdictCannotStartAChildByAnyButton()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string realExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // 08's own first half, waiting for its shut down, with the exact half-marker criteria
        // and a leftAtRest that never locks the banner.
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "08-acceptance-power-cycle");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("08-acceptance-power-cycle", "pass")
                .WithCriterion("default-config", "pass").WithCriterion("blocked-before-power-cycle", "pass")
                .WithFinding("leftAtRest", "yes").WithFinishedUtc("2026-09-21T00:05:00.000Z")
                .Build());

        string scriptPath = Path.Combine(repoRoot, "tools", "live-tests", "08-AcceptancePowerCycle.ps1");
        string runRoot = Path.Combine(liveTestRoot, "20260921T000000Z");
        string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -ExePath \"" + realExePath +
            "\" -RunRoot \"" + runRoot + "\" -Resume";
        File.WriteAllText(Path.Combine(folder, "resume.txt"), line);

        // Run all's own record, pointed straight at "08": without this, a fresh Run all click
        // would first meet rows 01 to 07 with no evidence at all (NotRun, RunAllAdvance's own
        // "safe to auto-start") and try to start one of those for real, which would prove nothing
        // about the restart refusal this test exists to check.
        string windowStateRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest-gui");
        int index08 = RunAllOrder.Items.ToList().FindIndex(item => item.Key == "08");
        Assert.IsTrue(index08 >= 0, "\"08\" was not found in RunAllOrder.Items.");
        RunAllFile.Write(windowStateRoot, new RunAllRecord
        {
            Order = RunAllOrder.Items.Select(item => item.Key).ToArray(),
            StoppedAtIndex = index08,
        });

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.PowerCycleVerdictOverrideForTests = () => PowerCycleVerdict.Restart;
            Assert.IsTrue(form.SelectRowForTests("08"));

            form.ClickStartForTests();
            Assert.IsNull(form.ActiveRunnerForTests, "Start built a child even though the verdict was a restart, not a shut down.");
            Assert.IsFalse(form.NotedStartWarningVisibleForTests, "a refusal must never show the noted-start warning: there is nothing to click past.");
            StringAssert.Contains(form.StatusTextForTests, "restart, not a shut down");

            form.ClickNotedStartButtonForTests();
            Assert.IsNull(form.ActiveRunnerForTests, "the noted-start button started a child although nothing was pending for it.");

            form.ClickRunAllForTests();
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            Assert.IsNull(form.ActiveRunnerForTests, "Run all built a child even though the verdict was a restart, not a shut down.");
        });
    }
}
