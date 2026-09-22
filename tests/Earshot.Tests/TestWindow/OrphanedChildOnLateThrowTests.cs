using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// EnsureMarker can throw (a corrupt run-sequence.json): before this fix that throw ran AFTER
// runner.Start(), and the catch's own runner.Dispose() only released the managed Process handle,
// never touching the real OS process, so a genuinely live child stayed running after this window
// believed nothing was, and RunGate let a fresh start (Restore included) begin beside it. Every
// throwing step now runs before Start() itself, and Dispose kills a live process rather than only
// letting go of it.
[TestClass]
public sealed class OrphanedChildOnLateThrowTests
{
    [TestMethod]
    public void ACorruptRunSequenceCounterNeverStartsARealChildAndRunGateStaysUsable()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        Directory.CreateDirectory(liveTestRoot);
        // A corrupt counter: RunSequence.EnsureMarker throws before this window ever calls
        // runner.Start() for the row below.
        File.WriteAllText(Path.Combine(liveTestRoot, RunSequence.CounterFileName), "not json at all");

        string driverPath = Path.Combine(sandbox.Path, "inert-driver.ps1");
        File.WriteAllText(driverPath, InertDriverScript);
        string resultFolder = Path.Combine(liveTestRoot, "20260921T000000Z", "orphan-throw-test");

        var row = new ManifestRow
        {
            Number = "OT", Script = "unused.ps1", TestId = "orphan-throw-test", Kind = "utility",
            Halves = 1, Name = "Orphan throw test", Title = "Orphan throw test", Proves = "test only", Settles = "test only",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            form.BeginRunForTests(displayRow, runner, resultFolder);

            Assert.IsFalse(runner.StartedForTests,
                "the corrupt counter must be discovered and thrown before the real child is ever started.");
            Assert.IsNull(form.ActiveRunnerForTests, "_activeRunner must be cleared after the failed start.");

            // The teardown (gui-killed.txt, clearing _activeRunner) is recorded synchronously,
            // inside the very same catch BeginRunForTests already returned from: RunGate is
            // usable again, row 00 Restore included, by the time this assertion runs, never left
            // waiting on an orphan that no longer exists.
            Assert.IsTrue(File.Exists(Path.Combine(resultFolder, "gui-killed.txt")),
                "no marker was written for the failed start.");
            Assert.IsTrue(form.SelectRowForTests("00"), "Row 00 (Restore) was not found in the real manifest.");
            Assert.IsTrue(form.StartButtonEnabledForTests, "Restore stayed refused after a start that never actually created a child.");
        });
    }

    [TestMethod]
    public void DisposeKillsARealLiveChildRatherThanOnlyReleasingItsHandle()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string driverPath = Path.Combine(sandbox.Path, "sleepy-driver.ps1");
        File.WriteAllText(driverPath, SleepyDriverScript);
        string resultFolder = Path.Combine(sandbox.Path, "result-folder");

        var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
            resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

        runner.Start();
        Assert.IsTrue(runner.StartedForTests);
        int pid = runner.ProcessId;

        // The real child is genuinely alive (its own script is still sleeping) at the moment
        // Dispose runs.
        bool aliveBeforeDispose;
        using (var process = System.Diagnostics.Process.GetProcessById(pid))
        {
            aliveBeforeDispose = !process.HasExited;
        }

        Assert.IsTrue(aliveBeforeDispose, "fixture sanity: the child must still be alive when Dispose runs.");

        runner.Dispose();

        bool stillRunning;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            MainFormTestHarness.PumpUntil(() => process.HasExited, TimeSpan.FromSeconds(10));
            stillRunning = !process.HasExited;
        }
        catch (ArgumentException)
        {
            stillRunning = false;
        }

        Assert.IsFalse(stillRunning, "Dispose must kill a live child, not only release its managed Process handle.");
    }

    private const string InertDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        function Send-InertTestMessage($Object)
        {
            $json = ($Object | ConvertTo-Json -Compress -Depth 6)
            $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
            [System.Console]::Out.WriteLine('@@EARSHOT-TW@@ ' + $wire)
            [System.Console]::Out.Flush()
        }

        Send-InertTestMessage ([ordered]@{ type = 'hello'; protocol = 1; pid = $PID; psVersion = $PSVersionTable.PSVersion.ToString(); script = 'inert-test.ps1'; half = 'first' })
        Send-InertTestMessage ([ordered]@{ type = 'exit'; code = 0 })
        exit 0
        """;

    private const string SleepyDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'
        Start-Sleep -Seconds 60
        exit 0
        """;
}
