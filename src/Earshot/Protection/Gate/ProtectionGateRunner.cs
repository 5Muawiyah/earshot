using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.AudioProtection.Gate;

// Which Bluetooth service API goes with a gate's node API. The gate checks the device nodes through its node
// API before any service call, so the two must describe the same machine: the real CfgMgr32 node API gets the
// real Bluetooth API, and any other node API (a test's fake node table) gets only the Bluetooth API paired
// with it. A gate built over fake nodes therefore can never reach a real BluetoothSetServiceState call; with
// nothing paired, the protect verbs stay not available.
internal static class GateBluetooth
{
    private static readonly ConditionalWeakTable<INodeApi, IBluetoothServiceApi> Paired = new();

    public static IBluetoothServiceApi? For(INodeApi nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes is CfgMgr32NodeApi)
        {
            return new BluetoothServiceApi();
        }

        return Paired.TryGetValue(nodes, out IBluetoothServiceApi? services) ? services : null;
    }

    public static void Pair(INodeApi nodes, IBluetoothServiceApi services)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(services);
        if (nodes is CfgMgr32NodeApi)
        {
            throw new ArgumentException("The real node API always uses the real Bluetooth API.", nameof(nodes));
        }

        Paired.AddOrUpdate(nodes, services);
    }
}

// protect-on, protect-off and the uninstall restore, inside the gate (SYSTEM, or the elevated uninstall).
// Identity is only ever the validated device.json in the context.
//
//   1. Read the target device nodes. If any is disabled, change nothing: nothing documents
//      BluetoothSetServiceState while the device node is disabled. A protect verb then stores the wanted
//      state in protection-intent.json for the next allow and exits with BlockedExit.
//   2. Find the remembered device and read its installed services (0 and 234 are success).
//   3. protect-on: turn off Handsfree (0000111E) and Headset (00001108) where they are on; A2DP sink
//      (0000110B) is never touched. Each GUID is written to protection.json before its call, and taken out
//      again when the call did not change it, so protection.json lists exactly what Earshot turned off.
//      protect-off and restore: turn back on only what protection.json lists, and take each out once on.
//   4. Read the services again and record the protection state.
// A service whose state already matches is not called, so nothing depends on the already-in-state result,
// but that result (E_INVALIDARG) is still taken as success when an incomplete list made a call necessary.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothenumerateinstalledservices
internal sealed class ProtectionGateRunner
{
    // The exit code for a request refused because the device is blocked. It is the code set-device uses for
    // "the pinned device is still disabled"; the tray reads it together with the protect verb.
    public const GateExitCode BlockedExit = GateExitCode.OtherDeviceBlocked;

    public const string RefusedStep = "protect-refused";
    public const string DeviceNodeStep = "protect-device-node";
    public const string ServicesAfterStep = "bt-installed-services-after";

    private readonly IBluetoothServiceApi _api;

    public ProtectionGateRunner(IBluetoothServiceApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    // protect-on (protect true) or protect-off.
    public void Run(GateRunContext ctx, bool protect)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        var intent = new ProtectionIntentFile(ctx.Store.Folder);

        GateExitCode? refused = CheckNodes(ctx);
        if (refused is not null)
        {
            if (refused == BlockedExit)
            {
                ctx.Steps.Add(intent.Write(protect));
            }

            ctx.Outcome = refused.Value;
            return;
        }

        if (!TryReadRecord(ctx, out ProtectionRecord record))
        {
            ctx.Outcome = GateExitCode.Failed;
            return;
        }

        if (!protect && record.DisabledServices.Count == 0)
        {
            ctx.Log.Info(ctx.Verb + ": protection.json lists no service Earshot turned off, so nothing is turned back on.");
            Finish(ctx, intent, GateExitCode.Success);
            return;
        }

        if (!TryPrepare(ctx, out BluetoothDeviceEntry? device, out IReadOnlyList<Guid> before, out bool complete, out GateExitCode failure))
        {
            ctx.Outcome = failure;
            return;
        }

        (int ok, int failed) = protect
            ? TurnOff(ctx, device, record, before, complete)
            : TurnBackOn(ctx, device, record, before);
        ReadBack(ctx, device);
        Finish(ctx, intent, Summarise(ok, failed));
    }

    // Uninstall: turn back on the services protection.json lists. Nothing else is turned on.
    public void Restore(GateRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        StepOutcome? cleared = new ProtectionIntentFile(ctx.Store.Folder).Clear();
        if (cleared is not null)
        {
            ctx.Steps.Add(cleared);
        }

        if (!TryReadRecord(ctx, out ProtectionRecord record))
        {
            ctx.Outcome = GateExitCode.Failed;
            return;
        }

        if (record.DisabledServices.Count == 0)
        {
            ctx.Outcome = GateExitCode.Success;
            return;
        }

        GateExitCode? refused = CheckNodes(ctx);
        if (refused is not null)
        {
            ctx.Outcome = refused.Value;
            return;
        }

        if (!TryPrepare(ctx, out BluetoothDeviceEntry? device, out IReadOnlyList<Guid> before, out _, out GateExitCode failure))
        {
            ctx.Outcome = failure;
            return;
        }

        (int ok, int failed) = TurnBackOn(ctx, device, record, before);
        ReadBack(ctx, device);
        ctx.Outcome = Summarise(ok, failed);
    }

    internal static GateExitCode Summarise(int ok, int failed) =>
        failed == 0 ? GateExitCode.Success
        : ok > 0 ? GateExitCode.Partial
        : GateExitCode.Failed;

    // Null when every target node is enabled and the device node is present; otherwise the exit code.
    private static GateExitCode? CheckNodes(GateRunContext ctx)
    {
        NodeReadResult read = new NodeStateReader(ctx.Nodes).Read(ctx.Identity.ContainerId, ctx.Identity.Address);
        foreach (StepOutcome step in read.Steps)
        {
            ctx.Steps.Add(step);
        }

        ctx.State = BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, read).ToString();
        if (!read.Listed)
        {
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep, "The device nodes could not be read, so no service was changed."));
            return GateExitCode.Failed;
        }

        BluetoothNode? deviceNode = read.Nodes.FirstOrDefault(n => n.InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase));
        if (deviceNode is null)
        {
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep,
                @"No BTHENUM\DEV_" + ctx.Identity.Address + " node matched the pinned device, so no service was changed."));
            return GateExitCode.NotFound;
        }

        List<BluetoothNode> blocked = read.Nodes.Where(n => n.Status == NodeBlockStatus.Disabled || n.ConfigFlagsDisabledBit).ToList();
        if (blocked.Count > 0)
        {
            string what = blocked.Contains(deviceNode) ? "The device node is blocked"
                : blocked.Count == 1 ? "A service node is blocked"
                : blocked.Count.ToString(CultureInfo.InvariantCulture) + " service nodes are blocked";
            ctx.Steps.Add(StepOutcomes.NotAttempted(RefusedStep, what + ", so no Bluetooth service was changed. Allow the device first."));
            return BlockedExit;
        }

        if (!deviceNode.IsPresent)
        {
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep,
                "The device node is not present, so no service was changed. Connect the device to this PC once."));
            return GateExitCode.NotPresent;
        }

        if (deviceNode.Status == NodeBlockStatus.Unknown)
        {
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep, "The device node status could not be read, so no service was changed."));
            return GateExitCode.Failed;
        }

        return null;
    }

    // protection.json, or an empty record when there is none. An invalid or unreadable file is never
    // overwritten: it may be the only record of what to turn back on.
    private static bool TryReadRecord(GateRunContext ctx, out ProtectionRecord record)
    {
        GateRead<ProtectionRecord> read = ctx.Store.ReadProtection();
        if (read.IsOk && read.Value is not null)
        {
            record = read.Value;
            return true;
        }

        record = new ProtectionRecord();
        if (read.Status == GateReadStatus.Missing)
        {
            return true;
        }

        ctx.Steps.Add(read.Step);
        return false;
    }

    private bool TryPrepare(
        GateRunContext ctx,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BluetoothDeviceEntry? device,
        out IReadOnlyList<Guid> services,
        out bool complete,
        out GateExitCode failure)
    {
        services = [];
        complete = false;
        device = BluetoothDeviceLookup.Find(_api, ctx.Identity.Address, ctx.Steps, out bool listFailed);
        if (device is null)
        {
            failure = listFailed ? GateExitCode.Failed : GateExitCode.NotFound;
            return false;
        }

        uint rc = ServiceStateReader.ReadServices(_api, device, ServiceStateReader.ServicesStep, ctx.Steps, out services);
        if (rc is not (BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA))
        {
            failure = GateExitCode.Failed;
            return false;
        }

        complete = rc == BluetoothApis.ERROR_SUCCESS;
        failure = GateExitCode.Success;
        return true;
    }

    private (int Ok, int Failed) TurnOff(GateRunContext ctx, BluetoothDeviceEntry device, ProtectionRecord record, IReadOnlyList<Guid> before, bool complete)
    {
        int ok = 0, failed = 0;
        foreach (Guid service in ProtectedServices.TurnedOff)
        {
            string step = ServiceStateResults.StepName(service, enable: false);

            // Headset is not advertised by these AirPods, so it is normally skipped here.
            if (complete && !before.Contains(service))
            {
                // Not counted: a skipped service must not turn a failed Handsfree change into a partial result.
                ctx.Steps.Add(StepOutcomes.FromWin32(step, BluetoothApis.ERROR_SUCCESS,
                    "Not in the complete installed service list, so it is already off and was not called."));
                continue;
            }

            // Recorded before the call: the call installs or removes a driver for an undocumented time and may
            // outlive the task's time limit, and restore must still know Earshot turned this service off.
            bool added = !record.DisabledServices.Contains(service);
            if (added)
            {
                record.DisabledServices.Add(service);
                StepOutcome written = ctx.Store.WriteProtection(record);
                ctx.Steps.Add(written);
                if (!written.Ok)
                {
                    record.DisabledServices.Remove(service);
                    ctx.Steps.Add(StepOutcomes.NotAttempted(step, "protection.json could not be written first, so the service was not changed."));
                    failed++;
                    continue;
                }
            }

            ServiceChange change = Call(ctx, device, service, enable: false);
            if (ServiceStateResults.IsOk(change))
            {
                ok++;
            }
            else
            {
                failed++;
            }

            if (added && change != ServiceChange.Changed)
            {
                // Earshot did not turn it off, so a restore must not turn it on.
                record.DisabledServices.Remove(service);
                StepOutcome undone = ctx.Store.WriteProtection(record);
                ctx.Steps.Add(undone);
                if (!undone.Ok)
                {
                    failed++;
                }
            }
        }

        return (ok, failed);
    }

    private (int Ok, int Failed) TurnBackOn(GateRunContext ctx, BluetoothDeviceEntry device, ProtectionRecord record, IReadOnlyList<Guid> before)
    {
        int ok = 0, failed = 0;
        foreach (Guid service in record.DisabledServices.ToList())
        {
            string step = ServiceStateResults.StepName(service, enable: true);
            if (!ProtectedServices.IsTurnedOff(service))
            {
                ctx.Steps.Add(StepOutcomes.NotAttempted(step,
                    "Earshot only turns Handsfree and Headset back on, so this entry was left in protection.json."));
                failed++;
                continue;
            }

            bool on;
            if (before.Contains(service))
            {
                ctx.Steps.Add(StepOutcomes.FromWin32(step, BluetoothApis.ERROR_SUCCESS,
                    "Already in the installed service list, so it is on and was not called."));
                on = true;
            }
            else
            {
                on = ServiceStateResults.IsOk(Call(ctx, device, service, enable: true));
            }

            if (!on)
            {
                failed++;
                continue;
            }

            ok++;
            record.DisabledServices.Remove(service);
            StepOutcome written = ctx.Store.WriteProtection(record);
            ctx.Steps.Add(written);
            if (!written.Ok)
            {
                failed++;
            }
        }

        return (ok, failed);
    }

    private ServiceChange Call(GateRunContext ctx, BluetoothDeviceEntry device, Guid service, bool enable)
    {
        long started = Stopwatch.GetTimestamp();
        uint rc = _api.SetServiceState(device, service, enable);
        StepOutcome step = ServiceStateResults.Step(service, enable, rc, Stopwatch.GetElapsedTime(started));
        ctx.Steps.Add(step);
        if (step.Ok)
        {
            // A failure is logged with the other failed steps by the caller of the hook.
            ctx.Log.Info(ctx.Verb + ": " + GateActions.Describe(step));
        }

        return ServiceStateResults.Map(rc);
    }

    private void ReadBack(GateRunContext ctx, BluetoothDeviceEntry device)
    {
        var steps = new List<StepOutcome>();
        uint rc = ServiceStateReader.ReadServices(_api, device, ServicesAfterStep, steps, out IReadOnlyList<Guid> after);
        bool listed = rc is BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA;
        AudioProtectionState state = ProtectionClassifier.Classify(listed, rc == BluetoothApis.ERROR_SUCCESS, after);
        foreach (StepOutcome step in steps)
        {
            ctx.Steps.Add(step with { Detail = "Protection " + state + ". " + step.Detail });
        }
    }

    private static void Finish(GateRunContext ctx, ProtectionIntentFile intent, GateExitCode outcome)
    {
        // A completed request replaces any request kept from while the device was blocked.
        if (outcome == GateExitCode.Success && intent.Clear() is { } cleared)
        {
            ctx.Steps.Add(cleared);
        }

        ctx.Outcome = outcome;
    }
}
