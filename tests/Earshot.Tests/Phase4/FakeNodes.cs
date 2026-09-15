using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Tests.Phase4;

// One devnode in the fake table.
internal sealed class FakeNode
{
    public FakeNode(string instanceId, Guid container, string? name = null)
    {
        InstanceId = instanceId;
        Container = container;
        Name = name;
    }

    public string InstanceId { get; }

    public Guid Container { get; }

    public string? Name { get; }

    public bool Present { get; set; } = true;

    public uint Status { get; set; } = CfgMgr32.DN_STARTED | CfgMgr32.DN_DISABLEABLE;

    public uint Problem { get; set; }

    public uint ConfigFlags { get; set; }

    // CONFIGRET a Disable call returns; the node changes only on CR_SUCCESS.
    public uint DisableResult { get; set; }

    public uint EnableResult { get; set; }

    public uint ContainerReadResult { get; set; }

    public uint StatusReadResult { get; set; }

    public bool IsDisabled => (Status & CfgMgr32.DN_HAS_PROBLEM) != 0 && Problem == CfgMgr32.CM_PROB_DISABLED;

    public void MarkDisabled(bool persistent)
    {
        Status |= CfgMgr32.DN_HAS_PROBLEM;
        Problem = CfgMgr32.CM_PROB_DISABLED;
        ConfigFlags = persistent ? ConfigFlags | CfgMgr32.CONFIGFLAG_DISABLED : ConfigFlags & ~CfgMgr32.CONFIGFLAG_DISABLED;
    }
}

internal sealed record NodeCall(string Kind, string InstanceId, uint Flags);

// An in-memory CfgMgr32. Devinst values are the table index plus one; a non-present node can be located only
// with the phantom flag. Every change is recorded so tests can assert exactly what the gate called.
internal sealed class FakeNodeApi : INodeApi
{
    private readonly List<FakeNode> _nodes;

    public FakeNodeApi(IEnumerable<FakeNode> nodes)
    {
        _nodes = nodes.ToList();
    }

    public List<NodeCall> Calls { get; } = new();

    public uint ListResult { get; set; }

    public IReadOnlyList<FakeNode> Nodes => _nodes;

    public FakeNode this[string instanceId] => _nodes.Single(n => n.InstanceId == instanceId);

    public uint ListDeviceIds(out string[] ids)
    {
        ids = ListResult == CfgMgr32.CR_SUCCESS ? _nodes.Select(n => n.InstanceId).ToArray() : [];
        return ListResult;
    }

    public uint Locate(string instanceId, bool includeNonPresent, out uint devInst)
    {
        int index = _nodes.FindIndex(n => string.Equals(n.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        devInst = 0;
        if (index < 0 || (!includeNonPresent && !_nodes[index].Present))
        {
            return CfgMgr32.CR_NO_SUCH_DEVNODE;
        }

        devInst = (uint)index + 1;
        return CfgMgr32.CR_SUCCESS;
    }

    public uint GetStatus(uint devInst, out uint status, out uint problem)
    {
        FakeNode node = Node(devInst);
        status = 0;
        problem = 0;
        if (!node.Present)
        {
            return CfgMgr32.CR_NO_SUCH_DEVNODE;
        }

        if (node.StatusReadResult != CfgMgr32.CR_SUCCESS)
        {
            return node.StatusReadResult;
        }

        status = node.Status;
        problem = node.Problem;
        return CfgMgr32.CR_SUCCESS;
    }

    public uint GetContainerId(uint devInst, out Guid containerId)
    {
        FakeNode node = Node(devInst);
        containerId = node.ContainerReadResult == CfgMgr32.CR_SUCCESS ? node.Container : Guid.Empty;
        return node.ContainerReadResult;
    }

    public uint GetConfigFlags(uint devInst, out uint configFlags)
    {
        configFlags = Node(devInst).ConfigFlags;
        return CfgMgr32.CR_SUCCESS;
    }

    public uint GetName(uint devInst, out string? name)
    {
        name = Node(devInst).Name;
        return name is null ? CfgMgr32.CR_NO_SUCH_VALUE : CfgMgr32.CR_SUCCESS;
    }

    public uint Disable(uint devInst, uint flags)
    {
        FakeNode node = Node(devInst);
        Calls.Add(new NodeCall("disable", node.InstanceId, flags));
        if (node.DisableResult == CfgMgr32.CR_SUCCESS)
        {
            node.MarkDisabled((flags & CfgMgr32.CM_DISABLE_PERSIST) != 0);
        }

        return node.DisableResult;
    }

    public uint Enable(uint devInst)
    {
        FakeNode node = Node(devInst);
        Calls.Add(new NodeCall("enable", node.InstanceId, 0));
        if (node.EnableResult == CfgMgr32.CR_SUCCESS)
        {
            node.Status &= ~CfgMgr32.DN_HAS_PROBLEM;
            node.Problem = 0;
            node.ConfigFlags &= ~CfgMgr32.CONFIGFLAG_DISABLED;
        }

        return node.EnableResult;
    }

    private FakeNode Node(uint devInst) => _nodes[(int)devInst - 1];
}

// The device-node set recorded on the owner's PC during phase 0 (the same ids as the NodeMatch safety test):
// the AirPods container, the paired iPhone in its own container and the Bluetooth radio in the PC container.
internal static class RecordedNodes
{
    public const string AirPodsAddress = "5A6B7C8D9EAF";
    public const string IPhoneAddress = "3410BE0E0ABB";

    public static readonly Guid AirPodsContainer = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
    public static readonly Guid IPhoneContainer = new("4FB94536-5965-549C-A947-0B115F3D9B56");

    public const string AirPodsDeviceNode = @"BTHENUM\DEV_5A6B7C8D9EAF\b&1a2b3c4d&0&BLUETOOTHDEVICE_5A6B7C8D9EAF";
    public const string IPhoneDeviceNode = @"BTHENUM\Dev_3410BE0E0ABB\b&1a2b3c4d&0&BluetoothDevice_3410BE0E0ABB";
    public const string RadioNode = @"BTH\MS_BTHBRB\a&3c4d5e6&0&1";

    public static readonly string[] AirPodsTargets =
    [
        @"BTHENUM\{00001000-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{00001801-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{4715650B-5E9D-4AC2-B898-A4FC0AA5DF78}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{0000110C-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        AirPodsDeviceNode,
        @"BTHENUM\{0000110E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
        @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000",
    ];

    public static readonly string[] AirPodsNonTargets =
    [
        @"BTHHFENUM\BTHHFPAUDIO\c&2b3c4d5e&1&97",
        @"SWD\MMDEVAPI\{0.0.1.00000000}.{0B46D234-B82D-4B72-B995-E8E3CA2937C9}",
        @"SWD\MMDEVAPI\{0.0.0.00000000}.{A2901F31-DC17-41B7-B0AD-2F77A5F04490}",
        @"SWD\MMDEVAPI\{0.0.0.00000000}.{6D6E788A-3608-4EF8-8B08-08DB2F516970}",
    ];

    public static readonly string[] IPhoneNodes =
    [
        IPhoneDeviceNode,
        @"BTHENUM\{00000000-deca-fade-deca-deafdecacafe}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{00001000-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{0000110a-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{0000110c-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{0000110e-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{00001116-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{0000111f-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{0000112f-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{00001132-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{00001801-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{02030302-1d19-415f-86f2-22a2106a0a77}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHENUM\{1ff31936-572e-4b36-a2bf-b2409b1aa6f4}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000",
        @"BTHHFENUM\BTHHFPAUDIO\c&4d5e6f7&0&97",
    ];

    public static IEnumerable<string> AllIds() => AirPodsTargets.Concat(AirPodsNonTargets).Concat(IPhoneNodes).Append(RadioNode);

    // A fresh table. The AirPods audio endpoints are non-present when the AirPods are not connected, as
    // phase 0 observed; every BTHENUM node is present and enabled.
    public static FakeNodeApi Table()
    {
        var nodes = new List<FakeNode>();
        nodes.AddRange(AirPodsTargets.Select(id => new FakeNode(id, AirPodsContainer, id == AirPodsDeviceNode ? "Owner’s AirPods Pro" : null)));
        nodes.AddRange(AirPodsNonTargets.Select(id => new FakeNode(id, AirPodsContainer) { Present = !id.StartsWith(@"SWD\", StringComparison.Ordinal) }));
        nodes.AddRange(IPhoneNodes.Select(id => new FakeNode(id, IPhoneContainer, id == IPhoneDeviceNode ? "iPhone" : null)));
        nodes.Add(new FakeNode(RadioNode, NodeMatch.PcContainer));
        return new FakeNodeApi(nodes);
    }

    public static DeviceIdentity AirPods() => new() { Address = AirPodsAddress, ContainerId = AirPodsContainer };

    public static DeviceIdentity IPhone() => new() { Address = IPhoneAddress, ContainerId = IPhoneContainer };
}
