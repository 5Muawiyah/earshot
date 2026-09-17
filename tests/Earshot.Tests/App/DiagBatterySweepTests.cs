using System.Text;
using System.Text.Json;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The battery sweep over a table of device nodes and association endpoints. No test reads the real device tree
// or runs a device query: diag runs only for the owner.
[TestClass]
public sealed class DiagBatterySweepTests
{
    private static readonly Guid AirPodsContainer = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
    private static readonly Guid PhoneContainer = new("4FB94536-5965-549C-A947-0B115F3D9B56");
    private static readonly DEVPROPKEY BatteryKey = new(new Guid(Earshot.BatterySweep.BatteryKeySet), 2);
    private static readonly DEVPROPKEY BatteryLife = new(new Guid("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);
    private static readonly DEVPROPKEY AepContainerId = new(new Guid("E7C3FB29-CAA7-4F47-8C8B-BE59B330D4C5"), 2);
    private static readonly string[] MatchedNodes = [@"BTHENUM\DEV_5A6B7C8D9EAF\7&1", @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2024\7&2"];

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s + "\0");

    private static FakeSweepReader Machine()
    {
        var reader = new FakeSweepReader();
        reader.Node(@"BTHENUM\DEV_5A6B7C8D9EAF\7&1", present: false, friendly: "Owner’s AirPods Pro", container: AirPodsContainer)
              .Set(BatteryKey, DevQuery.DEVPROP_TYPE_BYTE, [0x55]);
        reader.Node(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2024\7&2", present: false, friendly: null, container: AirPodsContainer);
        reader.Node(@"BTHENUM\DEV_3410BE0E0ABB\7&3", present: true, friendly: "iPhone", container: PhoneContainer);
        reader.Node(@"BTH\MS_BTHBRB\a&3c4d5e6&0&1", present: true, friendly: "Microsoft Bluetooth Enumerator", container: NodeMatch.PcContainer);

        reader.Aep("Bluetooth#Bluetooth20:0b:74:41:fe:d2-5A:6b:7C:8d:9E:af", "Owner’s AirPods Pro", AirPodsContainer);
        reader.Aep("Bluetooth#Bluetooth20:0b:74:41:fe:d2-34:10:be:0e:0a:bb", "iPhone", PhoneContainer);
        return reader;
    }

    [TestMethod]
    public void TheSweepReadsTheMatchedNodesAndEndpointsAndPassesItsControls()
    {
        FakeSweepReader reader = Machine();

        BatterySweepReport report = Earshot.BatterySweep.Run(reader, "AirPods", AirPodsContainer, TimeProvider.System);

        CollectionAssert.AreEquivalent(MatchedNodes, report.Nodes.Select(n => n.InstanceId).ToArray(), "The name match and the pinned container, never the phone or the radio.");
        Assert.AreEqual("friendly name", report.Nodes.Single(n => n.InstanceId == MatchedNodes[0]).MatchedBy);
        Assert.AreEqual("pinned container", report.Nodes.Single(n => n.InstanceId == MatchedNodes[1]).MatchedBy);
        Assert.IsTrue(report.NodeControlPassed);
        Assert.IsTrue(report.EndpointControlPassed);

        SweepObjectQuery aeps = report.Objects.Single(q => q.Kind == "aep");
        Assert.AreEqual(2, aeps.Paired);
        Assert.AreEqual(2, aeps.WithPairedKey);
        Assert.AreEqual("Bluetooth#Bluetooth20:0b:74:41:fe:d2-5A:6b:7C:8d:9E:af", aeps.Matched.Single().Id);

        SweepProperty value = report.WatchedValues.Single();
        Assert.AreEqual("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", value.Key);
        Assert.AreEqual("type 0x3, 85 (0x55)", value.Value, "Recorded as read.");
        Assert.AreEqual(ExitCodes.Ok, Earshot.BatterySweep.ExitCode(report, evidenceSaved: true));
    }

    [TestMethod]
    public void TheEvidenceHoldsEveryKeyAndOnlyWatchedValues()
    {
        BatterySweepReport report = Earshot.BatterySweep.Run(Machine(), "AirPods", AirPodsContainer, TimeProvider.System);

        using JsonDocument doc = JsonDocument.Parse(Earshot.BatterySweep.ToJson(report));
        JsonElement root = doc.RootElement;
        Assert.AreEqual("battery-sweep", root.GetProperty("target").GetString());
        JsonElement first = root.GetProperty("deviceNodes").GetProperty("matched")[0];
        JsonElement[] properties = first.GetProperty("properties").EnumerateArray().ToArray();
        Assert.IsTrue(properties.Any(p => p.GetProperty("key").GetString() == "{8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C} 2"), "The container id key is listed.");
        Assert.IsTrue(properties.Where(p => !p.TryGetProperty("watched", out _)).All(p => !p.TryGetProperty("value", out _)),
            "Only a watched key carries its value.");
        Assert.AreEqual(1, root.GetProperty("watchedValues").GetArrayLength());

        string summary = string.Join("\n", Earshot.BatterySweep.Summary(report));
        Assert.Contains("Battery keys with a value: 1, see the evidence", summary);
        Assert.DoesNotContain("%", summary, "The summary never prints a percentage.");
    }

    [TestMethod]
    public void NothingFoundWithBothControlsIsAnAnswer()
    {
        FakeSweepReader reader = Machine();
        reader.Remove(@"BTHENUM\DEV_5A6B7C8D9EAF\7&1", BatteryKey);

        BatterySweepReport report = Earshot.BatterySweep.Run(reader, "AirPods", AirPodsContainer, TimeProvider.System);

        Assert.IsEmpty(report.WatchedValues);
        Assert.AreEqual(ExitCodes.Ok, Earshot.BatterySweep.ExitCode(report, evidenceSaved: true));
        Assert.Contains("Battery keys with a value: none", string.Join("\n", Earshot.BatterySweep.Summary(report)));
    }

    [TestMethod]
    public void AFailedEndpointQueryProvesNothing()
    {
        FakeSweepReader reader = Machine();
        reader.ObjectsHr = unchecked((int)0x80070005);

        BatterySweepReport report = Earshot.BatterySweep.Run(reader, "AirPods", AirPodsContainer, TimeProvider.System);

        Assert.IsFalse(report.EndpointControlPassed);
        Assert.AreEqual(ExitCodes.Unavailable, Earshot.BatterySweep.ExitCode(report, evidenceSaved: true));
        Assert.IsTrue(report.Steps.Any(s => s.Step == "dev-get-objects:aep" && !s.Ok));
    }

    [TestMethod]
    public void NoMatchedNodeProvesNothingAndAFailedListIsAnOsError()
    {
        BatterySweepReport unmatched = Earshot.BatterySweep.Run(Machine(), "Beats", Guid.Empty, TimeProvider.System);
        Assert.IsEmpty(unmatched.Nodes);
        Assert.AreEqual(ExitCodes.Unavailable, Earshot.BatterySweep.ExitCode(unmatched, evidenceSaved: true));

        FakeSweepReader broken = Machine();
        broken.ListResult = CfgMgr32.CR_FAILURE;
        BatterySweepReport failed = Earshot.BatterySweep.Run(broken, "AirPods", AirPodsContainer, TimeProvider.System);
        Assert.AreEqual(ExitCodes.OsError, Earshot.BatterySweep.ExitCode(failed, evidenceSaved: true));
        Assert.AreEqual(ExitCodes.IoError, Earshot.BatterySweep.ExitCode(failed, evidenceSaved: false));
    }

    [TestMethod]
    public void TheWatchedKeysAreTheBatteryOnes()
    {
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(BatteryKey));
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(new DEVPROPKEY(BatteryKey.fmtid, 7)), "Any key in the phase 0 set.");
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(BatteryLife));
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(new DEVPROPKEY(BatteryLife.fmtid, 22)));
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(new DEVPROPKEY(BatteryLife.fmtid, 23)));
        Assert.IsTrue(Earshot.BatterySweep.IsWatched(new DEVPROPKEY(new Guid("C4C07F2B-8524-4E66-AE3A-A6235F103BEB"), 2)));
        Assert.IsFalse(Earshot.BatterySweep.IsWatched(new DEVPROPKEY(BatteryLife.fmtid, 11)));
        Assert.IsFalse(Earshot.BatterySweep.IsWatched(CfgMgr32.DEVPKEY_Device_ContainerId));
        Assert.IsFalse(Earshot.BatterySweep.IsWatched(DevQuery.PKEY_Devices_Aep_IsPaired));
    }

    [TestMethod]
    [DataRow(0x3u, new byte[] { 0x55 }, "type 0x3, 85 (0x55)")]
    [DataRow(0x7u, new byte[] { 0x64, 0, 0, 0 }, "type 0x7, 100 (0x64000000)")]
    [DataRow(0x11u, new byte[] { 0xFF }, "type 0x11, true (0xFF)")]
    [DataRow(0x0u, new byte[0], "type 0x0, empty")]
    [DataRow(0x1003u, new byte[] { 1, 2 }, "type 0x1003, 0x0102")]
    public void AValueIsDescribedAsRead(uint type, byte[] data, string expected)
    {
        Assert.AreEqual(expected, Earshot.BatterySweep.DescribeValue(type, data));
    }

    private sealed class FakeSweepReader : IBatterySweepReader
    {
        private readonly List<(string Id, bool Present, Dictionary<(Guid, uint), (uint Type, byte[] Data)> Properties)> _nodes = [];
        private readonly List<DevObjectRecord> _aeps = [];
        private readonly List<DevObjectRecord> _containers = [];

        public uint ListResult { get; set; }

        public int ObjectsHr { get; set; }

        public NodeBuilder Node(string id, bool present, string? friendly, Guid container)
        {
            var properties = new Dictionary<(Guid, uint), (uint, byte[])>
            {
                [(CfgMgr32.DEVPKEY_Device_ContainerId.fmtid, CfgMgr32.DEVPKEY_Device_ContainerId.pid)] = (CfgMgr32.DEVPROP_TYPE_GUID, container.ToByteArray()),
            };
            if (friendly is not null)
            {
                properties[(CfgMgr32.DEVPKEY_Device_FriendlyName.fmtid, CfgMgr32.DEVPKEY_Device_FriendlyName.pid)] = (CfgMgr32.DEVPROP_TYPE_STRING, Utf16(friendly));
            }

            _nodes.Add((id, present, properties));
            return new NodeBuilder(properties);
        }

        public void Remove(string id, DEVPROPKEY key) => _nodes.Single(n => n.Id == id).Properties.Remove((key.fmtid, key.pid));

        public void Aep(string id, string name, Guid container)
        {
            DevPropertyRecord[] properties =
            [
                new(CfgMgr32.DEVPKEY_NAME, 0, CfgMgr32.DEVPROP_TYPE_STRING, Utf16(name), false),
                new(DevQuery.PKEY_Devices_Aep_IsPaired, 0, CfgMgr32.DEVPROP_TYPE_BOOLEAN, [0xFF], false),
                new(AepContainerId, 0, CfgMgr32.DEVPROP_TYPE_GUID, container.ToByteArray(), false),
            ];
            _aeps.Add(new DevObjectRecord(DevQuery.DevObjectTypeAEP, id, properties));
            _containers.Add(new DevObjectRecord(DevQuery.DevObjectTypeAEPContainer, container.ToString("B"),
                [new(CfgMgr32.DEVPKEY_NAME, 0, CfgMgr32.DEVPROP_TYPE_STRING, Utf16(name), false),
                 new(DevQuery.PKEY_Devices_AepContainer_IsPaired, 0, CfgMgr32.DEVPROP_TYPE_BOOLEAN, [0xFF], false)]));
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
            keys = _nodes[(int)devInst - 1].Properties.Keys.Select(k => new DEVPROPKEY(k.Item1, k.Item2)).ToArray();
            return CfgMgr32.CR_SUCCESS;
        }

        public uint GetProperty(uint devInst, DEVPROPKEY key, out uint type, out byte[] data)
        {
            if (_nodes[(int)devInst - 1].Properties.TryGetValue((key.fmtid, key.pid), out (uint Type, byte[] Data) value))
            {
                (type, data) = value;
                return CfgMgr32.CR_SUCCESS;
            }

            type = 0;
            data = [];
            return CfgMgr32.CR_NO_SUCH_VALUE;
        }

        public bool IsPresent(string instanceId) => _nodes.Single(n => n.Id == instanceId).Present;

        public int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects)
        {
            objects = ObjectsHr < 0 ? [] : objectType == DevQuery.DevObjectTypeAEP ? _aeps : _containers;
            return ObjectsHr;
        }
    }

    private sealed class NodeBuilder(Dictionary<(Guid, uint), (uint, byte[])> properties)
    {
        public NodeBuilder Set(DEVPROPKEY key, uint type, byte[] data)
        {
            properties[(key.fmtid, key.pid)] = (type, data);
            return this;
        }
    }
}
