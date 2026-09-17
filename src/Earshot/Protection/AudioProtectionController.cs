using System.Security.Principal;
using Earshot.AudioProtection.Gate;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.AudioProtection;

// Protect audio quality as the tray sees it. Status is a non-elevated read of the device's installed
// Bluetooth services. A change never runs in this process: it starts \Earshot\Protect with protect-on or
// protect-off (RunEx on a system worker thread, never the UI thread) and is then confirmed by reading the
// services again, never by trusting the task's result.
//
// The identity the gate acts on is device.json. Status falls back to the tray's pinned settings only so the
// state can be shown before setup.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
internal sealed class AudioProtectionController : IAudioProtectionController, IDisposable
{
    internal const string NotSetUpMessage = "Protect audio quality is not set up yet. Choose Set up Earshot.";
    internal const string NeedsRepairMessage = "Protect audio quality needs repair. Choose Set up Earshot.";
    internal const string CouldNotStartMessage = "The protection task did not start. Choose Set up Earshot.";
    internal const string ProtectedMessage = "Audio quality protected";
    internal const string AlreadyProtectedMessage = "Audio quality is already protected";
    internal const string OffMessage = "Audio quality protection is off";
    internal const string AlreadyOffMessage = "Audio quality protection is already off";
    internal const string SavedForAllowMessage = "Saved. It applies when the AirPods are allowed.";
    internal const string SaveFailedMessage = "The AirPods are blocked and the change could not be saved. Try again.";
    internal const string BusyMessage = "Another change to the AirPods is still running. Try again.";
    internal const string OffOutsideEarshotMessage = "Handsfree was turned off outside Earshot, so it stays off.";
    internal const string PartialMessage = "Handsfree is off, but Headset is still on. Try again.";
    internal const string ProtectFailedMessage = "Could not protect audio quality. Try again.";
    internal const string OffFailedMessage = "Could not turn protection off. Try again.";
    internal const string ProtectedWithProblemMessage = "Audio quality protected, but not every step worked. Try again.";
    internal const string OffWithProblemMessage = "Protection is off, but not every step worked. Try again.";
    internal const string UnreadableMessage = "Could not read the AirPods services. Try again.";
    internal const string TimedOutMessage = "Protection did not finish in time. Try again.";
    internal const string CancelledMessage = "Stopped waiting for protection.";

    private readonly ILog _log;
    private readonly ISettingsStore _settings;
    private readonly GateStore _store;
    private readonly ServiceStateReader _reader;
    private readonly TaskSchedulerGate _gate;
    private readonly ISystemWorker _worker;
    private readonly IDisposable? _ownedWorker;
    private readonly Lock _statusLock = new();
    private string _lastStatusProblems = "";

    public AudioProtectionController(
        ILog log,
        ISettingsStore settings,
        GateStore store,
        ServiceStateReader reader,
        TaskSchedulerGate gate,
        ISystemWorker worker,
        IDisposable? ownedWorker = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(worker);
        _log = log;
        _settings = settings;
        _store = store;
        _reader = reader;
        _gate = gate;
        _worker = worker;
        _ownedWorker = ownedWorker;
    }

    // The real controller with a worker of its own. Constructing it reads the current user's SID and nothing
    // else; its worker thread starts on the first change.
    public static AudioProtectionController Create(ILog log, ISettingsStore settings, Paths paths)
    {
        var worker = new SystemWorker(log);
        return Create(log, settings, paths, worker, worker);
    }

    // The real controller on a worker shared with the block controller, so a protection change and a block
    // or allow requested by this tray queue behind each other rather than run side by side. The caller owns
    // the worker and disposes it.
    public static AudioProtectionController Create(ILog log, ISettingsStore settings, Paths paths, ISystemWorker sharedWorker) =>
        Create(log, settings, paths, sharedWorker, ownedWorker: null);

    private static AudioProtectionController Create(ILog log, ISettingsStore settings, Paths paths, ISystemWorker worker, IDisposable? ownedWorker)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(worker);
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        var store = new GateStore(paths.MachineFolder);
        var gate = new TaskSchedulerGate(new ComScheduledTasks(), store, paths.InstallFolder, sid,
            AccountSids.Translate, TimeProvider.System, TaskSchedulerGate.WaitOrCancelled, log);
        return new AudioProtectionController(log, settings, store, new ServiceStateReader(new BluetoothServiceReader()), gate, worker, ownedWorker);
    }

    // A fast read (the device list and one service list), so it runs on the thread pool rather than queueing
    // behind a protection change that may take minutes on the system worker.
    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) =>
        Task.Run(ReadStatus, ct);

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) =>
        _worker.RunAsync(token => Apply(protect, token), ct);

    // The thread its gate requests run on, shared with the block controller by the composition root.
    internal ISystemWorker Worker => _worker;

    // The protection state asked for while the device was blocked and not applied yet (protection-intent.json,
    // written only by the gate). Missing when nothing is pending. A read, so it runs on the thread pool. What
    // applies it after an allow is the caller's: see ProtectionPolicy (IntentPending).
    public Task<GateRead<ProtectionIntent>> GetPendingIntentAsync(CancellationToken ct = default) =>
        Task.Run(() => new ProtectionIntentFile(_store.Folder).Read(), ct);

    // The same kept request as the contract reports it: true to protect, false to restore, null when none is kept.
    // A file that is there but cannot be read or is not valid throws an IOException carrying the read's own code,
    // which the block coordinator records and logs. Read-only, so safe mode passes it through.
    public async Task<bool?> GetPendingProtectAsync(CancellationToken ct = default) =>
        KeptRequest(await GetPendingIntentAsync(ct).ConfigureAwait(false));

    internal static bool? KeptRequest(GateRead<ProtectionIntent> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.Status switch
        {
            GateReadStatus.Ok when read.Value is { } intent => intent.Protect,
            GateReadStatus.Missing => null,
            _ => throw new IOException(
                "The kept protection request could not be read (" + read.Status + "): " + read.Step.CodeName + (read.Step.Detail is null ? "" : ", " + read.Step.Detail),
                read.Step.Code),
        };
    }

    public void Dispose() => _ownedWorker?.Dispose();

    internal AudioProtectionSnapshot ReadStatus()
    {
        var steps = new List<StepOutcome>();
        DeviceIdentity? identity = ResolveIdentity(steps);
        ServiceReadResult read = identity is null ? ServiceReadResult.NotRead("", []) : _reader.Read(identity.Address);
        steps.AddRange(read.Steps);
        AudioProtectionSnapshot snapshot = ProtectionClassifier.Snapshot(read);

        string problems = string.Join(" | ", steps.Where(s => !s.Ok).Select(GateActions.Describe));
        lock (_statusLock)
        {
            if (!string.Equals(problems, _lastStatusProblems, StringComparison.Ordinal))
            {
                _lastStatusProblems = problems;
                if (problems.Length > 0)
                {
                    _log.Warn("audio protection status " + snapshot.State + ": " + problems);
                }
            }
        }

        return snapshot;
    }

    // device.json when valid (what the gate acts on), otherwise the pinned settings, otherwise none.
    private DeviceIdentity? ResolveIdentity(List<StepOutcome> steps)
    {
        GateRead<DeviceIdentity> device = _store.ReadDevice();
        if (device.IsOk)
        {
            return device.Value;
        }

        if (device.Status != GateReadStatus.Missing)
        {
            steps.Add(device.Step);
        }

        EarshotSettings settings = _settings.Current;
        var pinned = new DeviceIdentity { Address = settings.PinnedAddress, ContainerId = settings.PinnedContainerId };
        return GateStore.ValidateDevice(pinned) is null ? pinned : null;
    }

    private ControllerResult Apply(bool protect, CancellationToken ct)
    {
        string verb = protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff;
        var steps = new List<StepOutcome>();

        GateRead<DeviceIdentity> device = _store.ReadDevice();
        if (!device.IsOk || device.Value is null)
        {
            steps.Add(device.Step);
            TaskVerification check = _gate.Verify(TaskPlan.ProtectTaskName);
            steps.AddRange(check.Steps);
            return Finish(verb, ControllerResult.Fail(check.Health == TaskHealth.Missing ? NotSetUpMessage : NeedsRepairMessage, steps));
        }

        string address = device.Value.Address;
        ServiceReadResult before = _reader.Read(address);
        AudioProtectionState beforeState = ProtectionClassifier.Snapshot(before).State;
        bool satisfied = ProtectionPolicy.IsSatisfied(protect, beforeState);

        // A request kept from while the device was blocked would otherwise stay pending: one that differs would be
        // applied at the next allow, and one that matches would keep reporting a pending change. So whenever one is
        // kept the gate is started, even when the services already match; it clears the request on success and
        // keeps (or rewrites) it while the device is still blocked.
        GateRead<ProtectionIntent> intent = new ProtectionIntentFile(_store.Folder).Read();
        bool intentKept = intent.Status != GateReadStatus.Missing;
        if (intentKept)
        {
            steps.Add(intent.Step);
        }

        // Turning off only turns back on what Earshot turned off. protection.json can still list a service
        // that Windows has since turned back on; the gate drops such an entry without calling the service, so
        // a later off outside Earshot is never undone by a restore.
        bool nothingRecorded = false;
        bool staleRecord = false;
        if (!protect)
        {
            GateRead<ProtectionRecord> record = _store.ReadProtection();
            bool listsTurnedOff = record.IsOk && record.Value is not null && record.Value.DisabledServices.Any(ProtectedServices.IsTurnedOff);
            nothingRecorded = record.Status == GateReadStatus.Missing || (record.IsOk && !listsTurnedOff);
            staleRecord = satisfied && listsTurnedOff;
        }

        if (satisfied && !intentKept && !staleRecord)
        {
            return Finish(verb, ControllerResult.Already(protect ? AlreadyProtectedMessage : AlreadyOffMessage));
        }

        // When Handsfree is off and protection.json lists nothing, the gate would change nothing, so it is not
        // started unless a kept request has to be replaced or cleared.
        bool offOutsideEarshot = !protect && nothingRecorded && beforeState is AudioProtectionState.Protected or AudioProtectionState.Partial;
        if (offOutsideEarshot && !intentKept)
        {
            steps.AddRange(before.Steps);
            steps.Add(StepOutcomes.NotAttempted(verb, "protection.json lists no service Earshot turned off."));
            return Finish(verb, new ControllerResult(OpStatus.NotAttempted, OffOutsideEarshotMessage, steps));
        }

        GateRunResult run = _gate.Run(TaskPlan.ProtectTaskName, verb, Guid.NewGuid().ToString("N"), null, TaskSchedulerGate.ProtectTimeout, ct);
        steps.AddRange(run.Steps);
        if (run.Status is not null)
        {
            steps.AddRange(run.Status.Steps);
        }

        switch (run.Outcome)
        {
            case GateRunOutcome.NotSetUp:
                return Finish(verb, ControllerResult.Fail(NotSetUpMessage, steps));
            case GateRunOutcome.NeedsRepair:
                return Finish(verb, ControllerResult.Fail(NeedsRepairMessage, steps));
            case GateRunOutcome.RunFailed:
            case GateRunOutcome.TaskDisabled:
                return Finish(verb, ControllerResult.Fail(CouldNotStartMessage, steps));
        }

        if (run.Status is { } status)
        {
            if (status.ExitCode == (int)ProtectionGateRunner.BlockedExit)
            {
                return Finish(verb, new ControllerResult(OpStatus.NotAttempted, SavedForAllowMessage, steps));
            }

            // The gate changed nothing: another device change held the lock, or the device was blocked and
            // the request could not be kept.
            if (BlockController.IsBusy(run))
            {
                return Finish(verb, ControllerResult.Fail(BusyMessage, steps));
            }

            if (status.Steps.Any(s => s.Step == ProtectionGateRunner.RefusedStep))
            {
                return Finish(verb, ControllerResult.Fail(SaveFailedMessage, steps));
            }
        }

        // Ground truth: the services read now, not the task's result.
        ServiceReadResult after = _reader.Read(address);
        steps.AddRange(after.Steps);
        AudioProtectionState afterState = ProtectionClassifier.Snapshot(after).State;
        if (offOutsideEarshot && run.Status?.ExitCode == (int)GateExitCode.Success &&
            afterState is AudioProtectionState.Protected or AudioProtectionState.Partial)
        {
            // The gate only replaced the kept request; Handsfree stays off as it was turned off outside Earshot.
            return Finish(verb, new ControllerResult(OpStatus.NotAttempted, OffOutsideEarshotMessage, steps));
        }

        return Finish(verb, MapReadBack(protect, afterState, run, steps));
    }

    internal static ControllerResult MapReadBack(bool protect, AudioProtectionState state, GateRunResult run, IReadOnlyList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(steps);
        if (ProtectionPolicy.IsSatisfied(protect, state))
        {
            // The services match, but a gate that reported a failed step (a protection.json write, say) may
            // have left the restore record wrong, so this is not a clean success. The same holds when the gate's
            // outcome is unknown because no status file could be read. LastTaskResult alone is advisory (what it
            // holds for a task's exit code is undocumented), so it neither makes nor breaks a clean result.
            if (run.Status?.ExitCode != (int)GateExitCode.Success)
            {
                return new ControllerResult(OpStatus.Partial, protect ? ProtectedWithProblemMessage : OffWithProblemMessage, steps);
            }

            return ControllerResult.Ok(protect ? ProtectedMessage : OffMessage, steps);
        }

        // Partial is Handsfree off with Headset still on: part of the way to protected, no way towards off.
        if (protect && state == AudioProtectionState.Partial)
        {
            return new ControllerResult(OpStatus.Partial, PartialMessage, steps);
        }

        string message = run.Outcome switch
        {
            GateRunOutcome.TimedOut => TimedOutMessage,
            GateRunOutcome.Cancelled => CancelledMessage,
            _ when run.Status?.ExitCode is (int)GateExitCode.NotFound or (int)GateExitCode.NotPresent => BlockController.NotFoundMessage,
            _ when state == AudioProtectionState.Unknown => UnreadableMessage,
            _ => protect ? ProtectFailedMessage : OffFailedMessage,
        };
        return ControllerResult.Fail(message, steps);
    }

    private ControllerResult Finish(string action, ControllerResult result)
    {
        foreach (StepOutcome step in result.Steps.Where(s => !s.Ok))
        {
            _log.Warn("audio protection " + action + ": " + GateActions.Describe(step));
        }

        _log.Info("audio protection " + action + ": " + result.Status + ", \"" + result.UserMessage + "\".");
        return result;
    }
}
