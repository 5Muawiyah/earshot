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

    // A tray pairing that starts while the start checks wait for the tray's look: the card about unprotected AirPods is not shown
    // over it (its Fix button would be refused by the lock), and the card's once-per-run rule is not spent. Before, the pairing
    // flag was looked at only before the look.
    [TestMethod]
    public void TheNotProtectedCardIsNotShownWhileTheTrayPairs()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.Coordinator.StartLookHook = () =>
        {
            h.TrayPairing.Active = true;
            return Task.FromResult(false);
        };
        h.Start();
        h.Pump();

        Assert.AreEqual(0, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));

        h.TrayPairing.Active = false;
        h.Coordinator.CheckProtectionAfterTrayPairing("the pairing ended");
        h.Pump();

        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage), "Shown once the pairing has ended.");
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

    // A "not protected" card Windows held back (quiet time at sign-in) was not seen, so the once-per-run rule does not count it: the
    // next check offers it again. Before, the card was counted as shown when it was asked for.
    [TestMethod]
    public void ANotProtectedCardHeldBackIsOfferedAgainAtTheNextCheck()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.Cards.HoldBackNearTray = true;
        h.Start();
        Assert.AreEqual(1, h.Cards.HeldBack.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));

        h.Cards.HoldBackNearTray = false;
        DeviceSnapshot before = Devices.Idle(2);
        h.Monitor.Publish(before with { AllGroups = [.. before.AllGroups, new DeviceModel(Guid.NewGuid(), "AirPods", ConnectionState.Disconnected, [])] });
        h.Pump();
        h.Advance(BlockCoordinator.NewDeviceSettle);

        Assert.AreEqual(2, h.ProtectionScan.Reads);
        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    // The tray looks at whether the pinned pair is still paired when a new device group arrives. When that look found a stale pin and
    // told the person, the check for unprotected AirPods does not add a second card about the same pair.
    [TestMethod]
    public void ANewDeviceGroupAsksTheTraysPinnedPairLookFirstAndASaidSomethingAnswerSilencesTheOtherCard()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, false, 0);
        h.Start();
        int hooked = 0;
        h.Coordinator.NewDeviceHook = _ =>
        {
            hooked++;
            return Task.FromResult(true);
        };

        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        DeviceSnapshot before = Devices.Idle(2);
        h.Monitor.Publish(before with { AllGroups = [.. before.AllGroups, new DeviceModel(Guid.NewGuid(), "AirPods", ConnectionState.Disconnected, [])] });
        h.Pump();
        h.Advance(BlockCoordinator.NewDeviceSettle);

        Assert.AreEqual(1, hooked);
        Assert.AreEqual(1, h.ProtectionScan.Reads, "The tray said something about the pair, so the other check stays quiet.");
        Assert.AreEqual(0, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    // While the tray's Pair AirPods runs (or its look at a pairing that did not finish), a new device group is that run's to
    // answer, so the check for unprotected AirPods does not run, and a card about the same pair does not race the run's own.
    [TestMethod]
    public void ANewDeviceGroupWhileTheTraysPairingRunsIsLeftToThatRun()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        Assert.AreEqual(1, h.ProtectionScan.Reads);

        h.TrayPairing.Active = true;
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        DeviceSnapshot before = Devices.Idle(2);
        h.Monitor.Publish(before with { AllGroups = [.. before.AllGroups, new DeviceModel(Guid.NewGuid(), "AirPods", ConnectionState.Disconnected, [])] });
        h.Pump();
        h.Advance(BlockCoordinator.NewDeviceSettle);

        Assert.AreEqual(1, h.ProtectionScan.Reads);
        Assert.AreEqual(0, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    // A pairing that starts while the tray's pinned-pair look runs is seen too: the flag is read again after that look.
    [TestMethod]
    public void APairingThatStartsDuringTheTraysLookIsSeenAfterIt()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Coordinator.NewDeviceHook = _ =>
        {
            h.TrayPairing.Active = true;
            return Task.FromResult(false);
        };

        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        DeviceSnapshot before = Devices.Idle(2);
        h.Monitor.Publish(before with { AllGroups = [.. before.AllGroups, new DeviceModel(Guid.NewGuid(), "AirPods", ConnectionState.Disconnected, [])] });
        h.Pump();
        h.Advance(BlockCoordinator.NewDeviceSettle);

        Assert.AreEqual(1, h.ProtectionScan.Reads, "The pairing that started during the look answers for the new device.");
    }

    // At start, while the tray looks at a pairing that did not finish, the start check for unprotected AirPods waits; when that
    // look showed nothing, the tray asks for the check and it runs then.
    [TestMethod]
    public void TheStartCheckWaitsForTheTraysRecoveryAndRunsWhenAskedAfterIt()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.TrayPairing.Active = true;
        h.Start();

        Assert.AreEqual(0, h.ProtectionScan.Reads, "The start check waited for the tray's recovery.");
        Assert.AreEqual(0, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));

        h.Coordinator.CheckProtectionAfterTrayPairing("a test");
        h.Pump();
        Assert.AreEqual(0, h.ProtectionScan.Reads, "Not while the flag is still up.");

        h.TrayPairing.Active = false;
        h.Coordinator.CheckProtectionAfterTrayPairing("a test");
        h.Pump();

        Assert.AreEqual(1, h.ProtectionScan.Reads);
        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
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

        using var runtime = new RuntimeVariablesCleared();
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
