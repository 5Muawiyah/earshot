using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Audio;
using Earshot.Composition;
using Earshot.Contracts;

namespace Earshot;

// probe audio: every Core Audio endpoint in every state, grouped by container, with the connection state
// derived for each group and the target the tray would choose. It uses the production monitor and model
// builder, refreshes once and does not register for notifications. Read-only.
internal static partial class Program
{
    static partial void ProbeAudio(ProbeContext ctx)
    {
        ctx.Handled = true;
        ServiceRegistry services = ctx.Services;
        if (services.Monitor is not CoreAudioDeviceMonitor monitor)
        {
            WriteProbeProblem(ctx, "The audio device monitor is not part of this build.");
            ctx.ExitCode = ExitCodes.Unavailable;
            return;
        }

        MonitorRefresh refresh;
        try
        {
            refresh = monitor.RefreshDetailedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            services.Log.Error("probe audio failed.", ex);
            WriteProbeProblem(ctx, "Reading the audio devices failed: " + ex.Message);
            ctx.ExitCode = ExitCodes.Software;
            return;
        }

        WriteAudioProbe(ctx, refresh, services.Settings.Current);
        ctx.ExitCode = refresh.EnumerationOk ? ExitCodes.Ok : ExitCodes.OsError;
    }

    internal static void WriteAudioProbe(ProbeContext ctx, MonitorRefresh refresh, EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(settings);

        Dictionary<string, string?> interfaceNames = InterfaceNames(refresh.Readings);
        DeviceSnapshot snapshot = refresh.Snapshot;
        List<StepOutcome> failed = refresh.Steps.Where(s => !s.Ok).ToList();

        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "audio");
                w.WriteBoolean("enumerationOk", refresh.EnumerationOk);
                w.WriteString("takenUtc", snapshot.TakenUtc);
                w.WriteString("deviceMatch", settings.DeviceMatch);
                WriteContainerOrNull(w, "pinnedContainerId", settings.PinnedContainerId);
                w.WriteNumber("endpointCount", snapshot.AllGroups.Sum(g => g.Endpoints.Count));
                w.WriteStartArray("groups");
                foreach (DeviceModel group in snapshot.AllGroups)
                {
                    w.WriteStartObject();
                    w.WriteString("containerId", group.ContainerId);
                    w.WriteBoolean("targetCapable", NodeMatch.IsValidTargetContainer(group.ContainerId));
                    w.WriteString("displayName", group.DisplayName);
                    w.WriteString("connection", group.Connection.ToString());
                    w.WriteStartArray("endpoints");
                    foreach (AudioEndpoint endpoint in group.Endpoints)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", endpoint.EndpointId);
                        w.WriteString("flow", endpoint.Flow.ToString());
                        w.WriteString("state", StateText(endpoint.State));
                        w.WriteString("friendlyName", endpoint.FriendlyName);
                        w.WriteString("interfaceName", interfaceNames.GetValueOrDefault(endpoint.EndpointId));
                        w.WriteString("containerId", endpoint.ContainerId);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteDevice(w, "device", snapshot.Target, refresh.Resolution);
                w.WriteString("resolution", refresh.Resolution.ToString());
                WriteSteps(w, "failedSteps", failed);
                w.WriteEndObject();
            });
            return;
        }

        TextWriter o = ctx.Out;
        o.WriteLine("Enumeration: " + (refresh.EnumerationOk ? "ok" : "failed, showing the last known devices"));
        o.WriteLine("Endpoints: " + snapshot.AllGroups.Sum(g => g.Endpoints.Count).ToString(CultureInfo.InvariantCulture) +
                    " (EnumAudioEndpoints eAll, DEVICE_STATEMASK_ALL)");
        o.WriteLine("Device match: \"" + settings.DeviceMatch + "\"");
        o.WriteLine("Pinned container: " + (settings.PinnedContainerId == Guid.Empty ? "none" : TopologyWalk.Format(settings.PinnedContainerId)));

        foreach (DeviceModel group in snapshot.AllGroups)
        {
            o.WriteLine();
            o.WriteLine("Group " + TopologyWalk.Format(group.ContainerId) + " " + GroupLabel(group));
            foreach (AudioEndpoint endpoint in group.Endpoints)
            {
                string? interfaceName = interfaceNames.GetValueOrDefault(endpoint.EndpointId);
                o.WriteLine("  " + endpoint.Flow.ToString().PadRight(8) + StateText(endpoint.State).PadRight(11) +
                            (endpoint.FriendlyName is null ? "(name not readable)" : "\"" + endpoint.FriendlyName + "\"") +
                            (interfaceName is null ? "" : "  interface \"" + interfaceName + "\""));
                o.WriteLine("          id " + endpoint.EndpointId + "  container " + TopologyWalk.Format(endpoint.ContainerId));
            }
        }

        o.WriteLine();
        o.WriteLine(DeviceLine(snapshot.Target, refresh.Resolution));
        WriteStepLines(o, "Failed reads", failed);
    }

    internal static string StateText(EndpointState state) =>
        EndpointModelBuilder.IsKnownState(state)
            ? state.ToString()
            : "0x" + ((uint)state).ToString("X", CultureInfo.InvariantCulture);

    internal static string DeviceLine(DeviceModel? target, TargetResolution resolution)
    {
        if (target is null)
        {
            return resolution == TargetResolution.PinnedAbsent
                ? "Target: none found, the pinned container has no endpoints"
                : "Target: none found";
        }

        return "Target: " + TopologyWalk.Format(target.ContainerId) + " \"" + target.DisplayName + "\", " + target.Connection +
               ", " + (resolution == TargetResolution.Pinned ? "pinned" : "matched by name");
    }

    internal static void WriteDevice(Utf8JsonWriter w, string name, DeviceModel? target, TargetResolution resolution)
    {
        if (target is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("containerId", target.ContainerId);
        w.WriteString("displayName", target.DisplayName);
        w.WriteString("connection", target.Connection.ToString());
        w.WriteString("chosenBy", resolution.ToString());
        w.WriteEndObject();
    }

    internal static void WriteSteps(Utf8JsonWriter w, string name, IEnumerable<StepOutcome> steps)
    {
        w.WriteStartArray(name);
        foreach (StepOutcome step in steps)
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
    }

    internal static void WriteStepLines(TextWriter o, string label, IReadOnlyCollection<StepOutcome> failed)
    {
        o.WriteLine(label + ": " + failed.Count.ToString(CultureInfo.InvariantCulture));
        foreach (StepOutcome step in failed)
        {
            o.WriteLine("  " + CoreAudioDeviceMonitor.Describe(step));
        }
    }

    private static void WriteContainerOrNull(Utf8JsonWriter w, string name, Guid container)
    {
        if (container == Guid.Empty)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, container);
        }
    }

    private static string GroupLabel(DeviceModel group)
    {
        string kind = group.ContainerId == NodeMatch.PcContainer ? "(this PC, never a target) "
                    : group.ContainerId == Guid.Empty ? "(container not readable, never a target) "
                    : "";
        string name = group.DisplayName.Length == 0 ? "(no readable name)" : "\"" + group.DisplayName + "\"";
        return kind + name + ", " + group.Connection;
    }

    private static Dictionary<string, string?> InterfaceNames(IReadOnlyList<EndpointReading> readings)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (EndpointReading reading in readings)
        {
            names[reading.Endpoint.EndpointId] = reading.InterfaceName;
        }

        return names;
    }

    private static void WriteProbeProblem(ProbeContext ctx, string message)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", ctx.Target);
                w.WriteBoolean("available", false);
                w.WriteString("message", message);
                w.WriteEndObject();
            });
        }
        else
        {
            ctx.Out.WriteLine(message);
        }
    }
}
