using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot;

// diag battery-sweep: the battery check of the brief's phase 0, as evidence for the owner's live test 11 (the leg
// with the AirPods disconnected). It only reads, and like every diag target it runs only when the owner runs it.
//
//   1. Device nodes, the Get-PnpDevice and Get-PnpDeviceProperty sweep done with CfgMgr32: every device id,
//      including nodes that are not present; the nodes whose friendly name or name contains the device match
//      string, plus every node in the pinned container; for each, every property key set on it
//      (CM_Get_DevNode_Property_Keys) with its type and size, and the value of each watched key.
//   2. Association endpoints, the store the Windows device information APIs read: the paired AEPs and AEP
//      containers with all their properties (DevGetObjects), those matching the name or the pinned container,
//      and the value of each watched key on them.
//
// Watched keys: any property in the {104EA319-6EE2-4701-BD47-8DDBF425BBE5} set (the key the phase 0 sweep filtered
// on), and System.Devices.BatteryLife, System.Devices.BatteryPlusCharging, System.Devices.BatteryPlusChargingText
// and System.Devices.Notifications.LowBattery (propkey.h).
//
// Positive controls, so an empty result means something: a container id key read on at least one matched node,
// and the IsPaired key read on at least one paired endpoint. A sweep whose controls fail exits non-zero, because
// it proved nothing. Values are recorded exactly as read, never turned into a percentage.
// https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_property_keys
// https://learn.microsoft.com/en-us/windows/win32/api/devquery/nf-devquery-devgetobjects
internal static partial class Program
{
    static partial void DiagBatterySweep(DiagContext ctx)
    {
        ctx.Handled = true;
        ILog log = ctx.Services.Log;
        EarshotSettings settings = ctx.Services.Settings.Current;

        string evidence;
        try
        {
            evidence = ctx.NewEvidenceFile("battery-sweep");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string message = "The evidence folder " + ctx.EvidenceFolder + " could not be made, so nothing was read.";
            log.Error("diag battery-sweep: " + message + " " + NativeCodes.Name(ex.HResult) + " " + ex.Message);
            ctx.Out.WriteLine(message);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        BatterySweepReport report = BatterySweep.Run(new SystemBatterySweepReader(), settings.DeviceMatch, settings.PinnedContainerId, TimeProvider.System);
        string json = BatterySweep.ToJson(report);
        bool saved;
        try
        {
            File.WriteAllText(evidence, json);
            saved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("diag battery-sweep: the evidence could not be written to " + evidence + " (" + NativeCodes.Name(ex.HResult) + " " +
                     ex.Message + "). Evidence: " + json);
            ctx.Out.WriteLine("The evidence could not be written to " + evidence + ". It is in the log and below.");
            ctx.Out.WriteLine(json);
            saved = false;
        }

        foreach (string line in BatterySweep.Summary(report))
        {
            ctx.Out.WriteLine(line);
        }

        if (saved)
        {
            ctx.Out.WriteLine("Evidence: " + evidence);
        }

        foreach (StepOutcome step in report.Steps.Where(s => !s.Ok))
        {
            log.Warn("diag battery-sweep: " + step.Step + " " + step.CodeName + (step.Detail is null ? "" : " (" + step.Detail + ")"));
        }

        ctx.ExitCode = BatterySweep.ExitCode(report, saved);
        log.Info("diag battery-sweep: exit " + ctx.ExitCode.ToString(CultureInfo.InvariantCulture) + ", evidence " + (saved ? evidence : "in the log only"));
    }
}

// The reads the sweep makes. The real one is CfgMgr32 and DevQuery; tests use a table.
internal interface IBatterySweepReader
{
    uint ListDeviceIds(out string[] ids);

    // Includes nodes that are not present.
    uint Locate(string instanceId, out uint devInst);

    uint GetPropertyKeys(uint devInst, out DEVPROPKEY[] keys);

    uint GetProperty(uint devInst, DEVPROPKEY key, out uint type, out byte[] data);

    // Only nodes that are present are found by CM_LOCATE_DEVNODE_NORMAL.
    bool IsPresent(string instanceId);

    int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects);
}

internal sealed class SystemBatterySweepReader : IBatterySweepReader
{
    public uint ListDeviceIds(out string[] ids) => CfgMgr32.GetDeviceIdList(null, CfgMgr32.CM_GETIDLIST_FILTER_NONE, out ids);

    public uint Locate(string instanceId, out uint devInst) => CfgMgr32.LocateDevNode(instanceId, CfgMgr32.CM_LOCATE_DEVNODE_PHANTOM, out devInst);

    public uint GetPropertyKeys(uint devInst, out DEVPROPKEY[] keys) => CfgMgr32.GetDevNodePropertyKeys(devInst, out keys);

    public uint GetProperty(uint devInst, DEVPROPKEY key, out uint type, out byte[] data) => CfgMgr32.GetDevNodeProperty(devInst, key, out type, out data);

    public bool IsPresent(string instanceId) => CfgMgr32.LocateDevNode(instanceId, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL, out _) == CfgMgr32.CR_SUCCESS;

    public int GetPairedObjects(int objectType, out IReadOnlyList<DevObjectRecord> objects) => DevQuery.GetPairedObjects(objectType, out objects);
}

// A property as the evidence records it: the key as "{fmtid} pid", the DEVPROPTYPE, the size, and for a
// watched key the value as read (Value null for the others).
internal sealed record SweepProperty(string Key, uint Type, int Size, bool Watched, string? Value);

internal sealed record SweepNode(
    string InstanceId, string? FriendlyName, string? Name, bool Present, Guid? ContainerId, string MatchedBy,
    StepOutcome Keys, IReadOnlyList<SweepProperty> Properties)
{
    public bool HasContainerKey => Properties.Any(p => p.Key == BatterySweep.KeyText(CfgMgr32.DEVPKEY_Device_ContainerId));
}

internal sealed record SweepObject(string Id, string? Name, string MatchedBy, IReadOnlyList<SweepProperty> Properties);

internal sealed record SweepObjectQuery(string Kind, StepOutcome Query, int Paired, int WithPairedKey, IReadOnlyList<SweepObject> Matched);

internal sealed record BatterySweepReport(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string Match,
    Guid PinnedContainerId,
    StepOutcome List,
    int NodesListed,
    IReadOnlyList<SweepNode> Nodes,
    IReadOnlyList<SweepObjectQuery> Objects,
    IReadOnlyList<StepOutcome> Steps)
{
    // A watched key that came back with a value on a matched node or endpoint.
    public IReadOnlyList<SweepProperty> WatchedValues =>
        Nodes.SelectMany(n => n.Properties).Concat(Objects.SelectMany(q => q.Matched.SelectMany(o => o.Properties)))
            .Where(p => p.Watched && p.Size > 0).ToList();

    public bool NodeControlPassed => List.Ok && Nodes.Any(n => n.HasContainerKey);

    public bool EndpointControlPassed => Objects.Any(q => q.Query.Ok && q.WithPairedKey > 0);
}

internal static class BatterySweep
{
    public const string BatteryKeySet = "104EA319-6EE2-4701-BD47-8DDBF425BBE5";

    private static readonly Guid BatteryKeySetId = new(BatteryKeySet);

    // propkey.h: System.Devices.BatteryLife (10), BatteryPlusCharging (22), BatteryPlusChargingText (23), and
    // System.Devices.Notifications.LowBattery.
    private static readonly Guid DevicesSetId = new("49CD1F76-5626-4B17-A4E8-18B4AA1A2213");
    private static readonly uint[] DevicesBatteryIds = [10, 22, 23];
    private static readonly DEVPROPKEY LowBattery = new(new Guid("C4C07F2B-8524-4E66-AE3A-A6235F103BEB"), 2);

    // System.Devices.Aep.ContainerId (propkey.h).
    private static readonly DEVPROPKEY AepContainerId = new(new Guid("E7C3FB29-CAA7-4F47-8C8B-BE59B330D4C5"), 2);

    private const int MaxValueBytes = 256;

    public static bool IsWatched(DEVPROPKEY key) =>
        key.fmtid == BatteryKeySetId ||
        (key.fmtid == DevicesSetId && DevicesBatteryIds.Contains(key.pid)) ||
        (key.fmtid == LowBattery.fmtid && key.pid == LowBattery.pid);

    public static string KeyText(DEVPROPKEY key) =>
        key.fmtid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant() + " " + key.pid.ToString(CultureInfo.InvariantCulture);

    public static BatterySweepReport Run(IBatterySweepReader reader, string match, Guid pinnedContainer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(time);
        DateTimeOffset started = time.GetUtcNow();
        var steps = new List<StepOutcome>();
        bool pinned = NodeMatch.IsValidTargetContainer(pinnedContainer);

        uint cr = reader.ListDeviceIds(out string[] ids);
        StepOutcome list = StepOutcomes.FromConfigRet("cm-list", cr, ids.Length.ToString(CultureInfo.InvariantCulture) + " device ids, present or not.");
        steps.Add(list);

        var nodes = new List<SweepNode>();
        int locateFailures = 0;
        StepOutcome? firstLocateFailure = null;
        foreach (string id in cr == CfgMgr32.CR_SUCCESS ? ids : [])
        {
            uint located = reader.Locate(id, out uint devInst);
            if (located != CfgMgr32.CR_SUCCESS)
            {
                locateFailures++;
                firstLocateFailure ??= StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, located);
                continue;
            }

            string? friendly = ReadString(reader, devInst, CfgMgr32.DEVPKEY_Device_FriendlyName);
            string? name = ReadString(reader, devInst, CfgMgr32.DEVPKEY_NAME);
            Guid? container = ReadGuid(reader, devInst, CfgMgr32.DEVPKEY_Device_ContainerId);
            string? matchedBy = NodeMatch.NameMatches(friendly, match) ? "friendly name"
                : NodeMatch.NameMatches(name, match) ? "name"
                : pinned && container == pinnedContainer ? "pinned container"
                : null;
            if (matchedBy is null)
            {
                continue;
            }

            uint keysCr = reader.GetPropertyKeys(devInst, out DEVPROPKEY[] keys);
            StepOutcome keysStep = StepOutcomes.FromConfigRet("cm-property-keys:" + id, keysCr,
                keys.Length.ToString(CultureInfo.InvariantCulture) + " keys.");
            var properties = new List<SweepProperty>();
            foreach (DEVPROPKEY key in keys)
            {
                uint propertyCr = reader.GetProperty(devInst, key, out uint type, out byte[] data);
                bool watched = IsWatched(key);
                if (propertyCr != CfgMgr32.CR_SUCCESS)
                {
                    StepOutcome failed = StepOutcomes.FromConfigRet("cm-property:" + id + ":" + KeyText(key), propertyCr);
                    steps.Add(failed);
                    properties.Add(new SweepProperty(KeyText(key), 0, 0, watched, watched ? "not read: " + failed.CodeName : null));
                    continue;
                }

                properties.Add(new SweepProperty(KeyText(key), type, data.Length, watched, watched ? DescribeValue(type, data) : null));
            }

            if (!keysStep.Ok)
            {
                steps.Add(keysStep);
            }

            nodes.Add(new SweepNode(id, friendly, name, reader.IsPresent(id), container, matchedBy, keysStep, properties));
        }

        if (firstLocateFailure is not null)
        {
            steps.Add(firstLocateFailure with
            {
                Detail = locateFailures.ToString(CultureInfo.InvariantCulture) + " device ids could not be located; this is the first.",
            });
        }

        var objects = new List<SweepObjectQuery>
        {
            QueryObjects(reader, DevQuery.DevObjectTypeAEP, "aep", DevQuery.PKEY_Devices_Aep_IsPaired, match, pinned, pinnedContainer, steps),
            QueryObjects(reader, DevQuery.DevObjectTypeAEPContainer, "aep-container", DevQuery.PKEY_Devices_AepContainer_IsPaired, match, pinned, pinnedContainer, steps),
        };

        return new BatterySweepReport(started, time.GetUtcNow(), match, pinnedContainer, list, ids.Length, nodes, objects, steps);
    }

    private static SweepObjectQuery QueryObjects(
        IBatterySweepReader reader, int objectType, string kind, DEVPROPKEY pairedKey, string match, bool pinned, Guid pinnedContainer, List<StepOutcome> steps)
    {
        int hr = reader.GetPairedObjects(objectType, out IReadOnlyList<DevObjectRecord> found);
        StepOutcome query = StepOutcomes.FromHResult("dev-get-objects:" + kind, hr,
            found.Count.ToString(CultureInfo.InvariantCulture) + " paired objects with all their properties.");
        steps.Add(query);

        int withPairedKey = 0;
        var matched = new List<SweepObject>();
        foreach (DevObjectRecord item in found)
        {
            if (item.Properties.Any(p => Same(p.Key, pairedKey) && (p.Type & DevQuery.DEVPROP_MASK_TYPE) == CfgMgr32.DEVPROP_TYPE_BOOLEAN && p.Data.Length > 0))
            {
                withPairedKey++;
            }

            string? name = item.Properties.Where(p => Same(p.Key, CfgMgr32.DEVPKEY_NAME))
                .Select(p => CfgMgr32.TryDecodeString(p.Type, p.Data, out string? s) ? s : null)
                .FirstOrDefault(s => s is not null);
            Guid? container = objectType == DevQuery.DevObjectTypeAEPContainer
                ? Guid.TryParse(item.Id, out Guid parsed) ? parsed : null
                : item.Properties.Where(p => Same(p.Key, AepContainerId))
                    .Select(p => CfgMgr32.TryDecodeGuid(p.Type, p.Data, out Guid g) ? g : (Guid?)null)
                    .FirstOrDefault(g => g is not null);
            string? matchedBy = NodeMatch.NameMatches(name, match) ? "name"
                : pinned && container == pinnedContainer ? "pinned container"
                : null;
            if (matchedBy is null)
            {
                continue;
            }

            List<SweepProperty> properties = item.Properties
                .Select(p => new SweepProperty(KeyText(p.Key), p.Type, p.Data.Length, IsWatched(p.Key),
                    IsWatched(p.Key) ? DescribeValue(p.Type, p.Data) + (p.Truncated ? " (cut short)" : "") : null))
                .ToList();
            matched.Add(new SweepObject(item.Id, name, matchedBy, properties));
        }

        return new SweepObjectQuery(kind, query, found.Count, withPairedKey, matched);
    }

    // The value as read: the number, boolean or string for those types, and always the bytes in hex.
    public static string DescribeValue(uint type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string hex = data.Length == 0 ? "no data" : "0x" + Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, MaxValueBytes)));
        string typeText = "type 0x" + type.ToString("X", CultureInfo.InvariantCulture);
        if (type is CfgMgr32.DEVPROP_TYPE_EMPTY or CfgMgr32.DEVPROP_TYPE_NULL)
        {
            return typeText + ", empty";
        }

        string? decoded = type switch
        {
            DevQuery.DEVPROP_TYPE_BYTE when data.Length >= 1 => data[0].ToString(CultureInfo.InvariantCulture),
            DevQuery.DEVPROP_TYPE_SBYTE when data.Length >= 1 => ((sbyte)data[0]).ToString(CultureInfo.InvariantCulture),
            DevQuery.DEVPROP_TYPE_UINT16 when data.Length >= 2 => BitConverter.ToUInt16(data).ToString(CultureInfo.InvariantCulture),
            DevQuery.DEVPROP_TYPE_INT16 when data.Length >= 2 => BitConverter.ToInt16(data).ToString(CultureInfo.InvariantCulture),
            CfgMgr32.DEVPROP_TYPE_UINT32 when data.Length >= 4 => BitConverter.ToUInt32(data).ToString(CultureInfo.InvariantCulture),
            CfgMgr32.DEVPROP_TYPE_INT32 when data.Length >= 4 => BitConverter.ToInt32(data).ToString(CultureInfo.InvariantCulture),
            DevQuery.DEVPROP_TYPE_UINT64 when data.Length >= 8 => BitConverter.ToUInt64(data).ToString(CultureInfo.InvariantCulture),
            DevQuery.DEVPROP_TYPE_INT64 when data.Length >= 8 => BitConverter.ToInt64(data).ToString(CultureInfo.InvariantCulture),
            CfgMgr32.DEVPROP_TYPE_BOOLEAN when data.Length >= 1 => data[0] != CfgMgr32.DEVPROP_FALSE ? "true" : "false",
            CfgMgr32.DEVPROP_TYPE_STRING => CfgMgr32.TryDecodeString(type, data, out string? s) ? "\"" + s + "\"" : null,
            _ => null,
        };
        return typeText + ", " + (decoded is null ? hex : decoded + " (" + hex + ")");
    }

    public static IReadOnlyList<string> Summary(BatterySweepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "Match: \"" + report.Match + "\"" + (NodeMatch.IsValidTargetContainer(report.PinnedContainerId)
                ? ", pinned container " + report.PinnedContainerId.ToString("B", CultureInfo.InvariantCulture)
                : ", no pinned container"),
            "Device nodes: " + (report.List.Ok
                ? report.NodesListed.ToString(CultureInfo.InvariantCulture) + " listed, " + report.Nodes.Count.ToString(CultureInfo.InvariantCulture) + " matched"
                : "the list could not be read (" + report.List.CodeName + ")"),
        };
        foreach (SweepObjectQuery query in report.Objects)
        {
            lines.Add("Paired " + query.Kind + " objects: " + (query.Query.Ok
                ? query.Paired.ToString(CultureInfo.InvariantCulture) + ", " + query.Matched.Count.ToString(CultureInfo.InvariantCulture) + " matched"
                : "the query failed (" + query.Query.CodeName + ")"));
        }

        lines.Add("Checks: container id read on a matched node " + (report.NodeControlPassed ? "yes" : "no") +
                  ", IsPaired read on a paired endpoint " + (report.EndpointControlPassed ? "yes" : "no"));
        int values = report.WatchedValues.Count;
        lines.Add("Battery keys with a value: " + (values == 0 ? "none" : values.ToString(CultureInfo.InvariantCulture) + ", see the evidence"));
        return lines;
    }

    // 0 when the sweep read everything and both controls passed, whatever it found.
    public static int ExitCode(BatterySweepReport report, bool evidenceSaved)
    {
        ArgumentNullException.ThrowIfNull(report);
        return !evidenceSaved ? ExitCodes.IoError
            : !report.List.Ok ? ExitCodes.OsError
            : !report.NodeControlPassed || !report.EndpointControlPassed ? ExitCodes.Unavailable
            : ExitCodes.Ok;
    }

    public static string ToJson(BatterySweepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return ProbeContext.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", "battery-sweep");
            w.WriteString("startedUtc", report.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("finishedUtc", report.FinishedUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("match", report.Match);
            w.WriteString("pinnedContainer", report.PinnedContainerId.ToString("B", CultureInfo.InvariantCulture));
            w.WriteStartArray("watchedKeys");
            w.WriteStringValue("{" + BatteryKeySet + "} any");
            foreach (uint pid in DevicesBatteryIds)
            {
                w.WriteStringValue(KeyText(new DEVPROPKEY(DevicesSetId, pid)));
            }

            w.WriteStringValue(KeyText(LowBattery));
            w.WriteEndArray();

            w.WriteStartObject("deviceNodes");
            w.WriteString("list", report.List.CodeName);
            w.WriteNumber("listed", report.NodesListed);
            w.WriteBoolean("containerIdReadOnAMatchedNode", report.NodeControlPassed);
            w.WriteStartArray("matched");
            foreach (SweepNode node in report.Nodes)
            {
                w.WriteStartObject();
                w.WriteString("instanceId", node.InstanceId);
                w.WriteString("friendlyName", node.FriendlyName);
                w.WriteString("name", node.Name);
                w.WriteBoolean("present", node.Present);
                w.WriteString("container", node.ContainerId?.ToString("B", CultureInfo.InvariantCulture));
                w.WriteString("matchedBy", node.MatchedBy);
                w.WriteString("keys", node.Keys.CodeName);
                WriteProperties(w, node.Properties);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("associationEndpoints");
            foreach (SweepObjectQuery query in report.Objects)
            {
                w.WriteStartObject();
                w.WriteString("kind", query.Kind);
                w.WriteString("query", query.Query.CodeName);
                w.WriteNumber("paired", query.Paired);
                w.WriteNumber("withIsPaired", query.WithPairedKey);
                w.WriteStartArray("matched");
                foreach (SweepObject item in query.Matched)
                {
                    w.WriteStartObject();
                    w.WriteString("id", item.Id);
                    w.WriteString("name", item.Name);
                    w.WriteString("matchedBy", item.MatchedBy);
                    WriteProperties(w, item.Properties);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("watchedValues");
            foreach (SweepProperty property in report.WatchedValues)
            {
                w.WriteStringValue(property.Key + ": " + property.Value);
            }

            w.WriteEndArray();

            w.WriteStartArray("steps");
            foreach (StepOutcome step in report.Steps)
            {
                w.WriteStartObject();
                w.WriteString("step", step.Step);
                w.WriteBoolean("ok", step.Ok);
                w.WriteString("code", "0x" + unchecked((uint)step.Code).ToString("X8", CultureInfo.InvariantCulture));
                w.WriteString("codeName", step.CodeName);
                w.WriteString("detail", step.Detail);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static void WriteProperties(Utf8JsonWriter w, IReadOnlyList<SweepProperty> properties)
    {
        w.WriteStartArray("properties");
        foreach (SweepProperty property in properties)
        {
            w.WriteStartObject();
            w.WriteString("key", property.Key);
            w.WriteString("type", "0x" + property.Type.ToString("X", CultureInfo.InvariantCulture));
            w.WriteNumber("size", property.Size);
            if (property.Watched)
            {
                w.WriteBoolean("watched", true);
                w.WriteString("value", property.Value);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static bool Same(DEVPROPKEY a, DEVPROPKEY b) => a.fmtid == b.fmtid && a.pid == b.pid;

    private static string? ReadString(IBatterySweepReader reader, uint devInst, DEVPROPKEY key) =>
        reader.GetProperty(devInst, key, out uint type, out byte[] data) == CfgMgr32.CR_SUCCESS && CfgMgr32.TryDecodeString(type, data, out string? value)
            ? value
            : null;

    private static Guid? ReadGuid(IBatterySweepReader reader, uint devInst, DEVPROPKEY key) =>
        reader.GetProperty(devInst, key, out uint type, out byte[] data) == CfgMgr32.CR_SUCCESS && CfgMgr32.TryDecodeGuid(type, data, out Guid value)
            ? value
            : null;
}
