using Earshot.App;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Connect in Windows before Earshot is set up. Nothing of Earshot's blocks the nodes then, so there is nothing to allow: the
// button opens Windows' Bluetooth settings, or, with no AirPods paired at all, takes the person to Pair AirPods. It used to
// refuse with a message about the boot block.
[TestClass]
public sealed class ConnectInWindowsTests
{
    private static readonly string[] AllowOnly = ["allow"];

    private static CoordinatorHarness NotSetUp(PairedAirPodsReading paired)
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.NotSetUp();
        h.Monitor.Set(Devices.NotPresent(1));
        h.PairedAirPods.Reading = paired;
        h.Start();
        return h;
    }

    private static ToggleReport Done(CoordinatorHarness h, Task<ToggleReport> task)
    {
        h.Pump();
        for (int i = 0; i < 400 && !task.IsCompleted; i++)
        {
            h.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.IsTrue(task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NotSetUpOpensSettingsWithoutAnAllow()
    {
        using CoordinatorHarness h = NotSetUp(new PairedAirPodsReading(true, 1));

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsTrue(report.IsSuccess, report.UserMessage);
        Assert.AreEqual("Bluetooth settings is open. Connect your AirPods there.", report.UserMessage);
        Assert.AreEqual(1, h.Launcher.Opened);
        Assert.IsEmpty(h.Block.GateVerbCalls, "Nothing is allowed: no gate exists.");
        Assert.IsFalse(h.Coordinator.WindowsConnectOpen, "There is no idle rule before set-up, so no window to keep it off.");
        Assert.IsFalse(report.StartPairAirPods);
        ConnectHistoryEntry entry = h.History.Read().Single();
        Assert.AreEqual(ConnectHistoryActions.ConnectInWindows, entry.Action);
        Assert.AreEqual("not-set-up", entry.StartState);
        Assert.AreEqual("windows", entry.Path);
        Assert.AreEqual(ConnectHistoryOutcomes.Allowed, entry.Outcome);
        Assert.AreEqual(FailureClass.None.ToString(), entry.FailureClass, "It is no longer a PC-side failure.");
        Assert.IsFalse(entry.Steps.Any(s => s.Name.Contains("allow", StringComparison.Ordinal)), "No gate step.");
    }

    [TestMethod]
    public void NotSetUpWithNoAirPodsStartsPairAirPods()
    {
        using CoordinatorHarness h = NotSetUp(new PairedAirPodsReading(true, 0));

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsTrue(report.StartPairAirPods);
        Assert.IsFalse(report.IsSuccess);
        Assert.AreEqual(0, h.Launcher.Opened, "Windows' settings are not opened for AirPods that are not paired.");
        Assert.IsEmpty(h.Block.GateVerbCalls);
        Assert.IsEmpty(h.Cards.Statuses.Where(s => s.Contains("boot block", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void NotSetUpWhenThePairedListCannotBeReadStillOpensSettings()
    {
        using CoordinatorHarness h = NotSetUp(PairedAirPodsReading.Unknown);

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsTrue(report.IsSuccess, report.UserMessage);
        Assert.AreEqual(1, h.Launcher.Opened);
        Assert.IsFalse(report.StartPairAirPods, "Not knowing is never a reason to say nothing is paired.");
    }

    [TestMethod]
    public void AnUnknownStateKeepsItsRefusal()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Unknown();
        h.Monitor.Set(Devices.NotPresent(1));
        h.PairedAirPods.Reading = new PairedAirPodsReading(true, 1);
        h.Start();

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsFalse(report.IsSuccess);
        Assert.AreEqual(0, h.Launcher.Opened);
    }

    [TestMethod]
    public void ASetUpPcStillAllowsFirstAsBefore()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Start();

        ToggleReport report = Done(h, h.Coordinator.ConnectInWindowsAsync(CoordinatorHarness.Request(true)));

        Assert.IsTrue(report.IsSuccess);
        CollectionAssert.AreEqual(AllowOnly, h.Block.GateVerbCalls);
        Assert.IsTrue(h.Coordinator.WindowsConnectOpen);
    }
}
