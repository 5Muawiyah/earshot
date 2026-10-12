using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The real parts the tray starts with: the answer to a run that stopped half way, the window of a re-pair and the check for
// paired AirPods that are not the protected ones. They read; the only change any of them makes is the one normal block.
[TestClass]
public sealed class FixStartupTests
{
    [TestMethod]
    public async Task AStaleMarkerIsAnsweredWithOneBlockAtStartAndReportsThatItChangedTheNodes()
    {
        using var rig = new FixRig();
        bool alive = true;
        var marker = new FixRecovery(Path.Combine(Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N") + ".json"), rig.Gate.Time, _ => alive);
        marker.Begin("repairing");
        alive = false;
        var start = new FixRecoveryAtStart(marker, rig.Records, rig.GateFake, rig.Log);

        bool changed = await start.RunAsync(CancellationToken.None);

        Assert.IsTrue(changed);
        Expect.Same(rig.GateFake.Calls, "block");
        Assert.AreEqual(FixRecoveryState.None, marker.Read().State);
    }

    [TestMethod]
    public async Task NoMarkerAndAnOpenWindowBlockNothing()
    {
        using var rig = new FixRig();
        var start = new FixRecoveryAtStart(rig.Recovery, rig.Records, rig.GateFake, rig.Log);

        bool none = await start.RunAsync(CancellationToken.None);
        rig.Recovery.Begin("repairing");
        bool open = await start.RunAsync(CancellationToken.None);

        Assert.IsFalse(none);
        Assert.IsFalse(open);
        Assert.IsEmpty(rig.GateFake.Calls);
    }

    [TestMethod]
    public async Task ABlockThatFailsLeavesTheMarkerAndSaysSo()
    {
        using var rig = new FixRig();
        bool alive = true;
        var marker = new FixRecovery(Path.Combine(Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N") + ".json"), rig.Gate.Time, _ => alive);
        marker.Begin("repairing");
        alive = false;
        rig.GateFake.BlockResult = () => new FixGateResult(false, "no", [], 3);

        bool changed = await new FixRecoveryAtStart(marker, rig.Records, rig.GateFake, rig.Log).RunAsync(CancellationToken.None);

        Assert.IsFalse(changed);
        Assert.AreEqual(FixRecoveryState.Stale, marker.Read().State);
        Assert.IsTrue(rig.Log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn));
    }

    [TestMethod]
    public void TheRePairWindowIsOpenOnlyWhileTheMarkersRunIsAliveAndYoung()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        bool alive = true;
        var marker = new FixRecovery(temp.File("w.json"), time, _ => alive);
        var window = new FixMarkerRePairWindow(marker);

        Assert.IsFalse(window.IsOpen, "no marker");
        marker.Begin("repairing");
        Assert.IsTrue(window.IsOpen);
        alive = false;
        Assert.IsFalse(window.IsOpen, "the run has gone");
        alive = true;
        time.Advance(FixRecovery.WindowLength + TimeSpan.FromSeconds(1));
        Assert.IsFalse(window.IsOpen, "the window ran out");
        File.WriteAllText(temp.File("w.json"), "not json");
        Assert.IsFalse(window.IsOpen, "a damaged marker is not an open window");
    }

    [TestMethod]
    public async Task APairedContainerThatIsNotThePinnedOneIsCountedAndNothingIsChanged()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())));
        var check = new AirPodsProtectionCheck(rig.Nodes, rig.Records);

        ProtectionReading reading = await check.ReadAsync(CancellationToken.None);

        Assert.IsTrue(reading.Read);
        Assert.IsTrue(reading.Pinned);
        Assert.AreEqual(1, reading.Unprotected);
        Assert.IsTrue(reading.AnyUnprotected);
        Assert.IsEmpty(rig.Gate.Nodes.Calls, "No node was changed.");
        Assert.IsEmpty(rig.GateFake.Calls, "Nothing is adopted or blocked.");
        Assert.AreEqual(RecordedNodes.AirPodsContainer, rig.Records.ReadDevice().Value!.ContainerId, "The pin did not move.");
    }

    [TestMethod]
    public async Task ThePinnedPairAloneThePhoneAndAPairingThatWasRemovedAreNotCounted()
    {
        using (var alone = new FixRig())
        {
            Assert.AreEqual(0, (await new AirPodsProtectionCheck(alone.Nodes, alone.Records).ReadAsync(CancellationToken.None)).Unprotected);
        }

        using (var withPhone = new FixRig(RecordedNodes.TableWithHeadphones()))
        {
            Assert.AreEqual(0, (await new AirPodsProtectionCheck(withPhone.Nodes, withPhone.Records).ReadAsync(CancellationToken.None)).Unprotected);
        }

        FakeNodeApi table = RecordedNodes.Table();
        using var removed = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())));
        foreach (FakeNode n in FixRig.SecondAirPodsNodes())
        {
            removed.Gate.Nodes[n.InstanceId].Present = false;
        }

        Assert.AreEqual(0, (await new AirPodsProtectionCheck(removed.Nodes, removed.Records).ReadAsync(CancellationToken.None)).Unprotected);
    }

    [TestMethod]
    public async Task WithoutAPinTheReadingSaysSoAndTheCoordinatorShowsNothing()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())), pinned: false);

        ProtectionReading reading = await new AirPodsProtectionCheck(rig.Nodes, rig.Records).ReadAsync(CancellationToken.None);

        Assert.IsTrue(reading.Read);
        Assert.IsFalse(reading.Pinned);
        Assert.IsFalse(reading.AnyUnprotected, "Earshot has no pin, so there is no 'protected one' to be unlike.");
    }
}
