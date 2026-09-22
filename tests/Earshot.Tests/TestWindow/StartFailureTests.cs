using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// ChildRunner.Start() can throw: the host executable it was given can vanish between
// PowerShell51.ExecutablePath() being read and Process.Start() actually running (or, as proved
// here, simply not exist). Before this fix, BeginRun (MainForm.cs) set _activeRunner to the new
// runner and only afterwards called runner.Start(); a throw from Start() unwound out of BeginRun
// with _activeRunner still set, so RunGate.CanStart refused every further start, including row 00
// Restore, the only way off a red banner, and ChildRunner.Kill() (called from KillActiveRun or the
// harness's own cleanup on a runner that was never started) threw its own unguarded
// InvalidOperationException on top of that.
[TestClass]
public sealed class StartFailureTests
{
    private const string NonExistentHost = @"C:\nowhere\not-a-real-powershell.exe";

    // Direct proof against ChildRunner alone: Kill() must never throw for a runner whose Start()
    // was never called (the shape MainForm now reaches on a caught Start() failure) or whose
    // Start() itself failed.
    [TestMethod]
    public void KillDoesNotThrowWhenTheProcessWasNeverStarted()
    {
        using var runner = new ChildRunner(
            NonExistentHost, "unused-driver.ps1", "unused.ps1", @"C:\nowhere\Earshot.exe", @"C:\nowhere\run",
            resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

        runner.Kill();
    }

    [TestMethod]
    public void KillDoesNotThrowAfterStartItselfThrew()
    {
        using var runner = new ChildRunner(
            NonExistentHost, "unused-driver.ps1", "unused.ps1", @"C:\nowhere\Earshot.exe", @"C:\nowhere\run",
            resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

        Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(runner.Start);

        runner.Kill();
    }

    // The real caller, through the real headless form: BeginRunForTests drives the exact same
    // BeginRun MainForm's own start sites call. A runner built on a host that cannot be launched
    // must leave the window usable, not crash the message loop, and refuse nothing further.
    [TestMethod]
    public void AFailedStartLeavesTheWindowUsableAndTheRowUnknownWithTheReasonShown()
    {
        using var sandbox = new TempFolder();
        string resultFolder = Path.Combine(sandbox.Path, "result-folder");

        var row = new ManifestRow
        {
            Number = "SF",
            Script = "unused.ps1",
            TestId = "start-failure-test",
            Kind = "utility",
            Halves = 1,
            Name = "Start failure test",
            Title = "Start failure test",
            Proves = "test only",
            Settles = "test only",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            var runner = new ChildRunner(
                NonExistentHost, "unused-driver.ps1", "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            // Must not throw out of BeginRun (and so not out of this STA thread body, which
            // MainFormTestHarness.Run would otherwise rethrow as its own failure).
            form.BeginRunForTests(displayRow, runner, resultFolder);

            Assert.IsNull(form.ActiveRunnerForTests, "_activeRunner must be cleared after a failed Start(), or every later start (including row 00 Restore) stays refused.");
            Assert.IsTrue(RunGate.CanStart(form.ActiveRunnerForTests), "RunGate must not see a failed start as an active runner.");

            // The reason is shown, not swallowed, but with technical details off (the default) it
            // is this row's own plain name and Copy's own plain sentence, never the raw exception
            // type name: that used to leak here regardless of the toggle, exactly the class of leak
            // NoBannedWordsInPlainModeTests exists to catch.
            Assert.IsFalse(form.ShowTechnicalDetailsForTests, "sanity: technical details must be off by default for this to prove anything.");
            StringAssert.Contains(form.StatusTextForTests, "Start failure test");
            StringAssert.Contains(form.StatusTextForTests, "this test could not be started");
            Assert.IsFalse(form.StatusTextForTests.Contains("Win32Exception", StringComparison.Ordinal),
                "the raw exception type name must never appear with technical details off.");

            // The row reads Unknown from disk too, the same way a kill does: a stale or missing
            // result under this folder must never be trusted as a pass by a later StateDeriver
            // walk.
            Assert.IsTrue(File.Exists(Path.Combine(resultFolder, "gui-killed.txt")), "No marker was written for the failed start; the row would read as whatever stale (or absent) result.json happens to be on disk.");

            // Restore (and every other row) is genuinely startable again, not merely reported as
            // such: select row 00 and confirm the Start button itself is enabled for it.
            Assert.IsTrue(form.SelectRowForTests("00"), "Row 00 (Restore) was not found in the real manifest.");
            Assert.IsTrue(form.StartButtonEnabledForTests, "Restore stayed refused after a failed Start() that was never actually an active run.");
        });
    }
}
