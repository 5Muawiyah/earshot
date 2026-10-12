using System.Globalization;
using System.Text;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Repair;

// What Fix connection would undo. The plan is built from reads only and lists a change in two cases: Earshot's own records
// show it made the change (the pinned device and the services protection.json lists), or Earshot's own node choice
// (NodeMatch.IsDisableTarget) picks the node on an AirPods container that the identity rule found, and Earshot has left a
// footprint on this PC. It never lists another device and never a node outside an AirPods container.

// present and disabled (problem 22): enable it. Out of the tree with CONFIGFLAG_DISABLED: clear the flag.
internal enum FixNodeAction
{
    Enable,
    ClearFlag,
}

// Label is a plain description of the node's job, never its id, because the plan is shown to a person.
internal sealed record FixNodeStep(string InstanceId, string Label, FixNodeAction Action, bool FromRecords);

// One AirPods the identity rule found. Nodes is empty when nothing of this device is to be undone (the device is listed
// so it can be paired again).
internal sealed record FixDevice(
    string Name, string Address, Guid ContainerId, bool Pinned, bool DevicePresent, IReadOnlyList<FixNodeStep> Nodes);

internal sealed record FixServiceStep(Guid Service, string Label);

internal sealed record FixPlan(
    IReadOnlyList<FixDevice> Devices,
    IReadOnlyList<FixServiceStep> Services,
    IReadOnlyList<string> Leftovers,
    IReadOnlyList<string> Notes,
    bool HasFootprint,
    bool NeedsPick)
{
    public bool IsEmpty => Devices.All(d => d.Nodes.Count == 0) && Services.Count == 0;

    public IReadOnlyList<string> DeviceNames => Devices.Select(d => d.Name).ToList();

    // The plan for one device of this one, for when the owner picked which AirPods to undo. The services stay: they belong
    // to the device the records pin, which is never the one that is picked among several unpinned.
    public FixPlan OnlyDevice(string address) => this with
    {
        Devices = Devices.Where(d => string.Equals(d.Address, address, StringComparison.Ordinal)).ToList(),
        NeedsPick = false,
    };
}

internal sealed record FixPlanResult(FixPlan Plan, bool Listed, IReadOnlyList<StepOutcome> Steps);

// The records Earshot keeps, read without changing them. A fake over a temp folder, or the real folder.
internal interface IFixRecords
{
    GateRead<DeviceIdentity> ReadDevice();

    GateRead<ProtectionRecord> ReadProtection();

    GateRead<GateConfig> ReadConfig();

    // Earshot has left something on this PC: records, a settings file or an installed program.
    bool HasFootprint();
}

// What is still installed, as plain sentences for the plan. Informational: Fix connection removes none of it.
internal interface IFixLeftovers
{
    IReadOnlyList<string> Read();
}

internal sealed class StoreFixRecords : IFixRecords
{
    private readonly GateStore _store;
    private readonly Func<bool> _otherFootprint;

    // otherFootprint: a settings file or an installed program exists.
    public StoreFixRecords(GateStore store, Func<bool> otherFootprint)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(otherFootprint);
        _store = store;
        _otherFootprint = otherFootprint;
    }

    public GateRead<DeviceIdentity> ReadDevice() => _store.ReadDevice();

    public GateRead<ProtectionRecord> ReadProtection() => _store.ReadProtection();

    public GateRead<GateConfig> ReadConfig() => _store.ReadConfig();

    public bool HasFootprint() =>
        ReadDevice().Status != GateReadStatus.Missing || ReadConfig().Status != GateReadStatus.Missing ||
        ReadProtection().Status != GateReadStatus.Missing || _otherFootprint();
}

internal static class FixPlanner
{
    public static FixPlanResult Build(INodeReader nodes, IFixRecords records, IFixLeftovers leftovers)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(leftovers);

        var steps = new List<StepOutcome>();
        var notes = new List<string>();
        IReadOnlyList<string> left = leftovers.Read();
        bool footprint = records.HasFootprint();

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(nodes);
        steps.AddRange(found.Steps.Where(s => !s.Ok));
        if (!found.Listed)
        {
            notes.Add("The list of devices on this PC could not be read, so nothing is planned.");
            return new FixPlanResult(new FixPlan([], [], left, notes, footprint, NeedsPick: false), Listed: false, steps);
        }

        GateRead<DeviceIdentity> device = records.ReadDevice();
        bool pinnedKnown = device.IsOk && device.Value is not null;
        IReadOnlyList<AirPodsCandidate> inScope = found.Candidates;
        DeviceIdentity? pin = pinnedKnown ? device.Value : null;
        if (pin is not null)
        {
            inScope = found.Candidates.Where(c => c.ContainerId == pin.ContainerId && string.Equals(c.Address, pin.Address, StringComparison.Ordinal)).ToList();
            if (inScope.Count == 0)
            {
                notes.Add("The AirPods Earshot was set up for are not on this PC any more.");
            }
        }
        else if (device.Status == GateReadStatus.Invalid || device.Status == GateReadStatus.Unreadable)
        {
            steps.Add(device.Step);
            notes.Add("Earshot's record of the AirPods could not be read.");
        }

        bool restoreNodes = pin is not null || footprint;
        if (pin is null && !footprint)
        {
            notes.Add("Earshot has left no mark on this PC, so no setting is undone.");
        }

        var devices = new List<FixDevice>();
        var reader = new NodeStateReader(nodes);
        foreach (AirPodsCandidate candidate in inScope)
        {
            var nodeSteps = new List<FixNodeStep>();
            if (restoreNodes)
            {
                NodeReadResult read = reader.Read(candidate.ContainerId, candidate.Address);
                steps.AddRange(read.Steps.Where(s => !s.Ok && !s.Step.StartsWith("cm-locate:", StringComparison.Ordinal)));
                foreach (BluetoothNode node in read.Nodes)
                {
                    FixNodeAction? action = node.IsPresent
                        ? (node.Status == NodeBlockStatus.Disabled ? FixNodeAction.Enable : null)
                        : (node.ConfigFlagsDisabledBit ? FixNodeAction.ClearFlag : null);
                    if (action is { } a)
                    {
                        nodeSteps.Add(new FixNodeStep(node.InstanceId, FixLabels.NodeKind(node.InstanceId), a, FromRecords: pin is not null));
                    }
                }

                if (read.Unread.Count > 0)
                {
                    notes.Add("Some parts of " + NameOf(candidate) + " could not be read and are left alone.");
                }
            }

            devices.Add(new FixDevice(NameOf(candidate), candidate.Address, candidate.ContainerId, Pinned: pin is not null, candidate.DevicePresent, nodeSteps));
        }

        var services = new List<FixServiceStep>();
        if (pin is not null && inScope.Count > 0)
        {
            GateRead<ProtectionRecord> record = records.ReadProtection();
            if (record.IsOk && record.Value is not null)
            {
                services.AddRange(record.Value.DisabledServices.Select(s => new FixServiceStep(s, FixLabels.ServiceKind(s))));
            }
            else if (record.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
            {
                steps.Add(record.Step);
                notes.Add("Earshot's record of turned-off services could not be read, so no service is turned back on.");
            }
        }

        bool pick = pin is null && restoreNodes && devices.Count(d => d.Nodes.Count > 0) > 1;
        if (pick)
        {
            notes.Add("More than one pair of AirPods is on this PC. Pick the one to undo.");
        }

        return new FixPlanResult(new FixPlan(devices, services, left, notes, footprint, pick), Listed: true, steps);
    }

    private static string NameOf(AirPodsCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Name) ? "AirPods" : candidate.Name;
}

// Plain words for nodes and services. Never an id.
internal static class FixLabels
{
    public static string NodeKind(string instanceId)
    {
        if (instanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
        {
            return "The device itself";
        }

        if (instanceId.Contains("0000110B-", StringComparison.OrdinalIgnoreCase))
        {
            return "Audio playback";
        }

        if (instanceId.Contains("0000110C-", StringComparison.OrdinalIgnoreCase) || instanceId.Contains("0000110E-", StringComparison.OrdinalIgnoreCase))
        {
            return "Media controls";
        }

        if (instanceId.Contains("0000111E-", StringComparison.OrdinalIgnoreCase))
        {
            return "Hands-Free calls";
        }

        if (instanceId.Contains("00001000-", StringComparison.OrdinalIgnoreCase))
        {
            return "Service discovery";
        }

        if (instanceId.Contains("00001801-", StringComparison.OrdinalIgnoreCase))
        {
            return "Generic attributes";
        }

        return "An AirPods service";
    }

    public static string ServiceKind(Guid service) =>
        service == ProtectedServices.Handsfree ? "Hands-Free"
        : service == ProtectedServices.Headset ? "Headset"
        : "A Bluetooth service";
}

// The plan as the sentences a person reads, names only.
internal static class FixPlanText
{
    public static string Describe(FixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sb = new StringBuilder();
        if (plan.IsEmpty)
        {
            sb.AppendLine("Earshot has no change to undo.");
        }
        else
        {
            sb.AppendLine("Earshot would undo these changes:");
            foreach (FixDevice device in plan.Devices.Where(d => d.Nodes.Count > 0))
            {
                sb.AppendLine(device.Name + (device.Pinned ? "" : " (these may have been Earshot's)"));
                foreach (FixNodeStep node in device.Nodes)
                {
                    sb.AppendLine("  " + (node.Action == FixNodeAction.Enable ? "Turn on: " : "Clear the saved off setting: ") + node.Label);
                }
            }

            foreach (FixServiceStep service in plan.Services)
            {
                sb.AppendLine("  Turn back on in Windows: " + service.Label);
            }
        }

        if (plan.Devices.Count > 0)
        {
            sb.AppendLine("AirPods found: " + string.Join(", ", plan.DeviceNames));
        }

        foreach (string note in plan.Notes)
        {
            sb.AppendLine(note);
        }

        foreach (string item in plan.Leftovers)
        {
            sb.AppendLine(item);
        }

        return sb.ToString().TrimEnd();
    }

    public static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
