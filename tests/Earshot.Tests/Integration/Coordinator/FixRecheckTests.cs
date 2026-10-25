using System.Diagnostics;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// A Fix connection run started from the tray can end with a new pair that is not protected (its adoption failed). The tray
// must not wait for the next sign-in to say so: when the run's process ends, the start checks run again.
[TestClass]
public sealed class FixRecheckTests
{
    [TestMethod]
    public void WhenAFixRunEndsTheStartChecksRunAgainAndTheCardCanShowAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.Start();
        Assert.AreEqual(1, h.Recovery.Runs);
        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));

        h.Coordinator.RecheckProtectionAfterFix();
        h.Pump();

        Assert.AreEqual(2, h.Recovery.Runs, "A Fix run that stopped half way is answered again.");
        Assert.AreEqual(2, h.ProtectionScan.Reads, "The check for unprotected AirPods runs again.");
        Assert.AreEqual(2, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    // A pair made in Windows' settings, or by a Fix run started from Setup or the install script, arrives as a new device group.
    // The check for AirPods Earshot does not protect runs again then, after a settle, not only at the next sign-in. The card
    // keeps its once-per-run rule.
    [TestMethod]
    public void ANewDeviceGroupRunsTheCheckAgainAfterASettleAndTheCardShowsOnce()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, false, 0);
        h.Start();
        Assert.AreEqual(1, h.ProtectionScan.Reads);

        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        DeviceSnapshot before = Devices.Idle(2);
        h.Monitor.Publish(before with { AllGroups = [.. before.AllGroups, new DeviceModel(Guid.NewGuid(), "AirPods", ConnectionState.Disconnected, [])] });
        h.Pump();
        Assert.AreEqual(1, h.ProtectionScan.Reads, "The check waits for the new device to settle.");

        h.Advance(BlockCoordinator.NewDeviceSettle);
        Assert.AreEqual(2, h.ProtectionScan.Reads, "A new device group runs the check again.");
        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));

        // The same groups again: nothing new, so no check.
        h.Monitor.Publish(Devices.Idle(3) with { AllGroups = h.Monitor.Current.AllGroups });
        h.Advance(BlockCoordinator.NewDeviceSettle);
        Assert.AreEqual(2, h.ProtectionScan.Reads);
    }

    [TestMethod]
    public void AfterClosingARecheckDoesNothing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Coordinator.BeginShutdown();
        h.Pump();
        int runs = h.Recovery.Runs;

        h.Coordinator.RecheckProtectionAfterFix();
        h.Pump();

        Assert.AreEqual(runs, h.Recovery.Runs);
    }

    // A Fix run started by Setup.exe or by the install script is not a process the tray started, so the tray cannot watch it end.
    // The fix verb sets a named event as it ends and the tray waits on it. The run here is the real, built Earshot.exe, in safe
    // mode against a temporary data folder (whose event name is its own), started by this test and not by any launcher.
    [TestMethod]
    public void ARunStartedOutsideTheTrayMakesTheTrayRecheck()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var temp = new TempFolder();
        using var safe = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, temp.File("data"));
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.Start();
        Assert.AreEqual(1, h.Recovery.Runs);
        using var heard = new ManualResetEventSlim();
        using FixEndedSignal? signal = FixEndedSignal.Listen(
            FixEndedSignal.NameFor(Paths.Current), () => { h.Ui.Post(_ => h.Coordinator.RecheckProtectionAfterFix(), null); heard.Set(); }, TimeProvider.System, new CapturingLog());
        Assert.IsNotNull(signal);

        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("fix");
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(temp.File("fix-out.txt"));
        using (Process fix = Process.Start(info)!)
        {
            Assert.IsTrue(fix.WaitForExit(TimeSpan.FromSeconds(60)), "fix did not end.");
            Assert.AreEqual(ExitCodes.PlanOnly, fix.ExitCode);
        }

        Assert.IsTrue(heard.Wait(TimeSpan.FromSeconds(30)), "The tray was not told the run ended.");
        h.Pump();
        Assert.AreEqual(2, h.Recovery.Runs, "The start checks run again.");
        Assert.AreEqual(2, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    // The tray-started run raises the event and also ends as a process the tray watches: the tray hears twice and rechecks once.
    [TestMethod]
    public void TheEventAndTheEndOfTheProcessItStartedMakeOneRecheck()
    {
        var time = new ManualTime();
        int rechecks = 0;
        using FixEndedSignal? signal = FixEndedSignal.Listen(@"Local\Earshot.FixEnded.test-" + Guid.NewGuid().ToString("N"), () => Interlocked.Increment(ref rechecks), time, new CapturingLog());
        Assert.IsNotNull(signal);

        signal.Notify("the event");
        signal.Notify("the process");
        Assert.AreEqual(1, rechecks);

        time.Advance(FixEndedSignal.CoalesceWindow + TimeSpan.FromMilliseconds(1));
        signal.Notify("a later run");
        Assert.AreEqual(2, rechecks);
    }

    // The real launcher, against a harmless program that ends at once, in place of this program's own fix mode.
    [TestMethod]
    public void TheLauncherSaysWhenTheProcessItStartedHasEnded()
    {
        using var ended = new ManualResetEventSlim();
        string where = Path.Combine(Environment.SystemDirectory, "where.exe");
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => where);

        bool started = launcher.Start(new CapturingLog());

        Assert.IsTrue(started);
        Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(20)), "The end of the started process was not reported.");
    }
}
