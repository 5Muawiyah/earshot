using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.Boot;

internal sealed record ElevatedRun(int? ExitCode, StepOutcome Step);

// Starts Earshot elevated (one UAC prompt) and waits for it to exit.
internal interface IElevatedLauncher
{
    Task<ElevatedRun> RunAsync(string executable, string arguments, CancellationToken ct);
}

// ShellExecute with the runas verb. A declined prompt is ERROR_CANCELLED (1223).
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.verb
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw
internal sealed class ShellRunasLauncher : IElevatedLauncher
{
    public async Task<ElevatedRun> RunAsync(string executable, string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            return new ElevatedRun(null, StepOutcomes.FromWin32("runas", unchecked((uint)ex.NativeErrorCode), ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new ElevatedRun(null, StepOutcomes.NotAttempted("runas", ex.Message));
        }

        if (process is null)
        {
            return new ElevatedRun(null, StepOutcomes.NotAttempted("runas", "No process was started."));
        }

        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ElevatedRun(process.ExitCode, StepOutcomes.FromWin32("runas", 0, executable + " " + arguments));
        }
    }
}

// The boot block as the tray sees it. Status is read without elevation; every change goes through the
// \Earshot\Gate task (RunEx on the SystemWorker) and is then confirmed by reading the real node state,
// never by trusting the task's result. Setup and removal start Earshot elevated with one UAC prompt.
//
// The identity the gate acts on is device.json. Status falls back to the tray's pinned settings only to
// show the nodes before setup.
internal sealed class BlockController : IBlockController, IDisposable
{
    internal const string NotSetUpMessage = "Boot block is not set up yet. Choose Set up Earshot.";
    internal const string NeedsRepairMessage = "Boot block needs repair. Choose Set up Earshot.";
    internal const string CouldNotStartMessage = "The boot block task did not start. Choose Set up Earshot.";
    internal const string NotFoundMessage = "AirPods not found. Connect them to this PC once from Windows Bluetooth settings.";
    internal const string NotPresentBlockMessage = "Connect the AirPods to this PC once from Windows Bluetooth settings, then try Block again.";
    internal const string NotPresentAllowMessage = "Connect the AirPods to this PC once from Windows Bluetooth settings, then try again.";
    internal const string UnreadableMessage = "Could not read the AirPods state. Try again.";
    internal const string BlockedMessage = "Blocked at boot";
    internal const string AlreadyBlockedMessage = "Already blocked at boot";
    internal const string NotPersistentMessage = "Blocked for now, but it may not last a restart. Try again.";
    internal const string AllowedMessage = "Allowed";
    internal const string AlreadyAllowedMessage = "Already allowed";
    internal const string PartialBlockMessage = "Only part of the AirPods could be blocked. Try again.";
    internal const string PartialAllowMessage = "Only part of the AirPods could be allowed. Try again.";
    internal const string BlockFailedMessage = "Could not block the AirPods. Try again.";
    internal const string AllowFailedMessage = "Could not allow the AirPods. Try again.";
    internal const string TimedOutMessage = "The boot block did not finish in time. Try again.";
    internal const string CancelledMessage = "Stopped waiting for the boot block.";
    internal const string BusyMessage = "Another change to the AirPods is still running. Try again.";
    internal const string BlockAtBootOnMessage = "Block at boot is on";
    internal const string BlockAtBootOffMessage = "Block at boot is off";
    internal const string BlockAtBootFailedMessage = "Could not change Block at boot. Try again.";
    internal const string InvalidAddressMessage = "That device address is not valid.";
    internal const string DeviceChosenMessage = "Device chosen";
    internal const string OtherDeviceBlockedMessage = "Allow the current AirPods first, then choose another device.";
    internal const string DeviceFailedMessage = "Could not choose that device. Try again.";
    internal const string NothingPinnedMessage = "Choose your AirPods first, then set up Earshot.";
    internal const string NoUserMessage = "Setup needs a signed-in Windows user.";
    internal const string SetupDoneMessage = "Earshot is set up";
    internal const string SetupCancelledMessage = "Setup was cancelled";
    internal const string SetupNeedsAdminMessage = "Setup needs administrator approval.";
    internal const string SetupUnsafeFolderMessage = "Setup stopped because a folder it uses was not safe.";
    internal const string SetupFailedMessage = "Setup did not finish. Try again.";
    internal const string RemovedMessage = "Earshot is removed";
    internal const string RemovedPartlyMessage = "Earshot is mostly removed. Some parts could not be undone.";
    internal const string RemoveCancelledMessage = "Removal was cancelled";
    internal const string RemoveFailedMessage = "Removal did not finish. Try again.";

    private const uint ErrorCancelled = 1223;

    private readonly ILog _log;
    private readonly ISettingsStore _settings;
    private readonly GateStore _store;
    private readonly NodeStateReader _reader;
    private readonly TaskSchedulerGate _gate;
    private readonly IElevatedLauncher _launcher;
    private readonly string? _userSid;
    private readonly string? _executable;
    private readonly ISystemWorker _worker;
    private readonly IDisposable? _ownedWorker;
    private volatile bool _isSetUp;
    private string _lastStatusProblems = "";

    public BlockController(
        ILog log,
        ISettingsStore settings,
        GateStore store,
        INodeReader nodes,
        TaskSchedulerGate gate,
        IElevatedLauncher launcher,
        string? userSid,
        string? executable,
        ISystemWorker worker,
        IDisposable? ownedWorker = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(worker);
        _log = log;
        _settings = settings;
        _store = store;
        _reader = new NodeStateReader(nodes);
        _gate = gate;
        _launcher = launcher;
        _userSid = userSid;
        _executable = executable;
        _worker = worker;
        _ownedWorker = ownedWorker;
    }

    // The real controller. Constructing it reads the current user's SID and nothing else; the worker thread
    // starts on first use.
    public static BlockController Create(ILog log, ISettingsStore settings, Paths paths)
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
        return new BlockController(log, settings, store, new CfgMgr32NodeReader(), gate, new ShellRunasLauncher(),
            sid, Environment.ProcessPath, worker, worker);
    }

    // \Earshot\Gate, \Earshot\Protect and \Earshot\BootBlock present and verified, as of the last status
    // read or change. False until one has run; reading it does no I/O.
    public bool IsSetUp => _isSetUp;

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default) =>
        _worker.RunAsync(_ => ReadStatus(), ct);

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default) =>
        _worker.RunAsync(token => ChangeNodes(block: true, token), ct);

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default) =>
        _worker.RunAsync(token => ChangeNodes(block: false, token), ct);

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default) =>
        _worker.RunAsync(token => SetBlockAtBoot(blockAtBoot, token), ct);

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default) =>
        _worker.RunAsync(token => SetDevice(address12, token), ct);

    public async Task<ControllerResult> RunSetupAsync(CancellationToken ct = default)
    {
        EarshotSettings settings = _settings.Current;
        var steps = new List<StepOutcome>();
        if (!BoundaryValidation.IsAddress12(settings.PinnedAddress) || !NodeMatch.IsValidTargetContainer(settings.PinnedContainerId))
        {
            steps.Add(StepOutcomes.NotAttempted("install", "No device is pinned in the settings."));
            return Finish("install", ControllerResult.Fail(NothingPinnedMessage, steps));
        }

        if (!Sddl.IsUserSid(_userSid))
        {
            steps.Add(StepOutcomes.NotAttempted("install", "The current user has no usable SID."));
            return Finish("install", ControllerResult.Fail(NoUserMessage, steps));
        }

        if (string.IsNullOrEmpty(_executable))
        {
            steps.Add(StepOutcomes.NotAttempted("install", "The path of Earshot.exe is unknown."));
            return Finish("install", ControllerResult.Fail(SetupFailedMessage, steps));
        }

        string arguments = "install " + _userSid + " " + settings.PinnedAddress + " " + settings.PinnedContainerId.ToString("D");
        ElevatedRun run = await _launcher.RunAsync(_executable, arguments, ct).ConfigureAwait(false);
        steps.Add(run.Step);
        if (run.ExitCode is not int exitCode)
        {
            bool cancelled = run.Step.Code == unchecked((int)ErrorCancelled);
            return Finish("install", ControllerResult.Fail(cancelled ? SetupCancelledMessage : SetupFailedMessage, steps));
        }

        steps.Add(ExitStep("install-exit", exitCode));
        if (exitCode != (int)GateExitCode.Success)
        {
            string message = exitCode switch
            {
                (int)GateExitCode.NotElevated or (int)GateExitCode.RunningAsSystem => SetupNeedsAdminMessage,
                (int)GateExitCode.FolderNotSecure => SetupUnsafeFolderMessage,
                _ => SetupFailedMessage,
            };
            return Finish("install", ControllerResult.Fail(message, steps));
        }

        BootBlockStatus status = await GetStatusAsync(ct).ConfigureAwait(false);
        return Finish("install", status.TasksInstalled
            ? ControllerResult.Ok(SetupDoneMessage, steps)
            : ControllerResult.Fail(NeedsRepairMessage, steps));
    }

    public async Task<ControllerResult> UninstallAsync(CancellationToken ct = default)
    {
        var steps = new List<StepOutcome>();
        if (string.IsNullOrEmpty(_executable))
        {
            steps.Add(StepOutcomes.NotAttempted("uninstall", "The path of Earshot.exe is unknown."));
            return Finish("uninstall", ControllerResult.Fail(RemoveFailedMessage, steps));
        }

        ElevatedRun run = await _launcher.RunAsync(_executable, "uninstall", ct).ConfigureAwait(false);
        steps.Add(run.Step);
        if (run.ExitCode is not int exitCode)
        {
            bool cancelled = run.Step.Code == unchecked((int)ErrorCancelled);
            return Finish("uninstall", ControllerResult.Fail(cancelled ? RemoveCancelledMessage : RemoveFailedMessage, steps));
        }

        steps.Add(ExitStep("uninstall-exit", exitCode));
        ControllerResult result = exitCode switch
        {
            (int)GateExitCode.Success => ControllerResult.Ok(RemovedMessage, steps),
            (int)GateExitCode.Partial => new ControllerResult(OpStatus.Partial, RemovedPartlyMessage, steps),
            _ => ControllerResult.Fail(RemoveFailedMessage, steps),
        };
        if (exitCode is (int)GateExitCode.Success or (int)GateExitCode.Partial)
        {
            _isSetUp = false;
        }

        return Finish("uninstall", result);
    }

    public void Dispose() => _ownedWorker?.Dispose();

    internal BootBlockStatus ReadStatus()
    {
        var steps = new List<StepOutcome>();
        bool installed = VerifyTasks(steps);

        GateRead<GateConfig> config = _store.ReadConfig();
        if (config.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
        {
            steps.Add(config.Step);
        }

        // Before setup there is no config.json; the menu then shows the shipped default.
        bool blockAtBoot = config.IsOk && config.Value is not null ? config.Value.BlockAtBoot : new GateConfig().BlockAtBoot;

        DeviceIdentity? identity = ResolveIdentity(steps);
        NodeReadResult read = identity is null ? NodeReadResult.NoIdentity : _reader.Read(identity.ContainerId, identity.Address);
        steps.AddRange(read.Steps);
        BlockState state = BlockStateClassifier.Classify(installed, identity is not null, read);

        string problems = string.Join(" | ", steps.Where(s => !s.Ok).Select(GateActions.Describe));
        if (!string.Equals(problems, _lastStatusProblems, StringComparison.Ordinal))
        {
            _lastStatusProblems = problems;
            if (problems.Length > 0)
            {
                _log.Warn("boot block status " + state + ": " + problems);
            }
        }

        return new BootBlockStatus(state, identity?.ContainerId ?? Guid.Empty, read.Nodes, installed, blockAtBoot);
    }

    private bool VerifyTasks(List<StepOutcome> steps)
    {
        bool all = true;
        foreach (string name in TaskPlan.TaskNames)
        {
            TaskVerification check = _gate.Verify(name);
            steps.AddRange(check.Steps);
            if (check.Health != TaskHealth.Missing)
            {
                foreach (string problem in check.Problems)
                {
                    steps.Add(StepOutcomes.NotAttempted("task-check:" + name, problem));
                }
            }

            all &= check.Health == TaskHealth.Ready;
        }

        _isSetUp = all;
        return all;
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

    private ControllerResult ChangeNodes(bool block, CancellationToken ct)
    {
        string verb = block ? GateVerbs.Block : GateVerbs.Allow;
        var steps = new List<StepOutcome>();

        GateRead<DeviceIdentity> device = _store.ReadDevice();
        if (!device.IsOk || device.Value is null)
        {
            steps.Add(device.Step);
            bool installed = VerifyTasks(steps);
            return Finish(verb, ControllerResult.Fail(installed ? NeedsRepairMessage : NotSetUpMessage, steps));
        }

        DeviceIdentity identity = device.Value;
        NodeReadResult before = _reader.Read(identity.ContainerId, identity.Address);
        if (block ? BlockStateClassifier.IsFullyBlocked(before) : BlockStateClassifier.IsFullyAllowed(before))
        {
            return Finish(verb, ControllerResult.Already(block ? AlreadyBlockedMessage : AlreadyAllowedMessage));
        }

        GateRunResult run = _gate.Run(TaskPlan.GateTaskName, verb, NewNonce(), null, TaskSchedulerGate.GateTimeout, ct);
        steps.AddRange(run.Steps);
        if (run.Status is not null)
        {
            steps.AddRange(run.Status.Steps);
        }

        ControllerResult? refused = RunRefusal(run);
        if (refused is not null)
        {
            return Finish(verb, refused with { Steps = steps });
        }

        if (IsBusy(run))
        {
            return Finish(verb, ControllerResult.Fail(BusyMessage, steps));
        }

        // Ground truth: the node state read now, not the task's result.
        NodeReadResult after = _reader.Read(identity.ContainerId, identity.Address);
        steps.AddRange(after.Steps);
        BlockState state = BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, after);
        return Finish(verb, MapNodeResult(block, state, after, run.Outcome, steps));
    }

    internal static ControllerResult MapNodeResult(bool block, BlockState state, NodeReadResult after, GateRunOutcome run, IReadOnlyList<StepOutcome> steps)
    {
        bool anyNotPresent = after.Nodes.Any(n => !n.IsPresent);
        string failed = run switch
        {
            GateRunOutcome.TimedOut => TimedOutMessage,
            GateRunOutcome.Cancelled => CancelledMessage,
            _ => block ? BlockFailedMessage : AllowFailedMessage,
        };

        switch (state)
        {
            case BlockState.NotFound:
                return ControllerResult.Fail(NotFoundMessage, steps);

            case BlockState.Unknown:
                return ControllerResult.Fail(
                    after.Listed && after.Nodes.All(n => !n.IsPresent)
                        ? (block ? NotPresentBlockMessage : NotPresentAllowMessage)
                        : UnreadableMessage,
                    steps);

            case BlockState.Mixed:
                return new ControllerResult(OpStatus.Partial, block ? PartialBlockMessage : PartialAllowMessage, steps);

            case BlockState.Blocked when block:
                if (!BlockStateClassifier.IsFullyBlocked(after))
                {
                    return new ControllerResult(OpStatus.Partial, NotPersistentMessage, steps);
                }

                return anyNotPresent
                    ? new ControllerResult(OpStatus.Partial, NotPresentBlockMessage, steps)
                    : ControllerResult.Ok(BlockedMessage, steps);

            case BlockState.Allowed when !block:
                return anyNotPresent
                    ? new ControllerResult(OpStatus.Partial, NotPresentAllowMessage, steps)
                    : ControllerResult.Ok(AllowedMessage, steps);

            default:
                return ControllerResult.Fail(failed, steps);
        }
    }

    private ControllerResult SetBlockAtBoot(bool blockAtBoot, CancellationToken ct)
    {
        string verb = blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff;
        string done = blockAtBoot ? BlockAtBootOnMessage : BlockAtBootOffMessage;
        var steps = new List<StepOutcome>();

        GateRead<GateConfig> before = _store.ReadConfig();
        if (before.IsOk && before.Value is not null && before.Value.BlockAtBoot == blockAtBoot)
        {
            return Finish(verb, ControllerResult.Already(done));
        }

        GateRunResult run = _gate.Run(TaskPlan.GateTaskName, verb, NewNonce(), null, TaskSchedulerGate.GateTimeout, ct);
        steps.AddRange(run.Steps);
        if (run.Status is not null)
        {
            steps.AddRange(run.Status.Steps);
        }

        ControllerResult? refused = RunRefusal(run);
        if (refused is not null)
        {
            return Finish(verb, refused with { Steps = steps });
        }

        if (IsBusy(run))
        {
            return Finish(verb, ControllerResult.Fail(BusyMessage, steps));
        }

        GateRead<GateConfig> after = _store.ReadConfig();
        steps.Add(after.Step);
        bool applied = after.IsOk && after.Value is not null && after.Value.BlockAtBoot == blockAtBoot;
        return Finish(verb, applied
            ? ControllerResult.Ok(done, steps)
            : ControllerResult.Fail(run.Outcome == GateRunOutcome.TimedOut ? TimedOutMessage : BlockAtBootFailedMessage, steps));
    }

    private ControllerResult SetDevice(string address12, CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        if (!BoundaryValidation.IsAddress12(address12))
        {
            steps.Add(StepOutcomes.NotAttempted(GateVerbs.SetDevice, "The address is not 12 upper-case hex characters."));
            return Finish(GateVerbs.SetDevice, ControllerResult.Fail(InvalidAddressMessage, steps));
        }

        GateRunResult run = _gate.Run(TaskPlan.GateTaskName, GateVerbs.SetDevice, NewNonce(), address12, TaskSchedulerGate.GateTimeout, ct);
        steps.AddRange(run.Steps);
        if (run.Status is not null)
        {
            steps.AddRange(run.Status.Steps);
        }

        ControllerResult? refused = RunRefusal(run);
        if (refused is not null)
        {
            return Finish(GateVerbs.SetDevice, refused with { Steps = steps });
        }

        if (IsBusy(run))
        {
            return Finish(GateVerbs.SetDevice, ControllerResult.Fail(BusyMessage, steps));
        }

        GateRead<DeviceIdentity> after = _store.ReadDevice();
        steps.Add(after.Step);
        if (after.IsOk && after.Value is not null && string.Equals(after.Value.Address, address12, StringComparison.Ordinal))
        {
            return Finish(GateVerbs.SetDevice, ControllerResult.Ok(DeviceChosenMessage, steps));
        }

        string message = run.Status?.ExitCode switch
        {
            (int)GateExitCode.OtherDeviceBlocked => OtherDeviceBlockedMessage,
            (int)GateExitCode.NotFound => NotFoundMessage,
            _ => run.Outcome == GateRunOutcome.TimedOut ? TimedOutMessage : DeviceFailedMessage,
        };
        return Finish(GateVerbs.SetDevice, ControllerResult.Fail(message, steps));
    }

    // The gate changed nothing because another elevated run held the gate run lock or the device change lock.
    internal static bool IsBusy(GateRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Status is { } status && status.Steps.Any(s => DeviceChangeLock.IsBusy(s) || MachineGateMutex.IsBusy(s));
    }

    private ControllerResult? RunRefusal(GateRunResult run)
    {
        switch (run.Outcome)
        {
            case GateRunOutcome.NotSetUp:
                _isSetUp = false;
                return ControllerResult.Fail(NotSetUpMessage, []);
            case GateRunOutcome.NeedsRepair:
                _isSetUp = false;
                return ControllerResult.Fail(NeedsRepairMessage, []);
            case GateRunOutcome.RunFailed:
            case GateRunOutcome.TaskDisabled:
                return ControllerResult.Fail(CouldNotStartMessage, []);
            default:
                return null;
        }
    }

    private ControllerResult Finish(string action, ControllerResult result)
    {
        foreach (StepOutcome step in result.Steps.Where(s => !s.Ok))
        {
            _log.Warn("boot block " + action + ": " + GateActions.Describe(step));
        }

        _log.Info("boot block " + action + ": " + result.Status + ", \"" + result.UserMessage + "\".");
        return result;
    }

    private static StepOutcome ExitStep(string step, int exitCode) =>
        new(step, exitCode == 0, exitCode,
            GateExitCodes.NameOf(exitCode) ?? "exit " + exitCode.ToString(CultureInfo.InvariantCulture), null);

    private static string NewNonce() => Guid.NewGuid().ToString("N");
}
