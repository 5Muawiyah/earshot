using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot;

// Read-only CfgMgr32 access to device nodes. The tray, the probe and the gate all read through this
// interface; only the gate's INodeApi adds disable and enable. Every method returns the CONFIGRET.
internal interface INodeReader
{
    // Every devnode instance id, present or not (CM_GETIDLIST_FILTER_NONE), retried on CR_BUFFER_SMALL.
    uint ListDeviceIds(out string[] ids);

    // CM_LOCATE_DEVNODE_NORMAL finds only nodes in the tree now; PHANTOM also finds non-present ones.
    uint Locate(string instanceId, bool includeNonPresent, out uint devInst);

    // CM_Get_DevNode_Status. A non-present node gives CR_NO_SUCH_DEVNODE.
    uint GetStatus(uint devInst, out uint status, out uint problem);

    uint GetContainerId(uint devInst, out Guid containerId);

    // DEVPKEY_Device_ConfigFlags. CR_NO_SUCH_VALUE when the node has none.
    uint GetConfigFlags(uint devInst, out uint configFlags);

    // DEVPKEY_NAME.
    uint GetName(uint devInst, out string? name);
}

// The real reader. Nothing here changes a node.
internal class CfgMgr32NodeReader : INodeReader
{
    public uint ListDeviceIds(out string[] ids) =>
        CfgMgr32.GetDeviceIdList(null, CfgMgr32.CM_GETIDLIST_FILTER_NONE, out ids);

    public uint Locate(string instanceId, bool includeNonPresent, out uint devInst) =>
        CfgMgr32.LocateDevNode(
            instanceId,
            includeNonPresent ? CfgMgr32.CM_LOCATE_DEVNODE_PHANTOM : CfgMgr32.CM_LOCATE_DEVNODE_NORMAL,
            out devInst);

    public uint GetStatus(uint devInst, out uint status, out uint problem) =>
        CfgMgr32.CM_Get_DevNode_Status(out status, out problem, devInst, 0);

    public uint GetContainerId(uint devInst, out Guid containerId)
    {
        containerId = Guid.Empty;
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, CfgMgr32.DEVPKEY_Device_ContainerId, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return cr;
        }

        return CfgMgr32.TryDecodeGuid(type, data, out containerId) ? CfgMgr32.CR_SUCCESS : CfgMgr32.CR_INVALID_PROPERTY;
    }

    public uint GetConfigFlags(uint devInst, out uint configFlags)
    {
        configFlags = 0;
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, CfgMgr32.DEVPKEY_Device_ConfigFlags, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return cr;
        }

        return CfgMgr32.TryDecodeUInt32(type, data, out configFlags) ? CfgMgr32.CR_SUCCESS : CfgMgr32.CR_INVALID_PROPERTY;
    }

    public uint GetName(uint devInst, out string? name)
    {
        name = null;
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, CfgMgr32.DEVPKEY_NAME, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return cr;
        }

        return CfgMgr32.TryDecodeString(type, data, out name) ? CfgMgr32.CR_SUCCESS : CfgMgr32.CR_INVALID_PROPERTY;
    }
}

// A node selected for the pinned device, found with a non-present-inclusive locate.
internal sealed record TargetNode(string InstanceId, Guid ContainerId, uint PhantomDevInst)
{
    // The device node itself (BTHENUM\DEV_<address>\...), as opposed to its service nodes.
    public bool IsDeviceNode => InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
}

internal sealed record NodeScanResult(bool Listed, IReadOnlyList<TargetNode> Targets, IReadOnlyList<StepOutcome> Steps);

// Finds nodes by the shared NodeMatch predicate over the full devnode list, so the tray, the probe and the
// gate select exactly the same set.
internal static class NodeScan
{
    // Every disable target of the pinned identity: in the pinned container, a Bluetooth bus node, and
    // carrying the 12-hex address. Non-present nodes are included; a node whose container cannot be read
    // is never selected.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw
    public static NodeScanResult FindTargets(INodeReader nodes, Guid pinnedContainer, string address12)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(address12);
        var steps = new List<StepOutcome>();
        var targets = new List<TargetNode>();

        if (!NodeMatch.IsValidTargetContainer(pinnedContainer) || !BoundaryValidation.IsAddress12(address12))
        {
            steps.Add(StepOutcomes.NotAttempted("node-scan", "The pinned device is not valid, so no node is selected."));
            return new NodeScanResult(false, targets, steps);
        }

        uint cr = nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return new NodeScanResult(false, targets, steps);
        }

        foreach (string id in ids)
        {
            // Cheap filter first: NodeMatch requires the address in the id, so only those nodes need a
            // container read.
            if (!id.Contains(address12, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadContainer(nodes, id, steps, out uint devInst, out Guid container))
            {
                continue;
            }

            if (NodeMatch.IsDisableTarget(id, container, pinnedContainer, address12))
            {
                targets.Add(new TargetNode(id, container, devInst));
            }
        }

        targets.Sort(static (a, b) => string.CompareOrdinal(a.InstanceId, b.InstanceId));
        return new NodeScanResult(true, targets, steps);
    }

    // Every node of any enumerator in the container, for the probe's dump.
    public static NodeScanResult FindContainerNodes(INodeReader nodes, Guid container)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var steps = new List<StepOutcome>();
        var found = new List<TargetNode>();
        if (!NodeMatch.IsValidTargetContainer(container))
        {
            steps.Add(StepOutcomes.NotAttempted("node-scan", "The pinned container is not valid."));
            return new NodeScanResult(false, found, steps);
        }

        uint cr = nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return new NodeScanResult(false, found, steps);
        }

        foreach (string id in ids)
        {
            uint locate = nodes.Locate(id, includeNonPresent: true, out uint devInst);
            if (locate != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, locate, "Listed but could not be located."));
                continue;
            }

            uint read = nodes.GetContainerId(devInst, out Guid nodeContainer);
            if (read == CfgMgr32.CR_SUCCESS && nodeContainer == container)
            {
                found.Add(new TargetNode(id, nodeContainer, devInst));
            }
            else if (read is not (CfgMgr32.CR_SUCCESS or CfgMgr32.CR_NO_SUCH_VALUE))
            {
                // A node with no container is common and not a failure; anything else is reported.
                steps.Add(StepOutcomes.FromConfigRet("cm-container:" + id, read));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.InstanceId, b.InstanceId));
        return new NodeScanResult(true, found, steps);
    }

    private static bool TryReadContainer(INodeReader nodes, string id, List<StepOutcome> steps, out uint devInst, out Guid container)
    {
        container = Guid.Empty;
        uint cr = nodes.Locate(id, includeNonPresent: true, out devInst);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, cr, "Listed but could not be located."));
            return false;
        }

        cr = nodes.GetContainerId(devInst, out container);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-container:" + id, cr, "Container unreadable, so the node is not selected."));
            return false;
        }

        return true;
    }
}

internal sealed record NodeReadResult(bool Listed, IReadOnlyList<BluetoothNode> Nodes, IReadOnlyList<StepOutcome> Steps)
{
    public static NodeReadResult NoIdentity { get; } = new(false, Array.Empty<BluetoothNode>(), Array.Empty<StepOutcome>());
}

// Reads the real state of the target nodes without elevation. This is the ground truth for the boot
// block: a node is disabled when DN_HAS_PROBLEM is set with CM_PROB_DISABLED (22), and the disable
// persists when DEVPKEY_Device_ConfigFlags has CONFIGFLAG_DISABLED (0x1), a cross-check only because
// that property is documented for internal use.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-the-status-and-problem-code-for-a-device-instance
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-disabled
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
internal sealed class NodeStateReader
{
    private readonly INodeReader _nodes;

    public NodeStateReader(INodeReader nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
    }

    public NodeReadResult Read(Guid pinnedContainer, string address12)
    {
        NodeScanResult scan = NodeScan.FindTargets(_nodes, pinnedContainer, address12);
        var steps = new List<StepOutcome>(scan.Steps);
        var result = new List<BluetoothNode>(scan.Targets.Count);
        foreach (TargetNode target in scan.Targets)
        {
            result.Add(ReadNode(_nodes, target, steps));
        }

        return new NodeReadResult(scan.Listed, result, steps);
    }

    public static BluetoothNode ReadNode(INodeReader nodes, TargetNode target, List<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(steps);

        string id = target.InstanceId;
        string prefix = EnumeratorOf(id);

        uint nameCr = nodes.GetName(target.PhantomDevInst, out string? name);
        if (nameCr is not (CfgMgr32.CR_SUCCESS or CfgMgr32.CR_NO_SUCH_VALUE))
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-name:" + id, nameCr));
        }

        bool persistBit = false;
        uint flagsCr = nodes.GetConfigFlags(target.PhantomDevInst, out uint configFlags);
        if (flagsCr == CfgMgr32.CR_SUCCESS)
        {
            persistBit = (configFlags & CfgMgr32.CONFIGFLAG_DISABLED) != 0;
        }
        else if (flagsCr != CfgMgr32.CR_NO_SUCH_VALUE)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-configflags:" + id, flagsCr));
        }

        uint cr = nodes.Locate(id, includeNonPresent: false, out uint devInst);
        if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-locate:" + id, cr, "Not present."));
            return new BluetoothNode(id, prefix, name, IsPresent: false, NodeBlockStatus.Unknown, 0, persistBit);
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-locate:" + id, cr, "Presence unreadable."));
            return new BluetoothNode(id, prefix, name, IsPresent: false, NodeBlockStatus.Unknown, 0, persistBit);
        }

        cr = nodes.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-status:" + id, cr, "Status unreadable."));
            return new BluetoothNode(id, prefix, name, IsPresent: true, NodeBlockStatus.Unknown, 0, persistBit);
        }

        // The problem number is meaningful only with DN_HAS_PROBLEM.
        bool hasProblem = (status & CfgMgr32.DN_HAS_PROBLEM) != 0;
        uint problemCode = hasProblem ? problem : 0;
        NodeBlockStatus blockStatus = hasProblem && problem == CfgMgr32.CM_PROB_DISABLED
            ? NodeBlockStatus.Disabled
            : NodeBlockStatus.Enabled;
        return new BluetoothNode(id, prefix, name, IsPresent: true, blockStatus, problemCode, persistBit);
    }

    internal static string EnumeratorOf(string instanceId)
    {
        int slash = instanceId.IndexOf('\\', StringComparison.Ordinal);
        return (slash < 0 ? instanceId : instanceId[..slash]).ToUpperInvariant();
    }
}

// Turns the node reads into the BlockState the tray shows. Pure.
internal static class BlockStateClassifier
{
    // tasksInstalled: \Earshot\Gate (and its sibling tasks) present and verified.
    // identityKnown:  a valid pinned container and address were available.
    // Only present nodes decide Allowed, Blocked or Mixed; a node whose status could not be read makes the
    // state Unknown unless disabled and enabled nodes are both seen, which is Mixed whatever the rest are.
    public static BlockState Classify(bool tasksInstalled, bool identityKnown, NodeReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!tasksInstalled)
        {
            return BlockState.NotSetUp;
        }

        if (!identityKnown)
        {
            return BlockState.NotFound;
        }

        if (!read.Listed)
        {
            return BlockState.Unknown;
        }

        if (read.Nodes.Count == 0)
        {
            return BlockState.NotFound;
        }

        int disabled = 0, enabled = 0, unknown = 0;
        foreach (BluetoothNode node in read.Nodes)
        {
            if (!node.IsPresent)
            {
                continue;
            }

            switch (node.Status)
            {
                case NodeBlockStatus.Disabled: disabled++; break;
                case NodeBlockStatus.Enabled: enabled++; break;
                default: unknown++; break;
            }
        }

        if (disabled > 0 && enabled > 0)
        {
            return BlockState.Mixed;
        }

        if (unknown > 0 || disabled + enabled == 0)
        {
            return BlockState.Unknown;
        }

        return disabled > 0 ? BlockState.Blocked : BlockState.Allowed;
    }

    // True when every present target is disabled with the persistent flag and none is enabled or unreadable,
    // so a block request has nothing to do.
    public static bool IsFullyBlocked(NodeReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var present = read.Nodes.Where(n => n.IsPresent).ToList();
        return read.Listed && present.Count > 0 &&
               present.All(n => n.Status == NodeBlockStatus.Disabled && n.ConfigFlagsDisabledBit);
    }

    // True when every present target is enabled and none carries the persistent disable flag.
    public static bool IsFullyAllowed(NodeReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var present = read.Nodes.Where(n => n.IsPresent).ToList();
        return read.Listed && present.Count > 0 &&
               present.All(n => n.Status == NodeBlockStatus.Enabled && !n.ConfigFlagsDisabledBit);
    }
}
