using System.Diagnostics;
using System.Globalization;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.AudioProtection.Gate;

// protect-on, protect-off and the uninstall restore, inside the gate (SYSTEM, or the elevated uninstall).
// Identity is only ever the validated device.json in the context.
//
//   0. Take the device change lock (DeviceChangeLock), which the block, allow, boot and set-device verbs and
//      uninstall's node allow take too, so no node change runs while a service changes. If another holder
//      still has it after the wait, change nothing.
//   1. Read the target device nodes. If any counts as blocked (BlockedNodes), change nothing: nothing
//      documents BluetoothSetServiceState while the device node is disabled. A protect verb then stores the
//      wanted state in protection-intent.json for the next allow and exits with BlockedExit, or with Failed
//      when that file could not be written.
//   2. Find the remembered device and read its installed services (0 and 234 are success).
//   3. protect-on: turn off Handsfree (0000111E) and Headset (00001108) where they are on; A2DP sink
//      (0000110B) is never touched. Each GUID is written to protection.json before its call, so protection.json
//      lists what Earshot turned off. An entry is taken out again when the call reports the service already
//      off or not on the device, or when the call failed and the read-back in step 4 still lists the service.
//      protect-off and restore: turn back on only what protection.json lists, and take each out once on.
//   4. Read the services again, record the protection state, and settle the entries of failed calls.
// A service whose state already matches is not called, so nothing depends on the already-in-state result,
// but that result (E_INVALIDARG) is still taken as success when an incomplete list made a call necessary.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothenumerateinstalledservices
internal sealed class ProtectionGateRunner
{
    // The exit code for a request refused because the device is blocked and kept for the next allow
    // (device-blocked in the status file). It is only returned once the request has been written to
    // protection-intent.json.
    public const GateExitCode BlockedExit = GateExitCode.DeviceBlocked;

    public const string RefusedStep = "protect-refused";
    public const string DeviceNodeStep = "protect-device-node";
    public const string ServicesAfterStep = "bt-installed-services-after";
    public const string RecordKeptStepPrefix = "protection-record-kept:";

    // How long a protect verb waits for the device change lock: longer than \Earshot\Gate's PT2M limit, so a
    // block, allow or boot block that holds it has finished or been stopped by the scheduler. What is left of
    // \Earshot\Protect's PT5M is for the service calls.
    public static readonly TimeSpan ProtectLockTimeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

    // How long the uninstall restore waits: longer than \Earshot\Protect's PT5M limit, so a protect verb
    // that holds it has finished or been stopped.
    public static readonly TimeSpan RestoreLockTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15);

    private readonly IBluetoothServiceApi _api;
    private readonly Func<TimeSpan, bool> _wait;
    private readonly TimeSpan _protectLockTimeout;
    private readonly TimeSpan _restoreLockTimeout;

    public ProtectionGateRunner(IBluetoothServiceApi api)
        : this(api, DeviceChangeLock.SleepAndContinue, ProtectLockTimeout, RestoreLockTimeout)
    {
    }

    // For tests: the wait between lock attempts and the lock timeouts.
    internal ProtectionGateRunner(IBluetoothServiceApi api, Func<TimeSpan, bool> wait, TimeSpan protectLockTimeout, TimeSpan restoreLockTimeout)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(wait);
        _api = api;
        _wait = wait;
        _protectLockTimeout = protectLockTimeout;
        _restoreLockTimeout = restoreLockTimeout;
    }

    // protect-on (protect true) or protect-off.
    public void Run(GateRunContext ctx, bool protect)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        using DeviceChangeLock? held = DeviceChangeLock.TryAcquire(ctx.Store.Folder, DeviceChangeLockAccess.For(ctx.Nodes), _protectLockTimeout, _wait, ctx.Steps);
        if (held is null)
        {
            ctx.Outcome = GateExitCode.Failed;
            return;
        }

        var intent = new ProtectionIntentFile(ctx.Store.Folder);
        GateExitCode? refused = CheckNodes(ctx);
        if (refused is not null)
        {
            if (refused == BlockedExit)
            {
                StepOutcome kept = intent.Write(protect);
                ctx.Steps.Add(kept);

                // Only a request that is really kept may be reported as saved for the next allow.
                ctx.Outcome = kept.Ok ? BlockedExit : GateExitCode.Failed;
                return;
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

        if (protect)
        {
            (int ok, int failed, List<Guid> unsettled) = TurnOff(ctx, device, record, before, complete);
            ServicesAfter after = ReadBack(ctx, device);
            failed += SettleFailedDisables(ctx, record, unsettled, after);
            Finish(ctx, intent, Summarise(ok, failed));
        }
        else
        {
            (int ok, int failed) = TurnBackOn(ctx, device, record, before);
            ReadBack(ctx, device);
            Finish(ctx, intent, Summarise(ok, failed));
        }
    }

    // Uninstall: turn back on the services protection.json lists. Nothing else is turned on.
    public void Restore(GateRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        using DeviceChangeLock? held = DeviceChangeLock.TryAcquire(ctx.Store.Folder, DeviceChangeLockAccess.For(ctx.Nodes), _restoreLockTimeout, _wait, ctx.Steps);
        if (held is null)
        {
            ctx.Outcome = GateExitCode.Failed;
            return;
        }

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

        if (read.Unselectable.Count > 0)
        {
            // It may be one of the device's nodes, disabled, and nothing documents a service change then.
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep,
                "A node that may belong to the device could not be read, so no service was changed."));
            return GateExitCode.Failed;
        }

        BluetoothNode? deviceNode = read.Nodes.FirstOrDefault(IsDeviceNode);
        if (deviceNode is null)
        {
            ctx.Steps.Add(StepOutcomes.NotAttempted(DeviceNodeStep,
                @"No BTHENUM\DEV_" + ctx.Identity.Address + " node matched the pinned device, so no service was changed."));
            return GateExitCode.NotFound;
        }

        IReadOnlyList<BluetoothNode> blocked = BlockedNodes(read.Nodes);
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

    // The target nodes that rule out a service change. A present node counts when it is at problem 22
    // (CM_PROB_DISABLED), the test BlockStateClassifier uses for Blocked and Mixed, whatever its config flags
    // say. The device node also counts when it is not present but carries CONFIGFLAG_DISABLED, since it comes
    // back disabled. A service node that is not present never counts: the tray ignores it and allow cannot
    // enable it, so counting its flag would refuse every request while the tray shows the AirPods allowed.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
    internal static IReadOnlyList<BluetoothNode> BlockedNodes(IReadOnlyList<BluetoothNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes.Where(n => n.IsPresent
            ? n.Status == NodeBlockStatus.Disabled
            : n.ConfigFlagsDisabledBit && IsDeviceNode(n)).ToList();
    }

    internal static bool IsDeviceNode(BluetoothNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
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

    // Unsettled: services added to protection.json for this call whose call failed. Whether their entries
    // stay is decided after the read-back (SettleFailedDisables).
    private (int Ok, int Failed, List<Guid> Unsettled) TurnOff(GateRunContext ctx, BluetoothDeviceEntry device, ProtectionRecord record, IReadOnlyList<Guid> before, bool complete)
    {
        int ok = 0, failed = 0;
        var unsettled = new List<Guid>();
        foreach (Guid service in ProtectedServices.TurnedOff)
        {
            string step = ServiceStateResults.StepName(service, enable: false);

            // Headset is not advertised by these AirPods, so it is normally skipped here.
            if (complete && !before.Contains(service))
            {
                // Not counted: a skipped service must not turn a failed Handsfree change into a partial result.
                ctx.Steps.Add(ServiceStateResults.Skipped(service, enable: false,
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

            // Only a change is progress. Already off and not on the device are neither progress nor failure, like
            // a service the complete list skips, so they cannot turn a failed Handsfree change into a partial one.
            ServiceChange change = Call(ctx, device, service, enable: false);
            if (change == ServiceChange.Changed)
            {
                ok++;
            }
            else if (!ServiceStateResults.IsOk(change))
            {
                failed++;
            }

            if (!added || change == ServiceChange.Changed)
            {
                continue;
            }

            if (change is ServiceChange.AlreadyInState or ServiceChange.NotSupported)
            {
                // Earshot did not turn it off, so a restore must not turn it on.
                failed += RemoveEntry(ctx, record, service);
            }
            else
            {
                // The call may have removed the driver and still returned an error (the SDK header allows
                // "other WIN32 error"), so the read-back decides.
                unsettled.Add(service);
            }
        }

        return (ok, failed, unsettled);
    }

    // For each disable that failed: the entry is taken out of protection.json only when the read-back lists
    // the service, which proves it is still on. When the read-back does not list it (it may be off because of
    // this call) or could not be read, the entry stays, so a restore still turns it back on. A kept entry for
    // a service that turns out to be on costs nothing: turning back on skips a service the list shows as on,
    // and takes an already-on result as success, and either way drops the entry. Returns the failed writes.
    private static int SettleFailedDisables(GateRunContext ctx, ProtectionRecord record, IReadOnlyList<Guid> unsettled, ServicesAfter after)
    {
        int failed = 0;
        foreach (Guid service in unsettled)
        {
            if (after.Listed && after.Services.Contains(service))
            {
                failed += RemoveEntry(ctx, record, service);
                continue;
            }

            string why = !after.Listed ? "the services could not be read again"
                : after.Complete ? "the complete list read again no longer has it"
                : "the incomplete list read again does not show it";
            string detail = "Its disable call failed, but " + why + ", so Earshot may have turned it off. It stays in protection.json for the restore.";
            ctx.Steps.Add(new StepOutcome(RecordKeptStepPrefix + ProtectedServices.Label(service), Ok: true,
                NativeCodes.NotAttempted, NativeCodes.Name(NativeCodes.NotAttempted), detail));
            ctx.Log.Info(ctx.Verb + ": " + ProtectedServices.Label(service) + ": " + detail);
        }

        return failed;
    }

    // Takes a service out of protection.json. Returns 1 when the write failed.
    private static int RemoveEntry(GateRunContext ctx, ProtectionRecord record, Guid service)
    {
        record.DisabledServices.Remove(service);
        StepOutcome written = ctx.Store.WriteProtection(record);
        ctx.Steps.Add(written);
        return written.Ok ? 0 : 1;
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
                ctx.Steps.Add(ServiceStateResults.Skipped(service, enable: true,
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

    private ServicesAfter ReadBack(GateRunContext ctx, BluetoothDeviceEntry device)
    {
        var steps = new List<StepOutcome>();
        uint rc = ServiceStateReader.ReadServices(_api, device, ServicesAfterStep, steps, out IReadOnlyList<Guid> after);
        bool listed = rc is BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA;
        bool complete = rc == BluetoothApis.ERROR_SUCCESS;
        AudioProtectionState state = ProtectionClassifier.Classify(listed, complete, after);
        foreach (StepOutcome step in steps)
        {
            ctx.Steps.Add(step with { Detail = "Protection " + state + ". " + step.Detail });
        }

        return new ServicesAfter(listed, complete, listed ? after : []);
    }

    // The services read after the calls. Listed: 0 or ERROR_MORE_DATA; Complete: 0.
    private sealed record ServicesAfter(bool Listed, bool Complete, IReadOnlyList<Guid> Services);

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
