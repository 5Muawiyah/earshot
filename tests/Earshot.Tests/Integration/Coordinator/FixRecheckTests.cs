using System.Diagnostics;
using Earshot.App;
using Earshot.Contracts;
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
