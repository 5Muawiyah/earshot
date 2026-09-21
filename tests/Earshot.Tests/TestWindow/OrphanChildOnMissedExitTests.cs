using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// OnRunFinished used to call ChildRunner.WaitForExit and discard the result: a child that sent
// its own exit message but did not actually terminate within the grace window (a lingering
// handle, a slow-to-tear-down thread) was simply dropped, with nothing closing its stdin or
// killing it, leaving the OS process itself orphaned for as long as it happened to keep running.
[TestClass]
public sealed class OrphanChildOnMissedExitTests
{
    [TestMethod]
    public void AChildThatOutlivesItsOwnExitMessageIsKilledRatherThanOrphaned()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string driverPath = System.IO.Path.Combine(sandbox.Path, "orphan-driver.ps1");
        System.IO.File.WriteAllText(driverPath, OrphanDriverScript);
        string resultFolder = System.IO.Path.Combine(sandbox.Path, "result-folder");

        var row = new ManifestRow
        {
            Number = "OR",
            Script = "unused.ps1",
            TestId = "orphan-test",
            Kind = "utility",
            Halves = 1,
            Name = "Orphan test",
            Title = "Orphan test",
            Proves = "test only",
            Settles = "test only",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            // Short enough that this test does not wait for the child's own 30 s sleep, long
            // enough that the child has genuinely sent its exit message and started sleeping
            // before the deadline is checked.
            form.ExitGracePeriodForTests = TimeSpan.FromSeconds(2);

            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
            form.BeginRunForTests(displayRow, runner, resultFolder);

            int childProcessId = runner.ProcessId;

            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(120));
            Assert.IsNull(form.ActiveRunnerForTests, "OnRunFinished never completed.");

            bool stillRunning;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(childProcessId);
                stillRunning = !process.HasExited;
            }
            catch (ArgumentException)
            {
                stillRunning = false;
            }

            Assert.IsFalse(stillRunning, "the child process was left running after OnRunFinished returned: an orphan holding stdin.");
        }, timeout: TimeSpan.FromSeconds(300));
    }

    // Speaks the real wire protocol directly (the same prefix and base64 JSON
    // Send-EarshotTwMessage in ReadHostShim.ps1 writes) rather than going through
    // Invoke-GuiHalf.ps1, so the exit message and the process's own real exit can be pulled apart
    // in time, which the real driver (send exit, then "exit $code" on the very next line) never
    // allows on its own.
    private const string OrphanDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        function Send-OrphanTestMessage($Object)
        {
            $json = ($Object | ConvertTo-Json -Compress -Depth 6)
            $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
            [System.Console]::Out.WriteLine('@@EARSHOT-TW@@ ' + $wire)
            [System.Console]::Out.Flush()
        }

        Send-OrphanTestMessage ([ordered]@{ type = 'hello'; protocol = 1; pid = $PID; psVersion = $PSVersionTable.PSVersion.ToString(); script = 'orphan-test.ps1'; half = 'first' })
        Send-OrphanTestMessage ([ordered]@{ type = 'exit'; code = 0 })
        Start-Sleep -Seconds 30
        exit 0
        """;
}
