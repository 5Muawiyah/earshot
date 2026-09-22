using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Run all used to simply refuse under a red or unknown banner, the same as every other start
// route: the owner had to notice the banner, go select row 00 Restore by hand, run it, then come
// back and click "Run all" again. Now the window's own main control gets this computer back to a
// known state itself: it shows the same plain advice the not-at-rest result line gives as a
// deliberate step, then, once the owner says they have done it, starts row 00 Restore (the one
// start the banner lock already exempts) through the same single start gate every other route
// uses; only a Restore that itself records this computer at rest lets it carry on with the rest.
[TestClass]
public sealed class RunAllRestoreRecoveryTests
{
    [TestMethod]
    public void UnderARedBannerRunAllShowsTheAdviceStepAndStartsNothing()
    {
        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");

        // A run finished not at rest, outside this window: the fail-closed rule any start route
        // already reads (StartGateReadsFreshBannerTests pins the same fixture shape).
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();

            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests, "the red-banner advice step never appeared.");
            StringAssert.Contains(form.RunAllRestoreAdviceTextForTests, "case");
            Assert.IsFalse(form.RunAllRestoreAdviceTextForTests.Contains("click", StringComparison.OrdinalIgnoreCase),
                "the advice must never advise a click: a left click connects the AirPods to this computer.");
            Assert.IsNull(form.ActiveRunnerForTests, "Run all started something before the owner ever clicked through the advice.");
            Assert.IsFalse(form.RunAllActiveForTests, "Run all must not read as active until the owner has acted on the advice.");
        });
    }

    [TestMethod]
    public void ContinuingPastTheAdviceStartsRowZeroRestoreAndNothingElse()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string liveTestRoot = Path.Combine(sandbox.Path, "local", "Earshot", "livetest");
        string folder = Path.Combine(liveTestRoot, "20260921T000000Z", "01-a2dp-oneshot");
        ResultJsonFixture.WriteTo(Path.Combine(folder, "result.json"),
            new ResultJsonFixture("01-a2dp-oneshot", "pass").WithCriterion("c1", "pass").WithFinding("leftAtRest", "no").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRunAllForTests();
            Assert.IsTrue(form.RunAllRestoreAdviceVisibleForTests);

            form.ClickRunAllRestoreContinueForTests();

            MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is not null, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(form.ActiveRunnerForTests, "clicking through the advice never started anything.");
            Assert.AreEqual("00", form.ActiveRowNumberForTests, "the red-banner recovery step must start row 00 Restore, and nothing else.");
            Assert.IsFalse(form.RunAllRestoreAdviceVisibleForTests, "the advice step must clear once acted on.");
        });
    }

    [TestMethod]
    public void ARestoreThatRecordsAtRestYesAdvancesIntoTheOrdinarySequence()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        string driverPath = Path.Combine(sandbox.Path, "restore-yes-driver.ps1");
        File.WriteAllText(driverPath, ImmediateExitDriverScript);
        string resultFolder = Path.Combine(sandbox.Path, "restore-yes-result");
        ResultJsonFixture.WriteTo(Path.Combine(resultFolder, "result.json"),
            new ResultJsonFixture("00-restore", "pass").WithCriterion("setup", "pass").WithFinding("leftAtRest", "yes").Build());

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
            form.BeginRunAllRestoreRecoveryForTests(runner, resultFolder);

            // A clean at-rest Restore must carry Run all straight into the next item (row 01, which
            // has no evidence of its own here) rather than stopping once Restore itself is done.
            MainFormTestHarness.PumpUntil(
                () => form.ActiveRunnerForTests is not null && form.ActiveRowNumberForTests == "01", TimeSpan.FromSeconds(30));
            Assert.AreEqual("01", form.ActiveRowNumberForTests, "Restore recording at-rest yes did not advance Run all into the next test.");
            Assert.IsTrue(form.RunAllActiveForTests);
        });
    }

    [TestMethod]
    public void ARestoreThatDoesNotRecordAtRestYesHaltsAndStartsNothing()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        foreach (string? leftAtRest in new[] { "no", "unknown", null })
        {
            using var sandbox = new TempFolder();
            string driverPath = Path.Combine(sandbox.Path, "restore-no-driver.ps1");
            File.WriteAllText(driverPath, ImmediateExitDriverScript);
            string resultFolder = Path.Combine(sandbox.Path, "restore-no-result");

            var fixture = new ResultJsonFixture("00-restore", "pass").WithCriterion("setup", "pass");
            if (leftAtRest is not null)
            {
                fixture = fixture.WithFinding("leftAtRest", leftAtRest);
            }

            ResultJsonFixture.WriteTo(Path.Combine(resultFolder, "result.json"), fixture.Build());

            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                var runner = new ChildRunner(host, driverPath, "unused.ps1", @"C:\nowhere\Earshot.exe", resultFolder,
                    resume: false, variant: 0, offerUninstall: false, allowPlanB: false);
                form.BeginRunAllRestoreRecoveryForTests(runner, resultFolder);

                MainFormTestHarness.PumpUntil(() => form.ActiveRunnerForTests is null, TimeSpan.FromSeconds(30));
                Assert.IsNull(form.ActiveRunnerForTests,
                    "leftAtRest '" + (leftAtRest ?? "(missing)") + "' must halt Run all rather than start another test.");
                Assert.IsFalse(form.RunAllActiveForTests);
            });
        }
    }

    // Sends the real wire protocol's own hello and exit messages, immediately, so OnRunFinished
    // reads whatever result.json the test wrote beforehand rather than anything the driver itself
    // produces.
    private const string ImmediateExitDriverScript = """
        param(
            [Parameter(Mandatory = $true)][string]$Script,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$RunRoot
        )
        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        function Send-TestMessage($Object)
        {
            $json = ($Object | ConvertTo-Json -Compress -Depth 6)
            $wire = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
            [System.Console]::Out.WriteLine('@@EARSHOT-TW@@ ' + $wire)
            [System.Console]::Out.Flush()
        }

        Send-TestMessage ([ordered]@{ type = 'hello'; protocol = 1; pid = $PID; psVersion = $PSVersionTable.PSVersion.ToString(); script = 'restore-recovery-test.ps1'; half = 'first' })
        Send-TestMessage ([ordered]@{ type = 'exit'; code = 0 })
        exit 0
        """;
}
