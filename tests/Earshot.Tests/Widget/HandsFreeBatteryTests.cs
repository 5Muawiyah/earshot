using System.Text;
using Earshot.Battery;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase4;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Windows' Hands-Free battery figure over a table of device nodes and association objects. The one read of the real
// machine is HandsFreeBatteryRealQueryTests; nothing here touches it.
[TestClass]
public sealed class HandsFreeBatteryTests
{
    private static readonly Guid Container = RecordedNodes.AirPodsContainer;
    private static readonly Guid OtherContainer = RecordedNodes.IPhoneContainer;
    private const string Address = RecordedNodes.AirPodsAddress;
    private static readonly string DeviceNode = RecordedNodes.AirPodsDeviceNode;

    // A table of nodes and objects: the container each node is in, the figure property it carries (or none).
    private sealed class TableReader : IBatterySweepReader
    {
        private readonly List<(string Id, Guid Container, (uint Type, byte[] Data)? Figure)> _nodes = [];
        private readonly List<DevObjectRecord> _containerObjects = [];
        private readonly List<DevObjectRecord> _endpoints = [];

        public uint ListResult { get; set; }

        public uint FigureReadResult { get; set; }

        public int ObjectsResult { get; set; }

        public int FigureReads { get; private set; }

        public int ObjectQueries { get; private set; }

        public List<string> FigureReadNodes { get; } = [];

        public TableReader Node(string id, Guid container, uint? type = null, byte[]? data = null)
        {
            _nodes.Add((id, container, type is null ? null : (type.Value, data ?? [])));
            return this;
        }

        public TableReader ContainerObject(Guid container, uint? type = null, byte[]? data = null)
        {
            var properties = new List<DevPropertyRecord>();
            if (type is not null)
            {
                properties.Add(new DevPropertyRecord(BatterySweep.HandsFreeBatteryKey, 0, type.Value, data ?? [], false));
            }

            _containerObjects.Add(new DevObjectRecord(DevQuery.DevObjectTypeAEPContainer, container.ToString("B"), properties));
            return this;
        }

        public TableReader Endpoint(string id, Guid container, uint? type = null, byte[]? data = null)
        {
            var properties = new List<DevPropertyRecord>
            {
                new(BatterySweep.AepContainerId, 0, CfgMgr32.DEVPROP_TYPE_GUID, container.ToByteArray(), false),
            };
            if (type is not null)
            {
                properties.Add(new DevPropertyRecord(BatterySweep.HandsFreeBatteryKey, 0, type.Value, data ?? [], false));
            }

            _endpoints.Add(new DevObjectRecord(DevQuery.DevObjectTypeAEP, id, properties));
            return this;
        }

        public uint ListDeviceIds(out string[] ids)
        {
            ids = ListResult == CfgMgr32.CR_SUCCESS ? _nodes.Select(n => n.Id).ToArray() : [];
            return ListResult;
        }

        public uint Locate(string instanceId, out uint devInst)
        {
            int index = _nodes.FindIndex(n => n.Id == instanceId);
            devInst = (uint)(index + 1);
            return index < 0 ? CfgMgr32.CR_NO_SUCH_DEVNODE : CfgMgr32.CR_SUCCESS;
        }

        public uint GetPropertyKeys(uint devInst, out DEVPROPKEY[] keys)
        {
            keys = [];
            return CfgMgr32.CR_SUCCESS;
        }

        public uint GetProperty(uint devInst, DEVPROPKEY key, out uint type, out byte[] data)
        {
            (string id, Guid container, (uint Type, byte[] Data)? figure) = _nodes[(int)devInst - 1];
            if (key.fmtid == CfgMgr32.DEVPKEY_Device_ContainerId.fmtid && key.pid == CfgMgr32.DEVPKEY_Device_ContainerId.pid)
            {
                type = CfgMgr32.DEVPROP_TYPE_GUID;
                data = container.ToByteArray();
                return CfgMgr32.CR_SUCCESS;
            }

            FigureReads++;
            FigureReadNodes.Add(id);
            type = 0;
            data = [];
            if (FigureReadResult != CfgMgr32.CR_SUCCESS)
            {
                return FigureReadResult;
            }

            if (figure is null)
            {
                return CfgMgr32.CR_NO_SUCH_VALUE;
            }

            (type, data) = figure.Value;
            return CfgMgr32.CR_SUCCESS;
        }

        public bool IsPresent(string instanceId) => true;

        public int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects)
        {
            ObjectQueries++;
            objects = ObjectsResult < 0 ? [] : objectType == DevQuery.DevObjectTypeAEP ? _endpoints : _containerObjects;
            return ObjectsResult;
        }
    }

    private static HandsFreeBatteryRead Read(TableReader reader, bool pairedObjects = true) =>
        new HandsFreeBatteryProvider(reader, () => Address, pairedObjects).Read(Container, Address);

    private static byte[] Byte(int v) => [(byte)v];

    [TestMethod]
    public void AFigureOnTheDevicesNodeIsTheFigureAndSaysWhereItCameFrom()
    {
        var reader = new TableReader().Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70));

        HandsFreeBatteryRead read = Read(reader);

        Assert.AreEqual(70, read.Percent);
        Assert.AreEqual("device node", read.Origin);
        Assert.IsNull(read.Note);
    }

    [TestMethod]
    public void ASixteenOrThirtyTwoBitFigureIsTakenToo()
    {
        var sixteen = new TableReader().Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_UINT16, BitConverter.GetBytes((ushort)55));
        var thirtyTwo = new TableReader().Node(DeviceNode, Container, CfgMgr32.DEVPROP_TYPE_UINT32, BitConverter.GetBytes(40u));

        Assert.AreEqual(55, Read(sixteen).Percent);
        Assert.AreEqual(40, Read(thirtyTwo).Percent);
    }

    [TestMethod]
    public void ZeroAndOneHundredAreFiguresAndOneHundredAndOneIsNot()
    {
        Assert.AreEqual(0, HandsFreeBatteryProvider.Accept(DevQuery.DEVPROP_TYPE_BYTE, Byte(0)));
        Assert.AreEqual(100, HandsFreeBatteryProvider.Accept(DevQuery.DEVPROP_TYPE_BYTE, Byte(100)));
        Assert.IsNull(HandsFreeBatteryProvider.Accept(DevQuery.DEVPROP_TYPE_BYTE, Byte(101)));
        Assert.IsNull(HandsFreeBatteryProvider.Accept(CfgMgr32.DEVPROP_TYPE_UINT32, BitConverter.GetBytes(250u)));
        Assert.IsNull(HandsFreeBatteryProvider.Accept(CfgMgr32.DEVPROP_TYPE_UINT32, BitConverter.GetBytes(uint.MaxValue)));
    }

    [TestMethod]
    public void AnotherTypeOrASizeThatDoesNotFitIsNoFigureAndIsRecordedByTypeAndSizeOnly()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container, CfgMgr32.DEVPROP_TYPE_STRING, Encoding.Unicode.GetBytes("70\0"))
            .ContainerObject(Container, DevQuery.DEVPROP_TYPE_UINT16, Byte(70)); // too short for a 16 bit value

        HandsFreeBatteryRead read = Read(reader);

        Assert.IsNull(read.Percent);
        StringAssert.Contains(read.Note, "type 0x12, 6 bytes, on a device node");
        StringAssert.Contains(read.Note, "type 0x5, 1 bytes, on a paired object");
        Assert.IsFalse(read.Note!.Contains("70", StringComparison.Ordinal), "The bytes themselves are never in the note.");
    }

    // Windows' query for the paired objects blocks for about a minute, so the minute-by-minute reads leave it out: they
    // read the nodes alone and never call it.
    [TestMethod]
    public void WithoutThePairedObjectsOnlyTheNodesAreReadAndTheObjectQueryIsNeverMade()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container)
            .ContainerObject(Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(80));

        HandsFreeBatteryRead read = Read(reader, pairedObjects: false);

        Assert.IsNull(read.Percent, "The figure on the object is not looked for.");
        Assert.AreEqual(0, reader.ObjectQueries, "The device query is never made.");
        StringAssert.EndsWith(read.Note, "on 1 device nodes.");
    }

    [TestMethod]
    public void AnEmptyPropertyIsNoFigureAndTheNoteCountsWhatWasRead()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container)
            .Node(RecordedNodes.AirPodsTargets[1], Container)
            .ContainerObject(Container);

        HandsFreeBatteryRead read = Read(reader);

        Assert.IsNull(read.Percent);
        StringAssert.StartsWith(read.Note, "No figure: the property was empty or absent on 2 device nodes and 1 paired objects");
    }

    [TestMethod]
    public void TwoDifferentFiguresAreNoFigureAndBothKindsAreNamed()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70))
            .ContainerObject(Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(60));

        HandsFreeBatteryRead read = Read(reader);

        Assert.IsNull(read.Percent, "Which is right cannot be known, so neither is taken.");
        StringAssert.Contains(read.Note, "device node and paired object");
    }

    [TestMethod]
    public void TheSameFigureOnTheNodeAndTheObjectIsThatFigure()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70))
            .ContainerObject(Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70));

        Assert.AreEqual(70, Read(reader).Percent);
    }

    [TestMethod]
    public void AFigureOnTheContainerObjectAloneIsTaken()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container)
            .ContainerObject(Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(80));

        HandsFreeBatteryRead read = Read(reader);

        Assert.AreEqual(80, read.Percent);
        Assert.AreEqual("paired object", read.Origin);
    }

    [TestMethod]
    public void AFigureOnAnEndpointThatNamesTheContainerIsTakenAndAnotherDevicesIsNot()
    {
        var reader = new TableReader()
            .Endpoint("endpoint-of-the-pinned-device", Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(65))
            .Endpoint("endpoint-of-another-device", OtherContainer, DevQuery.DEVPROP_TYPE_BYTE, Byte(10));

        Assert.AreEqual(65, Read(reader).Percent);
    }

    [TestMethod]
    public void AnotherDevicesNodeAndObjectNeverGiveAFigure()
    {
        var reader = new TableReader()
            .Node(DeviceNode, OtherContainer, DevQuery.DEVPROP_TYPE_BYTE, Byte(10))
            .ContainerObject(OtherContainer, DevQuery.DEVPROP_TYPE_BYTE, Byte(10));

        Assert.IsNull(Read(reader).Percent);
    }

    // Only the pinned device's own nodes are asked for the figure: a node that does not carry the address, or is in
    // another container, is never read for it.
    [TestMethod]
    public void OnlyTheNodesOfThePinnedDeviceAreAskedForTheFigure()
    {
        var reader = new TableReader()
            .Node(DeviceNode, Container)
            .Node(RecordedNodes.IPhoneDeviceNode, OtherContainer)
            .Node(RecordedNodes.RadioNode, NodeMatch.PcContainer);

        Read(reader);

        CollectionAssert.AreEqual(new[] { DeviceNode }, reader.FigureReadNodes.ToArray());
    }

    [TestMethod]
    public void NoPinnedDeviceReadsNothingAndSaysSo()
    {
        var reader = new TableReader().Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70));

        HandsFreeBatteryRead read = new HandsFreeBatteryProvider(reader, () => "").Read(Guid.Empty, "");

        Assert.IsNull(read.Percent);
        Assert.AreEqual("No pinned device.", read.Note);
        Assert.HasCount(1, read.Steps);
        Assert.IsFalse(read.Steps[0].Ok);
        Assert.AreEqual(0, reader.FigureReads, "Nothing was read.");
    }

    [TestMethod]
    public void EveryFailingStepKeepsItsRawCode()
    {
        var reader = new TableReader().Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70));
        reader.FigureReadResult = CfgMgr32.CR_FAILURE;
        reader.ObjectsResult = unchecked((int)0x80070005);

        HandsFreeBatteryRead read = Read(reader);

        Assert.IsNull(read.Percent);
        Assert.IsTrue(read.Steps.Any(s => s.Step == "hands-free-battery:cm-property" && s.CodeName == "CR_FAILURE" && !s.Ok));
        Assert.IsTrue(read.Steps.Any(s => s.Step == "hands-free-battery:dev-get-objects" && !s.Ok && s.Code == unchecked((int)0x80070005)));
    }

    [TestMethod]
    public void AFailedDeviceListIsStillTheObjectsAnswerWithTheRawCode()
    {
        var reader = new TableReader().ContainerObject(Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(90));
        reader.ListResult = CfgMgr32.CR_FAILURE;

        HandsFreeBatteryRead read = Read(reader);

        Assert.AreEqual(90, read.Percent, "The nodes could not be listed, the object could still be read.");
        Assert.IsTrue(read.Steps.Any(s => s.Step == "hands-free-battery:cm-list" && s.CodeName == "CR_FAILURE"));
    }

    // Through the contract every battery provider shares, as a caller that only knows the interface sees it.
    private static (bool HasSource, int? FigureForPinned, int? FigureForNone) ThroughTheContract(IBatteryProvider provider) =>
        (provider.HasSource, provider.Read(Container).Percent, provider.Read(Guid.Empty).Percent);

    [TestMethod]
    public void TheProviderIsTheBatteryContractAndHasASource()
    {
        var reader = new TableReader().Node(DeviceNode, Container, DevQuery.DEVPROP_TYPE_BYTE, Byte(70));

        (bool hasSource, int? pinned, int? none) = ThroughTheContract(new HandsFreeBatteryProvider(reader, () => Address, readPairedObjects: true));

        Assert.IsTrue(hasSource);
        Assert.AreEqual(70, pinned);
        Assert.IsNull(none);
    }

    [TestMethod]
    public void TheNullProviderStillHasNoSourceAndNeverAFigure()
    {
        (bool hasSource, int? pinned, int? none) = ThroughTheContract(new Earshot.Contracts.Null.NoBatterySource());

        Assert.IsFalse(hasSource);
        Assert.IsNull(pinned);
        Assert.IsNull(none);
    }
}
