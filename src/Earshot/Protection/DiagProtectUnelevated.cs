using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;

namespace Earshot;

// diag protect-unelevated <on|off>: LIVE, for the owner's live test only. Calls BluetoothSetServiceState in
// this process, without elevation, to find out whether protection could work without the SYSTEM task. The
// shipped feature never does this; it always goes through \Earshot\Protect.
//
//   on   BLUETOOTH_SERVICE_DISABLE on Handsfree (0000111E), then on Headset (00001108)
//   off  BLUETOOTH_SERVICE_ENABLE on Handsfree (0000111E); Headset is not advertised by these AirPods, so
//        the on evidence shows whether its call changed anything
// A2DP sink (0000110B) is never called. Every call is made whatever the installed list says, because the raw
// return is the evidence (0, 5, 1060 or E_INVALIDARG). Each call passes a NULL radio handle, as the shipped gate
// does; when that call returns ERROR_INVALID_PARAMETER the same call is made once more with the first local
// radio's handle, so the evidence tells a rejected NULL radio from rejected flags. The JSON evidence holds each
// raw return with its name, radio and duration, and the installed services and node state before and after.
//
// It refuses to run elevated or as SYSTEM (the evidence would not be unelevated), and refuses while the
// device counts as blocked by the rule the gate keeps (ProtectionGateRunner.BlockedNodes). Program.RunDiag
// refuses every diag target in safe mode before this runs. The evidence file's folder is made before any
// call, so a folder that cannot be made stops the run with nothing called; if the file itself cannot be
// written after the calls, the whole JSON goes to the log and to the output instead.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal static partial class Program
{
    internal sealed record DiagProtectCall(Guid Service, bool Enable);

    internal sealed record DiagProtectPlan(bool Protect, IReadOnlyList<DiagProtectCall> Calls);

    // Radio: "null" for the call with no radio handle, "first-radio" for the call with the first radio's handle.
    private sealed record DiagProtectResult(DiagProtectCall Call, string Radio, uint Code, long Milliseconds, DateTimeOffset StartedUtc);

    internal const string DiagNullRadio = "null";
    internal const string DiagFirstRadio = "first-radio";

    // True when a NULL-radio call's result calls for the same call with a radio handle.
    internal static bool RetryWithRadioHandle(uint rc) => rc == BluetoothApis.ERROR_INVALID_PARAMETER;

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

        string evidence;
        try
        {
            evidence = ctx.NewEvidenceFile("protect-unelevated-" + ctx.Args[0]);
        }
        catch (IOException ex)
        {
            ctx.ExitCode = ReportEvidenceFolderProblem(ctx, log, ex);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ctx.ExitCode = ReportEvidenceFolderProblem(ctx, log, ex);
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
                results.Add(new DiagProtectResult(call, DiagNullRadio, rc, ms, callStarted));
                StepOutcome step = ServiceStateResults.Step(call.Service, call.Enable, rc, TimeSpan.FromMilliseconds(ms));
                steps.Add(step);
                log.Info("diag protect-unelevated: " + GateActions.Describe(step));

                if (!RetryWithRadioHandle(rc))
                {
                    continue;
                }

                callStarted = DateTimeOffset.UtcNow;
                t0 = Stopwatch.GetTimestamp();
                uint? radioRc = BluetoothServiceApi.SetServiceStateOnFirstRadio(entry, call.Service, call.Enable, out uint radioError, out uint closeError);
                ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (closeError != BluetoothApis.ERROR_SUCCESS)
                {
                    steps.Add(StepOutcomes.FromWin32("bt-radio-close", closeError, "A radio handle did not close."));
                }

                if (radioRc is not uint withRadio)
                {
                    StepOutcome noRadio = StepOutcomes.FromWin32("bt-find-radio", radioError, "No radio handle, so the call was not repeated with one.");
                    steps.Add(noRadio);
                    log.Warn("diag protect-unelevated: " + GateActions.Describe(noRadio));
                    continue;
                }

                results.Add(new DiagProtectResult(call, DiagFirstRadio, withRadio, ms, callStarted));
                StepOutcome radioStep = ServiceStateResults.Step(call.Service, call.Enable, withRadio, TimeSpan.FromMilliseconds(ms));
                radioStep = radioStep with { Step = radioStep.Step + ":" + DiagFirstRadio };
                steps.Add(radioStep);
                log.Info("diag protect-unelevated: " + GateActions.Describe(radioStep));
            }

            afterCode = ServiceStateReader.ReadServices(api, entry, "bt-installed-services-after", steps, out servicesAfter);
            nodesAfter = reader.Read(identity.ContainerId, identity.Address);
            steps.AddRange(nodesAfter.Steps);
        }

        DateTimeOffset finished = DateTimeOffset.UtcNow;
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
                w.WriteString("radio", result.Radio);
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
        bool saved = WriteDiagEvidence(evidence, json, log, ctx.Out);

        if (nodeRefusal is not null)
        {
            ctx.Out.WriteLine(nodeRefusal);
        }

        foreach (DiagProtectResult result in results)
        {
            ctx.Out.WriteLine(ProtectedServices.Label(result.Call.Service) + (result.Call.Enable ? " enable" : " disable") +
                              (result.Radio == DiagFirstRadio ? " with the radio handle: " : ": ") +
                              NativeCodes.Win32(result.Code) + " in " + result.Milliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        if (saved)
        {
            ctx.Out.WriteLine("Evidence: " + evidence);
        }

        log.Info("diag protect-unelevated " + ctx.Args[0] + ": " + results.Count + " calls, evidence " + (saved ? evidence : "in the log only"));
        ctx.ExitCode = !saved ? ExitCodes.IoError
            : nodeRefusal is not null ? ExitCodes.Refused
            : entry is null ? ExitCodes.OsError
            : plan.Calls.All(call => results.Any(r => r.Call == call && ServiceStateResults.IsOk(ServiceStateResults.Map(r.Code)))) ? ExitCodes.Ok
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

    // Writes the evidence JSON. When the file cannot be written, the whole JSON is logged at Warn with the
    // failure and written to the output, so the record of a single-shot live call is never lost. False then.
    internal static bool WriteDiagEvidence(string path, string json, ILog log, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(output);
        string problem;
        try
        {
            File.WriteAllText(path, json);
            return true;
        }
        catch (IOException ex)
        {
            problem = NativeCodes.Name(ex.HResult) + " " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = NativeCodes.Name(ex.HResult) + " " + ex.Message;
        }

        log.Warn("diag protect-unelevated: the evidence could not be written to " + path + " (" + problem + "). Evidence: " + json);
        output.WriteLine("The evidence could not be written to " + path + ". It is in the log and below.");
        output.WriteLine(json);
        return false;
    }

    private static int ReportEvidenceFolderProblem(DiagContext ctx, ILog log, Exception ex)
    {
        string message = "The evidence folder " + ctx.EvidenceFolder + " could not be made, so no service was called.";
        log.Error("diag protect-unelevated: " + message + " " + NativeCodes.Name(ex.HResult) + " " + ex.Message);
        ctx.Out.WriteLine(message);
        return ExitCodes.IoError;
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

    // Why the device's nodes rule the call out, or null when none counts as blocked and the device node is
    // present and enabled.
    internal static string? DiagProtectNodeRefusal(NodeReadResult nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!nodes.Listed)
        {
            return "The device nodes could not be read, so no service was called.";
        }

        BluetoothNode? deviceNode = nodes.Nodes.FirstOrDefault(ProtectionGateRunner.IsDeviceNode);
        if (deviceNode is null)
        {
            return "No device node matched the pinned device, so no service was called.";
        }

        if (ProtectionGateRunner.BlockedNodes(nodes.Nodes).Count > 0)
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
