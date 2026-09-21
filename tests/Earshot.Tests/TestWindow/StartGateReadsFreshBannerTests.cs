using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The start gate used to read MainForm's own cached _banner field, refreshed only at open and
// after every half this window itself ran. Rows 00 and 07's own unavailable branches
// (Copy.RestoreUninstallOfferNotAvailable, Copy.PlanBNotAvailable) both send the owner to a
// console to run the real driver directly; a console run finishing not at rest while this window
// stayed open left the window's own copy at whatever it was before (often None, on a window that
// has not itself run anything yet), so Start read the stale copy and started a child over a
// machine the fresh evidence on disk already says is not at rest.
[TestClass]
public sealed class StartGateReadsFreshBannerTests
{
    [TestMethod]
    public void BeginRunRefusesAFreshStartWhenDiskEvidenceIsNotAtRestEvenThoughTheCachedBannerIsStillNone()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        var row = new ManifestRow
        {
            Number = "FB",
            Script = "unused.ps1",
            TestId = "fresh-banner-test",
            Kind = "utility",
            Halves = 1,
            Name = "Fresh banner test",
            Title = "Fresh banner test",
            Proves = "test only",
            Settles = "test only",
        };
        var displayRow = new DisplayRow { Row = row };

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            // Nothing has run through this window yet: its own cached banner is None.
            Assert.IsNull(form.ActiveRunnerForTests);

            // A console run of one of the two commands rows 00/07 name finished, outside this
            // window, leaving the machine not at rest.
            string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
            string json = new ResultJsonFixture("01-a2dp-oneshot", "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "no")
                .Build();
            ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);

            string driverPath = Path.Combine(sandbox.Path, "inert-driver.ps1");
            File.WriteAllText(driverPath, InertDriverScript);
            string resultFolder = Path.Combine(sandbox.Path, "fresh-banner-result");

            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);

            // The real caller: BeginRunForTests drives the exact same BeginRun every start site
            // (including this one) funnels through.
            form.BeginRunForTests(displayRow, runner, resultFolder);

            // ProcessId throws unless Process.Start() actually ran: a synchronous, timing-free way
            // to tell whether BeginRun's own gate let Start() through.
            bool started;
            try
            {
                _ = runner.ProcessId;
                started = true;
            }
            catch (InvalidOperationException)
            {
                started = false;
            }

            Assert.IsFalse(started,
                "BeginRun started a child even though fresh disk evidence already shows the machine is not at " +
                "rest; the gate must recompute the banner from disk at the moment it checks, not trust its last " +
                "cached copy.");
            Assert.IsNull(form.ActiveRunnerForTests);
        });
    }

    // The Start button's own Enabled state (UpdateStartButton) is what a real click depends on
    // (PerformClick does nothing on a disabled button): it must read the same fresh disk state as
    // the gate, or an owner sees a clickable Start button that a click then silently does nothing
    // for.
    [TestMethod]
    public void TheStartButtonReadsTheFreshBannerTooNotOnlyTheCachedCopy()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"), "Row 01 was not found in the real manifest.");
            Assert.IsTrue(form.StartButtonEnabledForTests, "Row 01 must start out enabled: no evidence exists yet.");

            string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
            string json = new ResultJsonFixture("01-a2dp-oneshot", "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "no")
                .Build();
            ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);

            // Nothing here calls PopulateRows or otherwise refreshes the cached banner: only
            // re-selecting the same row, which re-runs UpdateStartButton.
            form.SelectRowForTests("01");

            Assert.IsFalse(form.StartButtonEnabledForTests,
                "The Start button stayed enabled for a locked row after fresh disk evidence showed the machine " +
                "is not at rest; UpdateStartButton must recompute the banner too, not only the gate inside BeginRun.");
        });
    }

    // Activated (an owner alt-tabbing or clicking back onto this window, the only chance it gets
    // to notice what a console run left on disk) must refresh both the row list and the banner
    // label, the same as PopulateRows does at open and after every half this window itself runs.
    [TestMethod]
    public void ActivatingTheWindowRefreshesTheBannerFromDisk()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsFalse(form.BannerVisibleForTests, "No evidence exists yet: the banner must start out hidden.");

            string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
            string json = new ResultJsonFixture("01-a2dp-oneshot", "pass")
                .WithCriterion("c1", "pass")
                .WithFinding("leftAtRest", "no")
                .Build();
            ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"), json);

            form.RaiseActivatedForTests();

            Assert.IsTrue(form.BannerVisibleForTests, "Activated did not refresh the banner from disk.");
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
}
