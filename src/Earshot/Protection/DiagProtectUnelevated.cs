using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// diag protect-unelevated <on|off>: LIVE, for the owner's live test only. Calls BluetoothSetServiceState in
// this process, without elevation, to find out whether protection could work without the SYSTEM task. The
// shipped feature never does this; it always goes through \Earshot\Protect.
//
//   on   BLUETOOTH_SERVICE_DISABLE on Handsfree (0000111E), then on Headset (00001108)
//   off  BLUETOOTH_SERVICE_ENABLE on Handsfree (0000111E); Headset is not advertised by these AirPods, so
//        the on evidence shows whether its call changed anything
// A2DP sink (0000110B) is never called. Every call is made whatever the installed list says, because the raw
// return is the evidence (0, 5, 1060 or E_INVALIDARG). The JSON evidence holds each raw return with its name
// and duration, and the installed services and node state before and after.
//
// It refuses to run elevated or as SYSTEM (the evidence would not be unelevated), and refuses while any
// target node of the device is disabled, the same rule the gate keeps. Program.RunDiag refuses every diag
// target in safe mode before this runs.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal static partial class Program
{
    internal sealed record DiagProtectCall(Guid Service, bool Enable);

    internal sealed record DiagProtectPlan(bool Protect, IReadOnlyList<DiagProtectCall> Calls);

    private sealed record DiagProtectResult(DiagProtectCall Call, uint Code, long Milliseconds, DateTimeOffset StartedUtc);

    static partial void DiagProtectUnelevated(DiagContext ctx)
    {
        ctx.Handled = true;
        ILog log = ctx.Services.Log;
        if (!TryPlanDiagProtect(ctx.Args, out DiagProtectPlan? plan, out string? error))
        {
            log.Warn("diag protect-unelevated: " + error);
            ctx.Out.WriteLine(error);
            ctx.Out.WriteLine(DiagUsage);
            ctx.ExitCode = ExitCodes.Usage;
            return;
        }

        string? refusal = DiagProtectRefusal(WindowsProcessToken.Current());
        if (refusal is not null)
        {
            log.Warn("diag protect-unelevated: refused. " + refusal);
            ctx.Out.WriteLine(refusal);
            ctx.ExitCode = ExitCodes.Refused;
            return;
        }

        Paths paths = Paths.Current;
        EarshotSettings settings = ctx.Services.Settings.Current;
        GateRead<DeviceIdentity> device = new GateStore(paths.MachineFolder).ReadDevice();
        var pinned = new DeviceIdentity { Address = settings.PinnedAddress, ContainerId = settings.PinnedContainerId };
        DeviceIdentity? identity = device.IsOk ? device.Value : GateStore.ValidateDevice(pinned) is null ? pinned : null;
        if (identity is null)
        {
            const string noDevice = "No valid device is pinned in device.json or the settings.";
            log.Warn("diag protect-unelevated: " + noDevice);
            ctx.Out.WriteLine(noDevice);
            ctx.ExitCode = ExitCodes.Config;
            return;
        }

        var steps = new List<StepOutcome>();
        var reader = new NodeStateReader(new CfgMgr32NodeReader());
        var api = new BluetoothServiceApi();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        NodeReadResult nodesBefore = reader.Read(identity.ContainerId, identity.Address);
        steps.AddRange(nodesBefore.Steps);
        string? nodeRefusal = DiagProtectNodeRefusal(nodesBefore);
        BluetoothDeviceEntry? entry = null;
        IReadOnlyList<Guid> servicesBefore = [];
        IReadOnlyList<Guid> servicesAfter = [];
        uint beforeCode = 0, afterCode = 0;
        var results = new List<DiagProtectResult>();
        NodeReadResult? nodesAfter = null;

        if (nodeRefusal is null)
        {
            entry = BluetoothDeviceLookup.Find(api, identity.Address, steps, out _);
        }

        if (entry is not null)
        {
            beforeCode = ServiceStateReader.ReadServices(api, entry, "bt-installed-services-before", steps, out servicesBefore);
            foreach (DiagProtectCall call in plan.Calls)
            {
                DateTimeOffset callStarted = DateTimeOffset.UtcNow;
                long t0 = Stopwatch.GetTimestamp();
                uint rc = api.SetServiceState(entry, call.Service, call.Enable);
                long ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                results.Add(new DiagProtectResult(call, rc, ms, callStarted));
                StepOutcome step = ServiceStateResults.Step(call.Service, call.Enable, rc, TimeSpan.FromMilliseconds(ms));
                steps.Add(step);
                log.Info("diag protect-unelevated: " + GateActions.Describe(step));
            }

            afterCode = ServiceStateReader.ReadServices(api, entry, "bt-installed-services-after", steps, out servicesAfter);
            nodesAfter = reader.Read(identity.ContainerId, identity.Address);
            steps.AddRange(nodesAfter.Steps);
        }

        DateTimeOffset finished = DateTimeOffset.UtcNow;
        string evidence = ctx.NewEvidenceFile("protect-unelevated-" + ctx.Args[0]);
        string json = ProbeContext.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", "protect-unelevated");
            w.WriteString("mode", ctx.Args[0]);
            w.WriteBoolean("elevated", false);
            w.WriteString("address", identity.Address);
            w.WriteString("identityFrom", device.IsOk ? "device.json" : "settings");
            w.WriteString("startedUtc", started.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("finishedUtc", finished.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("refused", nodeRefusal);
            w.WriteBoolean("deviceFound", entry is not null);
            w.WriteString("deviceName", entry?.Name);
            w.WriteBoolean("connected", entry?.Connected ?? false);
            WriteDiagServices(w, "servicesBefore", entry is null ? null : (uint?)beforeCode, servicesBefore);
            w.WriteStartArray("calls");
            foreach (DiagProtectResult result in results)
            {
                w.WriteStartObject();
                w.WriteString("service", result.Call.Service.ToString("D"));
                w.WriteString("label", ProtectedServices.Label(result.Call.Service));
                w.WriteString("flags", result.Call.Enable ? "BLUETOOTH_SERVICE_ENABLE" : "BLUETOOTH_SERVICE_DISABLE");
                w.WriteString("startedUtc", result.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
                w.WriteNumber("milliseconds", result.Milliseconds);
                w.WriteString("returnCode", "0x" + result.Code.ToString("X8", CultureInfo.InvariantCulture));
                w.WriteNumber("returnValue", result.Code);
                w.WriteString("returnName", NativeCodes.Win32(result.Code));
                w.WriteString("meaning", ServiceStateResults.Map(result.Code).ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteDiagServices(w, "servicesAfter", entry is null ? null : (uint?)afterCode, servicesAfter);
            WriteDiagNodes(w, "nodesBefore", nodesBefore);
            WriteDiagNodes(w, "nodesAfter", nodesAfter);
            WriteSteps(w, "steps", steps);
            w.WriteEndObject();
        });
        File.WriteAllText(evidence, json);

        if (nodeRefusal is not null)
        {
            ctx.Out.WriteLine(nodeRefusal);
        }

        foreach (DiagProtectResult result in results)
        {
            ctx.Out.WriteLine(ProtectedServices.Label(result.Call.Service) + (result.Call.Enable ? " enable: " : " disable: ") +
                              NativeCodes.Win32(result.Code) + " in " + result.Milliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        ctx.Out.WriteLine("Evidence: " + evidence);
        log.Info("diag protect-unelevated " + ctx.Args[0] + ": " + results.Count + " calls, evidence " + evidence);
        ctx.ExitCode = nodeRefusal is not null ? ExitCodes.Refused
            : entry is null ? ExitCodes.OsError
            : results.All(r => ServiceStateResults.IsOk(ServiceStateResults.Map(r.Code))) ? ExitCodes.Ok
            : ExitCodes.Software;
    }

    // The calls for on or off. Exactly one argument, "on" or "off".
    internal static bool TryPlanDiagProtect(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out DiagProtectPlan? plan,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        plan = null;
        if (args.Count != 1 || args[0] is not ("on" or "off"))
        {
            error = "diag protect-unelevated needs on or off.";
            return false;
        }

        bool protect = args[0] == "on";
        plan = protect
            ? new DiagProtectPlan(true, [new DiagProtectCall(ProtectedServices.Handsfree, Enable: false), new DiagProtectCall(ProtectedServices.Headset, Enable: false)])
            : new DiagProtectPlan(false, [new DiagProtectCall(ProtectedServices.Handsfree, Enable: true)]);
        error = null;
        return true;
    }

    // Why this process must not make the unelevated call, or null when it may.
    internal static string? DiagProtectRefusal(IProcessToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.IsLocalSystem)
        {
            return "diag protect-unelevated records an unelevated call, and this process runs as SYSTEM.";
        }

        return token.IsElevatedAdministrator
            ? "diag protect-unelevated records an unelevated call, and this process is elevated. Run it from a prompt that is not elevated."
            : null;
    }

    // Why the device's nodes rule the call out, or null when every target node is enabled and present.
    internal static string? DiagProtectNodeRefusal(NodeReadResult nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!nodes.Listed)
        {
            return "The device nodes could not be read, so no service was called.";
        }

        BluetoothNode? deviceNode = nodes.Nodes.FirstOrDefault(n => n.InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase));
        if (deviceNode is null)
        {
            return "No device node matched the pinned device, so no service was called.";
        }

        if (nodes.Nodes.Any(n => n.Status == NodeBlockStatus.Disabled || n.ConfigFlagsDisabledBit))
        {
            return "The AirPods are blocked, so no service was called. Allow them first.";
        }

        return deviceNode.IsPresent && deviceNode.Status == NodeBlockStatus.Enabled
            ? null
            : "The device node is not present or its status is unreadable, so no service was called.";
    }

    private static void WriteDiagServices(Utf8JsonWriter w, string name, uint? code, IReadOnlyList<Guid> services)
    {
        if (code is not uint rc)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("returnName", NativeCodes.Win32(rc));
        w.WriteNumber("returnValue", rc);
        w.WriteString("protection", ProtectionClassifier.Classify(rc is 0 or 234, rc == 0, services).ToString());
        w.WriteStartArray("installed");
        foreach (Guid service in services)
        {
            w.WriteStringValue(service.ToString("D"));
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
