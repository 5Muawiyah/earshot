using Earshot.App;
using Earshot.Interop;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase4;
using Earshot.Tests.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Whether any AirPods are paired with this PC, read from the device nodes. Reads only: nothing is located for change, enabled or
// disabled, and a pairing that was removed leaves nodes behind that are not a pairing.
[TestClass]
public sealed class NodePairedAirPodsCheckTests
{
    [TestMethod]
    public async Task APairWithItsDeviceNodeInTheTreeIsCounted()
    {
        using var rig = new FixRig();

        PairedAirPodsReading reading = await new NodePairedAirPodsCheck(rig.Nodes).ReadAsync(CancellationToken.None);

        Assert.IsTrue(reading.Read);
        Assert.AreEqual(1, reading.Count);
        Assert.IsFalse(reading.NonePaired);
        Assert.IsEmpty(rig.Gate.Nodes.Calls, "Nothing was changed.");
    }

    [TestMethod]
    public async Task TwoPairsAreCountedTwice()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())));

        PairedAirPodsReading reading = await new NodePairedAirPodsCheck(rig.Nodes).ReadAsync(CancellationToken.None);

        Assert.AreEqual(2, reading.Count);
    }

    [TestMethod]
    public async Task APairingThatWasRemovedLeavesNodesBehindAndIsNotCounted()
    {
        using var rig = new FixRig();
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Gate.Nodes[id].Present = false;
        }

        PairedAirPodsReading reading = await new NodePairedAirPodsCheck(rig.Nodes).ReadAsync(CancellationToken.None);

        Assert.IsTrue(reading.Read);
        Assert.AreEqual(0, reading.Count);
        Assert.IsTrue(reading.NonePaired);
    }

    [TestMethod]
    public async Task OnlyAPhoneMeansNoneArePaired()
    {
        using var rig = new FixRig(RecordedNodes.TableWithHeadphones());
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Gate.Nodes[id].Present = false;
        }

        PairedAirPodsReading reading = await new NodePairedAirPodsCheck(rig.Nodes).ReadAsync(CancellationToken.None);

        Assert.IsTrue(reading.NonePaired);
    }

    [TestMethod]
    public async Task AFailedListIsUnknownNeverNone()
    {
        using var rig = new FixRig();
        rig.Gate.Nodes.ListResult = CfgMgr32.CR_FAILURE;

        PairedAirPodsReading reading = await new NodePairedAirPodsCheck(rig.Nodes).ReadAsync(CancellationToken.None);

        Assert.IsFalse(reading.Read);
        Assert.IsFalse(reading.NonePaired, "Not knowing is never a reason to say nothing is paired.");
    }
}
