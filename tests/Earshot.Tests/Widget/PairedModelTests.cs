using Earshot.Battery;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase4;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class PairedModelTests
{
    // An invented service node of the invented device in RecordedNodes, with its vendor and product id text swapped
    // for the given one.
    private static string Id(string vidAndPid) =>
        RecordedNodes.AirPodsTargets.First(t => t.Contains("_VID&0001004C_PID&2027", StringComparison.Ordinal))
            .Replace("VID&0001004C_PID&2027", vidAndPid, StringComparison.Ordinal);

    [TestMethod]
    public void TheProductIdIsReadFromAnAppleNodesInstanceId()
    {
        Assert.AreEqual((ushort)0x2027, PairedModelParser.ParseModel(Id("VID&0001004C_PID&2027")));
        Assert.AreEqual((ushort)0x200E, PairedModelParser.ParseModel(Id("vid&0001004c_pid&200e")));
    }

    [TestMethod]
    public void ANodeWithNoAppleProductIdGivesNoModel()
    {
        string[] ids =
        [
            RecordedNodes.AirPodsDeviceNode,
            Id("VID&0001004c_PID&0000"),
            Id("VID&0002004c_PID&2027"),
            Id("VID&0001004d_PID&2027"),
            Id("VID&0001004c_PID&20270"),
            "",
        ];

        foreach (string id in ids)
        {
            Assert.IsNull(PairedModelParser.ParseModel(id), id);
        }
    }

    [TestMethod]
    public void ThePinnedDevicesNodesGiveItsModel()
    {
        FakeNodeApi table = RecordedNodes.Table();

        PairedModelRead read = new NodePairedModelSource(table).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.AreEqual((ushort)0x2027, read.Model);
        Assert.IsEmpty(read.Steps);
    }

    // The iPhone's nodes carry a product id of 0000, and in any case another container: pinned to it, no model is
    // read from them.
    [TestMethod]
    public void AnotherDevicesNodesAreNeverTheModel()
    {
        FakeNodeApi table = RecordedNodes.Table();

        PairedModelRead read = new NodePairedModelSource(table).Read(RecordedNodes.IPhoneContainer, RecordedNodes.IPhoneAddress);

        Assert.IsNull(read.Model);
    }

    [TestMethod]
    public void NoPinnedDeviceReadsAsNoModelAndSaysSo()
    {
        FakeNodeApi table = RecordedNodes.Table();

        PairedModelRead read = new NodePairedModelSource(table).Read(Guid.Empty, "");

        Assert.IsNull(read.Model);
        Assert.HasCount(1, read.Steps);
        Assert.IsFalse(read.Steps[0].Ok);
    }

    [TestMethod]
    public void AFailedListGivesNoModelAndTheRawCode()
    {
        FakeNodeApi table = RecordedNodes.Table();
        table.ListResult = CfgMgr32.CR_FAILURE;

        PairedModelRead read = new NodePairedModelSource(table).Read(RecordedNodes.AirPodsContainer, RecordedNodes.AirPodsAddress);

        Assert.IsNull(read.Model);
        Assert.IsTrue(read.Steps.Any(s => s.CodeName == "CR_FAILURE" && !s.Ok), "The raw code is kept.");
    }

    [TestMethod]
    public void TheModelOnTheWireIsTheProductIdLittleEndian()
    {
        // The product id 0x2027 travels as 0x27 then 0x20.
        var message = new ProximityMessage(ModelHigh: 0x27, ModelLow: 0x20, 0, 0, 0, 0, 0, 0);

        Assert.AreEqual((ushort)0x2027, message.Model);
    }
}
