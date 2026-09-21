using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A killed second half must never look like only the first half ever happened: the row reads
// Unknown, the at-rest banner locks every other row, and "Carry on with the second half" is
// withdrawn back to a plain "Start" (PendingRunFinder no longer offers the stale first-half
// result once gui-killed.txt sits beside it). Real MainForm, a real second-half child, a real
// forced kill; test 10 variant 1 is the only two-half row tools\live-tests\selftest\Fakes.psm1
// has fixture data for, so its resumed second half is the one two-half flow this class can drive
// for real in a sandbox window.
[TestClass]
public sealed class KilledSecondHalfNeverMasksAsFirstHalfOnlyTests
{
    [TestMethod]
    public void AKillDuringTheSecondHalfShowsTheBannerWithdrawsCarryOnAndReadsUnknown()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string runRoot = Path.Combine(liveTestRoot, "20260921T000000Z");
        const string testId = "10-shutdown-messages-v1";
        string testFolder = Path.Combine(runRoot, testId);

        // The first half's own stale record: no criteria, only the stateBeforeRestart finding
        // (test 10's own first-half marker), the exact shape a real first half leaves. Never run
        // for real here: PendingRunFinder and StateDeriver read it off disk the same either way,
        // and this class exists to prove what happens to a killed SECOND half, not the first.
        ResultJsonFixture.WriteTo(Path.Combine(testFolder, "result.json"),
            new ResultJsonFixture(testId, "inconclusive")
                .WithFinding("stateBeforeRestart", "connected")
                .WithFinding("leftAtRest", "no-on-purpose")
                .WithFinishedUtc("2026-09-21T00:00:00.000Z")
                .Build());

        string scriptPath = Path.Combine(repoRoot, "tools", "live-tests", "10-ShutdownMessages.ps1");
        string exePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string resumeLine = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -ExePath \"" +
            exePath + "\" -RunRoot \"" + runRoot + "\" -Resume -Variant 1";
        File.WriteAllText(Path.Combine(testFolder, "resume.txt"), resumeLine);

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.PowerCycleVerdictOverrideForTests = () => PowerCycleVerdict.Restart;
            Assert.IsTrue(form.SelectRowForTests("10.1"));
            Assert.AreEqual("Carry on with the second half", form.StartButtonTextForTests,
                "the pending first half was not recognised before the second half ever started.");

            form.ClickStartForTests();

            ChildRunner? runner = form.ActiveRunnerForTests;
            Assert.IsNotNull(runner, "the second half never became the active runner. status=" + form.StatusTextForTests);
            Assert.IsGreaterThan(0, runner!.ProcessId, "no real child process was started for the second half.");

            form.KillActiveRunForTests();

            Assert.IsNull(form.ActiveRunnerForTests, "the kill did not clear the active runner.");
            Assert.AreEqual("Start", form.StartButtonTextForTests,
                "\"Carry on with the second half\" was not withdrawn: the kill must stop the stale first-half result from still being offered as pending.");
            Assert.IsFalse(form.StartButtonEnabledForTests, "the red banner did not lock the Start button for this row.");
            Assert.AreEqual("Unknown", form.RowStateTextForTests("10.1"),
                "the row did not read Unknown after its second half was killed.");

            Assert.IsTrue(form.SelectRowForTests("01"));
            Assert.IsFalse(form.StartButtonEnabledForTests, "the at-rest banner did not lock an unrelated row too.");
        });
    }
}
