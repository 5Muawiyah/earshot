using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;

namespace Earshot;

// probe nodes: a read-only CfgMgr32 dump of the pinned device's container. Every node in the container is
// listed with whether the shared NodeMatch predicate makes it a disable target, so the dump shows the exact
// set a block would change and the nodes it leaves alone. Nothing is disabled, enabled or written.
internal static partial class Program
{
    internal sealed record ProbeNodeRow(
        string InstanceId,
        bool IsTarget,
        bool Present,
        uint? DevNodeStatus,
        uint Problem,
        uint? ConfigFlags,
        NodeBlockStatus Status,
        string? Name);

    internal sealed record NodesProbeReport(
        Guid? SettingsContainer,
        string SettingsAddress,
        string DeviceFile,
        Guid? DeviceContainer,
        string? DeviceAddress,
        Guid? UsedContainer,
        string? UsedAddress,
        string? UsedFrom,
        bool Listed,
        IReadOnlyList<ProbeNodeRow> Rows,
        BlockState? NodeState,
        IReadOnlyList<StepOutcome> Steps);

    static partial void ProbeNodes(ProbeContext ctx)
    {
        EarshotSettings settings = ctx.Services.Settings.Current;
        var store = new GateStore(Paths.Current.MachineFolder);
        NodesProbeReport report = ReadNodesProbe(new CfgMgr32NodeReader(), settings, store);
        WriteNodesProbe(ctx, report);
        ctx.Handled = true;
        ctx.ExitCode = report.UsedContainer is null ? ExitCodes.Config
            : !report.Listed ? ExitCodes.OsError
            : ExitCodes.Ok;
    }

    internal static NodesProbeReport ReadNodesProbe(INodeReader nodes, EarshotSettings settings, GateStore store)
    {
        var steps = new List<StepOutcome>();
        GateRead<DeviceIdentity> device = store.ReadDevice();
        var pinned = new DeviceIdentity { Address = settings.PinnedAddress, ContainerId = settings.PinnedContainerId };

        DeviceIdentity? used = device.IsOk ? device.Value : GateStore.ValidateDevice(pinned) is null ? pinned : null;
        string? usedFrom = device.IsOk ? "device.json" : used is null ? null : "settings";
        if (used is null)
        {
            return new NodesProbeReport(settings.PinnedContainerId, settings.PinnedAddress, device.Status.ToString(),
                device.Value?.ContainerId, device.Value?.Address, null, null, null, false, [], null, steps);
        }

        NodeScanResult scan = NodeScan.FindContainerNodes(nodes, used.ContainerId);
        steps.AddRange(scan.Steps);
        var rows = new List<ProbeNodeRow>();
        var targets = new List<BluetoothNode>();
        foreach (TargetNode node in scan.Targets)
        {
            BluetoothNode read = NodeStateReader.ReadNode(nodes, node, steps);
            bool isTarget = NodeMatch.IsDisableTarget(node.InstanceId, node.ContainerId, used.ContainerId, used.Address);
            if (isTarget)
            {
                targets.Add(read);
            }

            uint? rawStatus = null;
            if (nodes.Locate(node.InstanceId, includeNonPresent: false, out uint devInst) == CfgMgr32.CR_SUCCESS &&
                nodes.GetStatus(devInst, out uint status, out _) == CfgMgr32.CR_SUCCESS)
            {
                rawStatus = status;
            }

            uint? configFlags = nodes.GetConfigFlags(node.PhantomDevInst, out uint flags) == CfgMgr32.CR_SUCCESS ? flags : null;
            rows.Add(new ProbeNodeRow(node.InstanceId, isTarget, read.IsPresent, rawStatus, read.ProblemCode, configFlags, read.Status, read.Name));
        }

        // Classified from the nodes alone; the task check is probe task's job.
        BlockState? state = scan.Listed
            ? BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, new NodeReadResult(true, targets, []))
            : null;
        return new NodesProbeReport(settings.PinnedContainerId, settings.PinnedAddress, device.Status.ToString(),
            device.Value?.ContainerId, device.Value?.Address, used.ContainerId, used.Address, usedFrom, scan.Listed, rows, state, steps);
    }

    internal static void WriteNodesProbe(ProbeContext ctx, NodesProbeReport report)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "nodes");
                w.WriteString("settingsContainer", report.SettingsContainer?.ToString("D"));
                w.WriteString("settingsAddress", report.SettingsAddress);
                w.WriteString("deviceFile", report.DeviceFile);
                w.WriteString("usedFrom", report.UsedFrom);
                w.WriteString("container", report.UsedContainer?.ToString("D"));
                w.WriteString("address", report.UsedAddress);
                w.WriteBoolean("listed", report.Listed);
                w.WriteString("nodeState", report.NodeState?.ToString());
                w.WriteNumber("targets", report.Rows.Count(r => r.IsTarget));
                w.WriteStartArray("nodes");
                foreach (ProbeNodeRow row in report.Rows)
                {
                    w.WriteStartObject();
                    w.WriteString("instanceId", row.InstanceId);
                    w.WriteBoolean("target", row.IsTarget);
                    w.WriteBoolean("present", row.Present);
                    w.WriteString("status", row.Status.ToString());
                    WriteHex(w, "devNodeStatus", row.DevNodeStatus);
                    w.WriteNumber("problem", row.Problem);
                    WriteHex(w, "configFlags", row.ConfigFlags);
                    w.WriteString("name", row.Name);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteSteps(w, report.Steps);
                w.WriteEndObject();
            });
            return;
        }

        TextWriter o = ctx.Out;
        o.WriteLine("Pinned in settings: container " + (report.SettingsContainer?.ToString("D") ?? "none") +
                    ", address " + (report.SettingsAddress.Length == 0 ? "none" : report.SettingsAddress));
        o.WriteLine("Gate identity (device.json): " + (report.DeviceAddress is null ? report.DeviceFile : report.DeviceAddress + ", " + report.DeviceContainer?.ToString("D")));
        if (report.UsedContainer is null)
        {
            o.WriteLine("No valid device is pinned, so there is nothing to read.");
            return;
        }

        o.WriteLine("Reading container " + report.UsedContainer.Value.ToString("D") + ", address " + report.UsedAddress + " (from " + report.UsedFrom + ")");
        if (!report.Listed)
        {
            o.WriteLine("The device list could not be read.");
        }

        o.WriteLine("Nodes in the container: " + report.Rows.Count.ToString(CultureInfo.InvariantCulture) +
                    ", disable targets: " + report.Rows.Count(r => r.IsTarget).ToString(CultureInfo.InvariantCulture));
        foreach (ProbeNodeRow row in report.Rows)
        {
            o.WriteLine("  " + (row.IsTarget ? "[target] " : "[other]  ") + row.InstanceId);
            o.WriteLine("           present " + (row.Present ? "yes" : "no") + ", " + row.Status +
                        ", DN status " + Hex(row.DevNodeStatus) + ", problem " + row.Problem.ToString(CultureInfo.InvariantCulture) +
                        ", ConfigFlags " + Hex(row.ConfigFlags) + (row.Name is null ? "" : ", name " + row.Name));
        }

        o.WriteLine("Target node state: " + (report.NodeState?.ToString() ?? "unknown"));
        foreach (StepOutcome step in report.Steps.Where(s => !s.Ok))
        {
            o.WriteLine("  " + GateActions.Describe(step));
        }
    }

    private static string Hex(uint? value) =>
        value is null ? "unreadable" : "0x" + value.Value.ToString("X8", CultureInfo.InvariantCulture);

    private static void WriteHex(Utf8JsonWriter w, string name, uint? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, Hex(value));
        }
    }

    private static void WriteSteps(Utf8JsonWriter w, IEnumerable<StepOutcome> steps)
    {
        w.WriteStartArray("steps");
        foreach (StepOutcome step in steps)
        {
            w.WriteStartObject();
            w.WriteString("step", step.Step);
            w.WriteBoolean("ok", step.Ok);
            w.WriteNumber("code", step.Code);
            w.WriteString("codeName", step.CodeName);
            w.WriteString("detail", step.Detail);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
