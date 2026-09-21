using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The real caller: BeginRun writes gui-run-started.txt the moment a half actually starts, and
// removes it on every path this window itself sees a half end (a clean exit through
// OnRunFinished, or a forced stop through MarkUnknownAndReset). Only a half that dies together
// with the window (never proved here: that would require actually killing this test process)
// ever leaves it behind, which is exactly what RunStartedMarkerStateDeriverTests,
// RunStartedMarkerBannerTests and RunStartedMarkerPendingRunTests each prove happens when it is.
[TestClass]
public sealed class RunStartedMarkerMainFormTests
{
    private const string MarkerFileName = "gui-run-started.txt";

    [TestMethod]
    public void BeginRunWritesTheMarkerAndACleanExitRemovesIt()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        var row = new ManifestRow
        {
            Number = "RS", Script = "unused.ps1", TestId = "run-started-marker-test", Kind = "utility",
            Halves = 1, Name = "n", Title = "t", Proves = "p", Settles = "s",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            string driverPath = Path.Combine(sandbox.Path, "inert-driver.ps1");
            File.WriteAllText(driverPath, InertDriverScript);
            string resultFolder = Path.Combine(sandbox.Path, "run-started-result");

            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            form.BeginRunForTests(displayRow, runner, resultFolder);

            MainFormTestHarness.PumpUntil(() => File.Exists(Path.Combine(resultFolder, MarkerFileName)), TimeSpan.FromSeconds(30));
            Assert.IsTrue(File.Exists(Path.Combine(resultFolder, MarkerFileName)), "BeginRun did not write the run-started marker once Start() succeeded.");

            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(120));
            Assert.IsNull(form.ActiveRunnerForTests, "OnRunFinished never completed.");

            Assert.IsFalse(File.Exists(Path.Combine(resultFolder, MarkerFileName)), "A clean exit must remove the run-started marker; a later open must not read this run as abandoned.");
        });
    }

    [TestMethod]
    public void KillActiveRunRemovesTheMarkerToo()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        var row = new ManifestRow
        {
            Number = "RK", Script = "unused.ps1", TestId = "run-started-marker-kill-test", Kind = "utility",
            Halves = 1, Name = "n", Title = "t", Proves = "p", Settles = "s",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            string driverPath = Path.Combine(sandbox.Path, "slow-driver.ps1");
            File.WriteAllText(driverPath, SlowDriverScript);
            string resultFolder = Path.Combine(sandbox.Path, "run-started-kill-result");

            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            form.BeginRunForTests(displayRow, runner, resultFolder);

            MainFormTestHarness.PumpUntil(() => File.Exists(Path.Combine(resultFolder, MarkerFileName)), TimeSpan.FromSeconds(30));
            Assert.IsTrue(File.Exists(Path.Combine(resultFolder, MarkerFileName)));

            form.KillActiveRunForTests();

            Assert.IsFalse(File.Exists(Path.Combine(resultFolder, MarkerFileName)), "A forced kill must remove the run-started marker; gui-killed.txt alone (already written) is enough to explain this row.");
            Assert.IsTrue(File.Exists(Path.Combine(resultFolder, "gui-killed.txt")));
        });
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

    // Sends hello, then sleeps well past this test's own kill: long enough that
    // KillActiveRunForTests always finds it still alive to kill.
    private const string SlowDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        function Send-SlowTestMessage($Object)
        {
            $json = ($Object | ConvertTo-Json -Compress -Depth 6)
            $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
            [System.Console]::Out.WriteLine('@@EARSHOT-TW@@ ' + $wire)
            [System.Console]::Out.Flush()
        }

        Send-SlowTestMessage ([ordered]@{ type = 'hello'; protocol = 1; pid = $PID; psVersion = $PSVersionTable.PSVersion.ToString(); script = 'slow-test.ps1'; half = 'first' })
        Start-Sleep -Seconds 120
        exit 0
        """;
}
