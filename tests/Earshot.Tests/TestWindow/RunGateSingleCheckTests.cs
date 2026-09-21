using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Two start paths used to reach a child process without ever asking RunGate's combined question
// (one active runner at a time, and the red at-rest banner locks every row except Restore): Run
// all's own carry-on button only ever checked the single-runner rule, never the banner, so
// carrying on past a killed row started the next one anyway; ProceedWithNotedStart checked
// nothing at all, so a click on it while some other run was already active started a second
// child outright. Both are fixed the same way: BeginRun (the one place every start path ends up
// before a child becomes active) and AdvanceRunAll's own dispatch now ask RunGate.CanStart's
// three-argument overload, so a future start path that forgets its own check still cannot reach a
// child through here.
[TestClass]
public sealed class RunGateSingleCheckTests
{
    // Reproduces the red banner being bypassed: Run all starts row 01 for real (the banner is
    // clear at that point), a forced kill turns the banner red mid-run the same way a real owner's
    // Stop-then-confirm would, and Run all halts there offering "Carry on with Run all". On the
    // old code that click moved straight on to starting row 02 for real, with the banner still
    // red. On the fixed code it must halt again instead, starting nothing.
    [TestMethod]
    public void CarryingOnPastAKilledRowWithTheBannerRedNeverStartsTheNextRow()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            ChildRunner? runningRow01 = form.ActiveRunnerForTests;
            Assert.IsNotNull(runningRow01, "Run all never started row 01. status=" + form.StatusTextForTests);
            Assert.IsGreaterThan(0, runningRow01!.ProcessId, "no real child process was started.");

            // The window's own forced hard stop: the same path a real Stop-then-confirm-Yes
            // reaches, which writes gui-killed.txt and turns the banner red.
            form.KillActiveRunForTests();

            Assert.IsNull(form.ActiveRunnerForTests, "the kill did not clear the active runner.");
            Assert.IsTrue(form.CarryOnVisibleForTests, "Run all did not offer to carry on past the killed row.");
            Assert.IsFalse(form.StartButtonEnabledForTests, "the red banner did not lock the Start button.");

            form.ClickCarryOnForTests();
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            Assert.IsNull(form.ActiveRunnerForTests,
                "carrying on past a killed row started the next one for real while the at-rest banner was still red.");
        });
    }

    // Reproduces two children alive at once: a real run is already active, a noted-start warning
    // for a different row is showing (the shape a restart-instead-of-shut-down verdict leaves),
    // and the noted-start button is clicked. On the old code ProceedWithNotedStart asked nothing
    // at all and started a second child outright.
    [TestMethod]
    public void ClickingTheNotedStartButtonWhileAnotherRunIsAlreadyActiveNeverStartsASecondChild()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();

            ChildRunner? first = form.ActiveRunnerForTests;
            Assert.IsNotNull(first, "the first Start click never became the active runner. status=" + form.StatusTextForTests);
            Assert.IsGreaterThan(0, first!.ProcessId, "no real child process was started.");

            var row = DisplayRow.Flatten(Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json")))
                .First(r => r.Number == "08");
            var instruction = new ResumeInstruction
            {
                ScriptPath = Path.Combine(repoRoot, "tools", "live-tests", "08-AcceptancePowerCycle.ps1"),
                ExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                RunRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest", "20260921T000000Z"),
            };
            form.ShowNotedStartWarningForTests(host, row, instruction, "a warning");
            Assert.IsTrue(form.NotedStartWarningVisibleForTests, "the noted-start warning was never shown.");

            form.ClickNotedStartButtonForTests();

            Assert.AreSame(first, form.ActiveRunnerForTests,
                "the noted-start button started a second child while the first run was still active.");
            Assert.IsTrue(form.NotedStartWarningVisibleForTests,
                "a refused noted start must leave its warning in place; nothing about it was acted on.");
        });
    }
}
