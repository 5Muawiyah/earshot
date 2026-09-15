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
    internal const string OffOutsideEarshotMessage = "Handsfree was turned off outside Earshot, so it stays off.";
    internal const string PartialMessage = "Handsfree is off, but Headset is still on. Try again.";
    internal const string ProtectFailedMessage = "Could not protect audio quality. Try again.";
    internal const string OffFailedMessage = "Could not turn protection off. Try again.";
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

    // The real controller. Constructing it reads the current user's SID and nothing else; its worker thread
    // starts on the first change.
    public static AudioProtectionController Create(ILog log, ISettingsStore settings, Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        var worker = new SystemWorker(log);
        var store = new GateStore(paths.MachineFolder);
        var gate = new TaskSchedulerGate(new ComScheduledTasks(), store, paths.InstallFolder, sid,
            AccountSids.Translate, TimeProvider.System, TaskSchedulerGate.WaitOrCancelled);
        return new AudioProtectionController(log, settings, store, new ServiceStateReader(new BluetoothServiceReader()), gate, worker, worker);
    }

    // A fast read (the device list and one service list), so it runs on the thread pool rather than queueing
    // behind a protection change that may take minutes on the system worker.
    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) =>
        Task.Run(ReadStatus, ct);

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) =>
        _worker.RunAsync(token => Apply(protect, token), ct);

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
        if (ProtectionPolicy.IsSatisfied(protect, beforeState))
        {
            return Finish(verb, ControllerResult.Already(protect ? AlreadyProtectedMessage : AlreadyOffMessage));
        }

        // Turning off only turns back on what Earshot turned off. When Handsfree is off and protection.json
        // lists nothing, the gate would change nothing, so it is not started.
        if (!protect && beforeState is AudioProtectionState.Protected or AudioProtectionState.Partial)
        {
            GateRead<ProtectionRecord> record = _store.ReadProtection();
            bool nothingRecorded = record.Status == GateReadStatus.Missing ||
                                   (record.IsOk && record.Value is not null && !record.Value.DisabledServices.Any(ProtectedServices.IsTurnedOff));
            if (nothingRecorded)
            {
                steps.AddRange(before.Steps);
                steps.Add(StepOutcomes.NotAttempted(verb, "protection.json lists no service Earshot turned off."));
                return Finish(verb, new ControllerResult(OpStatus.NotAttempted, OffOutsideEarshotMessage, steps));
            }
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

        if (run.Status?.ExitCode == (int)ProtectionGateRunner.BlockedExit)
        {
            return Finish(verb, new ControllerResult(OpStatus.NotAttempted, SavedForAllowMessage, steps));
        }

        // Ground truth: the services read now, not the task's result.
        ServiceReadResult after = _reader.Read(address);
        steps.AddRange(after.Steps);
        return Finish(verb, MapReadBack(protect, ProtectionClassifier.Snapshot(after).State, run, steps));
    }

    internal static ControllerResult MapReadBack(bool protect, AudioProtectionState state, GateRunResult run, IReadOnlyList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(steps);
        if (ProtectionPolicy.IsSatisfied(protect, state))
        {
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
