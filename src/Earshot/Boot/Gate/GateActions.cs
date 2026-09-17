using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Interop;

namespace Earshot.Boot.Gate;

// Node reads plus the two calls only the elevated side makes. The tray never holds one of these.
internal interface INodeApi : INodeReader
{
    // CM_Disable_DevNode. https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
    uint Disable(uint devInst, uint flags);

    // CM_Enable_DevNode with ulFlags 0. https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
    uint Enable(uint devInst);
}

internal sealed class CfgMgr32NodeApi : CfgMgr32NodeReader, INodeApi
{
    public uint Disable(uint devInst, uint flags) => CfgMgr32.CM_Disable_DevNode(devInst, flags);

    public uint Enable(uint devInst) => CfgMgr32.CM_Enable_DevNode(devInst, 0);
}

// Exit codes of gate, install and uninstall. They stay clear of the sysexits values in ExitCodes, and the
// name of each is written to the status file. LastTaskResult may carry them back to the tray, but that
// is undocumented, so the tray only reports them.
internal enum GateExitCode
{
    Success = 0,
    Partial = 2,             // some target nodes (or install or uninstall steps) failed
    Failed = 3,
    NotPresent = 4,          // every target node is non-present, so none could be changed
    NotFound = 5,            // no node matched the pinned device
    NoIdentity = 6,          // device.json is missing or not valid
    NotAvailable = 7,        // protect-on or protect-off in a build without protection
    OtherDeviceBlocked = 8,  // set-device while the device pinned now is still disabled
    StatusNotWritten = 9,    // the action succeeded but its status file could not be written
    FolderNotSecure = 10,    // %ProgramData%\Earshot is missing or its ACL fails the check
    DeviceBlocked = 11,      // protect-on or protect-off while the device is blocked; the request is kept for the next allow
    NotAudioSink = 12,       // set-device for a device with no A2DP sink node, such as a phone
    OtherDeviceProtected = 13, // set-device while protection.json lists services turned off on the device pinned now
    NoManifest = 14,         // install without a valid Earshot.files.json next to the running exe
    Rejected = 20,           // the command line did not validate; nothing was done
    NotElevated = 21,        // not SYSTEM or an elevated administrator
    RunningAsSystem = 22,    // install or uninstall started as SYSTEM
}

internal static class GateExitCodes
{
    private static readonly Dictionary<GateExitCode, string> Names = new()
    {
        [GateExitCode.Success] = "success",
        [GateExitCode.Partial] = "partial",
        [GateExitCode.Failed] = "failed",
        [GateExitCode.NotPresent] = "not-present",
        [GateExitCode.NotFound] = "not-found",
        [GateExitCode.NoIdentity] = "no-identity",
        [GateExitCode.NotAvailable] = "not-available",
        [GateExitCode.OtherDeviceBlocked] = "other-device-blocked",
        [GateExitCode.StatusNotWritten] = "status-not-written",
        [GateExitCode.FolderNotSecure] = "folder-not-secure",
        [GateExitCode.DeviceBlocked] = "device-blocked",
        [GateExitCode.NotAudioSink] = "not-audio-sink",
        [GateExitCode.OtherDeviceProtected] = "other-device-protected",
        [GateExitCode.NoManifest] = "no-manifest",
        [GateExitCode.Rejected] = "rejected",
        [GateExitCode.NotElevated] = "not-elevated",
        [GateExitCode.RunningAsSystem] = "running-as-system",
    };

    public static string ResultName(GateExitCode code) =>
        Names.TryGetValue(code, out string? name) ? name : "failed";

    public static bool IsResultName(string name) => Names.ContainsValue(name);

    // The name for a raw exit code, or null when it is not one of these.
    public static string? NameOf(int exitCode) =>
        Enum.IsDefined((GateExitCode)exitCode) ? Names[(GateExitCode)exitCode] : null;
}

// Which command line started the gate. \Earshot\Gate and \Earshot\BootBlock run "gate"; \Earshot\Protect runs
// "gate-protect", so a Bluetooth service change only ever runs under the Protect task's longer time limit and a
// node change never does. Each mode accepts only its own verbs.
internal enum GateMode
{
    Gate,     // gate <verb> <nonce> [address]: every verb except protect-on and protect-off
    Protect,  // gate-protect <verb> <nonce>: protect-on and protect-off only
}

internal static class GateModes
{
    public const string GateToken = "gate";
    public const string ProtectToken = "gate-protect";

    public static bool IsProtectVerb(string verb) => verb is GateVerbs.ProtectOn or GateVerbs.ProtectOff;

    // True when the verb may run in the mode.
    public static bool Allows(GateMode mode, string verb) =>
        mode == GateMode.Protect ? IsProtectVerb(verb) : !IsProtectVerb(verb);

    public static string TokenOf(GateMode mode) => mode == GateMode.Protect ? ProtectToken : GateToken;
}

internal sealed record GateRequest(string Verb, string Nonce, string? Address, GateMode Mode = GateMode.Gate);

// What a gate verb implemented elsewhere receives (the protect verbs and the protection restore hook).
// Identity is the validated device.json. Every native call is appended to Steps as a StepOutcome; the
// implementation sets Handled, Outcome and, when it read one, State.
//
// Bluetooth is the service API the protect verbs call, or null in a build or test without one (the verbs then
// report not available). The gate checks the device nodes through Nodes before any service call, so the two
// must describe the same machine: the real node API goes only with the real Bluetooth API (or none), and a
// test's fake node table never with the real one, so a test can never reach a real BluetoothSetServiceState.
internal sealed class GateRunContext
{
    public GateRunContext(
        string verb, string nonce, DeviceIdentity identity, INodeApi nodes, GateStore store, ILog log, IList<StepOutcome> steps,
        IBluetoothServiceApi? bluetooth = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(steps);
        GateActions.RequireSameMachine(nodes, bluetooth);
        Verb = verb;
        Nonce = nonce;
        Identity = identity;
        Nodes = nodes;
        Store = store;
        Log = log;
        Steps = steps;
        Bluetooth = bluetooth;
    }

    public IBluetoothServiceApi? Bluetooth { get; }

    public string Verb { get; }

    public string Nonce { get; }

    public DeviceIdentity Identity { get; }

    public INodeApi Nodes { get; }

    public GateStore Store { get; }

    public ILog Log { get; }

    public IList<StepOutcome> Steps { get; }

    public bool Handled { get; set; }

    public GateExitCode Outcome { get; set; } = GateExitCode.Failed;

    public string? State { get; set; }
}

internal sealed record NodeChangeSummary(int Total, int Changed, int Already, int NotPresent, int Failed)
{
    public GateExitCode Outcome =>
        Total == 0 ? GateExitCode.NotFound
        : Changed + Already == Total ? GateExitCode.Success
        : Changed + Already > 0 ? GateExitCode.Partial
        : NotPresent == Total ? GateExitCode.NotPresent
        : GateExitCode.Failed;
}

// The elevated action behind \Earshot\Gate and \Earshot\BootBlock (gate <verb> <nonce> [address]) and
// \Earshot\Protect (gate-protect <verb> <nonce>).
//
// Runs as SYSTEM. The request has already been validated (GateArguments). The identity for every verb
// except set-device comes only from device.json, never from the command line. Before anything is read,
// %ProgramData%\Earshot must pass the machine folder ACL check, so a folder a user could write is never
// trusted. Each run writes status-<nonce>.json with every step and returns an exit code for the result.
//
// Serialisation. A run first enters the machine-wide gate run lock (IGateRunLock), so no two elevated Earshot
// runs overlap whichever task started them; a run that cannot enter it within its wait changes nothing and
// reports the busy step. Every node change (block, allow, the boot block, set-device) also takes the device
// change lock before it reads the nodes, as the protect verbs do, so the rule holds for any process that takes
// that lock too.
internal sealed partial class GateActions
{
    // CM_DISABLE_PERSIST is what makes the disable survive a reboot; without it the acceptance test fails
    // silently. CM_DISABLE_UI_NOT_OK keeps any failure UI away from a SYSTEM session.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
    public const uint BlockDisableFlags = CfgMgr32.CM_DISABLE_PERSIST | CfgMgr32.CM_DISABLE_UI_NOT_OK;

    private readonly INodeApi _nodes;
    private readonly GateStore _store;
    private readonly IFolderSecurity _folders;
    // How long a run waits to enter the gate run lock. A gate run waits no longer than a node change waits for
    // the device change lock, inside \Earshot\Gate's PT2M limit. A protect run may wait for a whole gate run
    // (PT2M) and still leave most of \Earshot\Protect's PT5M for the service calls.
    public static readonly TimeSpan GateRunWait = DeviceChangeLock.NodeChangeLockTimeout;
    public static readonly TimeSpan ProtectRunWait = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly IGateRunLock _runLock;
    private readonly IBluetoothServiceApi? _bluetooth;

    // runLock null: no machine-wide lock, for a gate over a test's fake node table. bluetooth null: the protect
    // verbs are not available. ForMachine passes the mutex and the real Bluetooth API.
    public GateActions(
        INodeApi nodes, GateStore store, IFolderSecurity folders, ILog log, TimeProvider time,
        IGateRunLock? runLock = null, IBluetoothServiceApi? bluetooth = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        RequireSameMachine(nodes, bluetooth);
        _nodes = nodes;
        _store = store;
        _folders = folders;
        _log = log;
        _time = time;
        _runLock = runLock ?? NoGateRunLock.Instance;
        _bluetooth = bluetooth;
    }

    public static GateActions ForMachine(string machineFolder, ILog log) =>
        new(new CfgMgr32NodeApi(), new GateStore(machineFolder), new NtfsFolderSecurity(), log, TimeProvider.System, new MachineGateMutex(), new BluetoothServiceApi());

    internal IGateRunLock RunLock => _runLock;

    internal IBluetoothServiceApi? Bluetooth => _bluetooth;

    // The real Bluetooth API only with the real node API; see GateRunContext.
    internal static void RequireSameMachine(INodeApi nodes, IBluetoothServiceApi? bluetooth)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (bluetooth is BluetoothServiceApi && nodes is not CfgMgr32NodeApi)
        {
            throw new ArgumentException("The real Bluetooth API goes only with the real node API.", nameof(bluetooth));
        }

        if (nodes is CfgMgr32NodeApi && bluetooth is not (null or BluetoothServiceApi))
        {
            throw new ArgumentException("The real node API goes only with the real Bluetooth API.", nameof(bluetooth));
        }
    }

    // For tests: the wait between device change lock attempts. Returning false stops waiting at once.
    internal Func<TimeSpan, bool> LockWait { get; init; } = DeviceChangeLock.SleepAndContinue;

    private sealed record VerbResult(GateExitCode Outcome, string? State);

    public GateExitCode Run(GateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!GateModes.Allows(request.Mode, request.Verb))
        {
            // The command line parser refuses this already; the check is repeated so no caller of Run can send a
            // service change through the Gate task or a node change through the Protect task.
            _log.Warn(GateModes.TokenOf(request.Mode) + " " + request.Verb + ": rejected, this verb does not run in this mode.");
            return GateExitCode.Rejected;
        }

        DateTimeOffset started = _time.GetUtcNow();
        var steps = new List<StepOutcome>();
        using IDisposable? held = _runLock.TryEnter(request.Mode == GateMode.Protect ? ProtectRunWait : GateRunWait, steps);

        if (!FolderIsSecure(steps))
        {
            foreach (StepOutcome step in steps.Where(s => !s.Ok))
            {
                _log.Error("gate " + request.Verb + ": " + Describe(step));
            }

            return GateExitCode.FolderNotSecure;
        }

        // Without the run lock nothing is changed or pruned; the status file, named by this run's own nonce,
        // still says why. An unexpected failure inside a verb is recorded the same way rather than ending the
        // process without a status file.
        VerbResult result;
        try
        {
            result = held is null ? new VerbResult(GateExitCode.Failed, null) : request.Verb switch
            {
                GateVerbs.Block => Block(steps),
                GateVerbs.Allow => Allow(steps),
                GateVerbs.Status => Status(steps),
                GateVerbs.SetBootOn => SetBoot(true, steps),
                GateVerbs.SetBootOff => SetBoot(false, steps),
                GateVerbs.SetDevice => SetDevice(request.Address ?? "", steps),
                GateVerbs.Boot => Boot(steps),
                GateVerbs.ProtectOn => Protect(request, protect: true, steps),
                GateVerbs.ProtectOff => Protect(request, protect: false, steps),
                _ => new VerbResult(GateExitCode.Rejected, null),
            };
        }
        catch (Exception ex)
        {
            steps.Add(ElevatedFailure.Step(request.Verb, ex));
            _log.Error("gate " + request.Verb + " stopped with " + ex.GetType().Name + " after " + steps.Count + " steps.", ex);
            result = new VerbResult(GateExitCode.Failed, null);
        }

        if (held is not null)
        {
            steps.AddRange(_store.PruneStatusFiles(_time.GetUtcNow()));
        }

        GateExitCode outcome = result.Outcome;
        var status = new GateStatusFile(
            GateStore.SchemaVersion, request.Nonce, request.Verb, started, _time.GetUtcNow(),
            GateExitCodes.ResultName(outcome), (int)outcome, result.State, steps, StepsTruncated: false);
        StepOutcome written = _store.WriteStatus(status);
        if (!written.Ok)
        {
            _log.Error("gate " + request.Verb + ": " + Describe(written));
            if (outcome == GateExitCode.Success)
            {
                outcome = GateExitCode.StatusNotWritten;
            }
        }

        foreach (StepOutcome step in steps.Where(s => !s.Ok))
        {
            _log.Warn("gate " + request.Verb + " " + request.Nonce + ": " + Describe(step));
        }

        _log.Info("gate " + request.Verb + " " + request.Nonce + ": " + GateExitCodes.ResultName(outcome) +
                  " (" + (int)outcome + ")" + (result.State is null ? "" : ", state " + result.State) + ".");
        return outcome;
    }

    private bool FolderIsSecure(List<StepOutcome> steps)
    {
        StepOutcome read = _folders.ReadSddl(_store.Folder, out string? sddl);
        steps.Add(read);
        if (!read.Ok)
        {
            return false;
        }

        IReadOnlyList<string> problems = AclCheck.CheckMachineFolder(sddl);
        foreach (string problem in problems)
        {
            steps.Add(StepOutcomes.NotAttempted("machine-folder-acl", problem));
        }

        return problems.Count == 0;
    }

    private DeviceIdentity? ReadIdentity(List<StepOutcome> steps)
    {
        GateRead<DeviceIdentity> read = _store.ReadDevice();
        steps.Add(read.Step);
        return read.IsOk ? read.Value : null;
    }

    private VerbResult Block(List<StepOutcome> steps)
    {
        using DeviceChangeLock? changing = AcquireForNodeChange(steps);
        if (changing is null)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        DeviceIdentity? identity = ReadIdentity(steps);
        if (identity is null)
        {
            return new VerbResult(GateExitCode.NoIdentity, null);
        }

        NodeScanResult scan = NodeScan.FindTargets(_nodes, identity.ContainerId, identity.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            return new VerbResult(GateExitCode.Failed, nameof(BlockState.Unknown));
        }

        if (scan.Targets.Count == 0)
        {
            return NoTargets(scan);
        }

        NodeChangeSummary summary = ApplyBlock(_nodes, scan.Targets, steps);
        return new VerbResult(WithUnreadable(summary.Outcome, scan), ReadState(identity));
    }

    // No node to change: NotFound, unless a node that may belong to the device could not be read.
    private static VerbResult NoTargets(NodeScanResult scan) =>
        scan.Unreadable.Count > 0
            ? new VerbResult(GateExitCode.Failed, nameof(BlockState.Unknown))
            : new VerbResult(GateExitCode.NotFound, nameof(BlockState.NotFound));

    // A node that may belong to the device but could not be read was not changed, so every other node changing
    // is only part of the job.
    private static GateExitCode WithUnreadable(GateExitCode outcome, NodeScanResult scan) =>
        outcome == GateExitCode.Success && scan.Unreadable.Count > 0 ? GateExitCode.Partial : outcome;

    private VerbResult Allow(List<StepOutcome> steps)
    {
        using DeviceChangeLock? changing = AcquireForNodeChange(steps);
        if (changing is null)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        DeviceIdentity? identity = ReadIdentity(steps);
        if (identity is null)
        {
            return new VerbResult(GateExitCode.NoIdentity, null);
        }

        NodeScanResult scan = NodeScan.FindTargets(_nodes, identity.ContainerId, identity.Address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed)
        {
            return new VerbResult(GateExitCode.Failed, nameof(BlockState.Unknown));
        }

        if (scan.Targets.Count == 0)
        {
            return NoTargets(scan);
        }

        NodeChangeSummary summary = ApplyAllow(_nodes, scan.Targets, steps);
        return new VerbResult(WithUnreadable(summary.Outcome, scan), ReadState(identity));
    }

    private VerbResult Status(List<StepOutcome> steps)
    {
        DeviceIdentity? identity = ReadIdentity(steps);
        if (identity is null)
        {
            return new VerbResult(GateExitCode.NoIdentity, null);
        }

        NodeReadResult read = new NodeStateReader(_nodes).Read(identity.ContainerId, identity.Address);
        steps.AddRange(read.Steps);
        BlockState state = BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, read);
        return new VerbResult(read.Listed ? GateExitCode.Success : GateExitCode.Failed, state.ToString());
    }

    private VerbResult SetBoot(bool blockAtBoot, List<StepOutcome> steps)
    {
        StepOutcome written = _store.WriteConfig(new GateConfig { BlockAtBoot = blockAtBoot });
        steps.Add(written);
        return new VerbResult(written.Ok ? GateExitCode.Success : GateExitCode.Failed, null);
    }

    // Re-pins device.json to the device with this address. The address is validated, but it is still
    // attacker-controlled: it only ever selects a BTHENUM\DEV_<address> node, and the container is read from
    // that node, never taken from the command line.
    //
    // Guards, each refusing with nothing written:
    //   - the device must have an A2DP sink service node (BTHENUM\{0000110B-...}) with the address in the same
    //     container, so only headphones or speakers can be pinned and a paired phone never can;
    //   - moving the pin away from a device whose nodes are still disabled would leave it disabled with nothing
    //     in Earshot able to allow it again;
    //   - moving the pin while protection.json lists services Earshot turned off (or cannot be read) would make
    //     protect-off and the uninstall restore turn services on for the new device and never for the old one.
    //     The one exception is a device pinned now that has been removed from Windows (no node, not paired): its
    //     entries went with its pairing, so they are emptied here rather than hold the pin for good.
    // A node read that fails in either of the first two checks refuses with Failed: it says nothing about the
    // device, so it is neither "not an audio device" nor "not blocked".
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio
    private VerbResult SetDevice(string address, List<StepOutcome> steps)
    {
        if (!BoundaryValidation.IsAddress12(address))
        {
            steps.Add(StepOutcomes.NotAttempted("set-device", "The address is not 12 upper-case hex characters."));
            return new VerbResult(GateExitCode.Rejected, null);
        }

        using DeviceChangeLock? changing = AcquireForNodeChange(steps);
        if (changing is null)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        uint cr = _nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return new VerbResult(GateExitCode.Failed, null);
        }

        string prefix = @"BTHENUM\DEV_" + address + @"\";
        List<string> matches = ids.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            steps.Add(StepOutcomes.FromConfigRet("set-device-find", CfgMgr32.CR_NO_SUCH_DEVNODE, "No Bluetooth device node has this address."));
            return new VerbResult(GateExitCode.NotFound, nameof(BlockState.NotFound));
        }

        if (matches.Count > 1)
        {
            steps.Add(StepOutcomes.NotAttempted("set-device-find", "More than one device node has this address."));
            return new VerbResult(GateExitCode.Failed, null);
        }

        string deviceId = matches[0];
        cr = _nodes.Locate(deviceId, includeNonPresent: true, out uint devInst);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + deviceId, cr));
            return new VerbResult(GateExitCode.Failed, null);
        }

        cr = _nodes.GetContainerId(devInst, out Guid container);
        steps.Add(StepOutcomes.FromConfigRet("cm-container:" + deviceId, cr));
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        if (!NodeMatch.IsDisableTarget(deviceId, container, container, address))
        {
            steps.Add(StepOutcomes.NotAttempted("set-device", "The node's container is empty or the PC container."));
            return new VerbResult(GateExitCode.Failed, null);
        }

        switch (FindAudioSinkNode(ids, container, address, steps))
        {
            case SinkNode.None:
                steps.Add(StepOutcomes.NotAttempted("set-device",
                    "The device has no A2DP sink node, so it is not headphones or speakers and was not pinned."));
                return new VerbResult(GateExitCode.NotAudioSink, null);

            case SinkNode.Unreadable:
                // A read that failed is not a claim about the device.
                steps.Add(StepOutcomes.NotAttempted("set-device",
                    "An A2DP sink node with this address could not be read, so whether the device plays audio is not known and it was not pinned."));
                return new VerbResult(GateExitCode.Failed, null);

            case SinkNode.Found:
                break;
        }

        GateRead<DeviceIdentity> current = _store.ReadDevice();
        if (current.Status == GateReadStatus.Unreadable)
        {
            steps.Add(current.Step);
            return new VerbResult(GateExitCode.Failed, null);
        }

        if (current.Status == GateReadStatus.Invalid)
        {
            steps.Add(current.Step);
        }

        bool samePin = current.IsOk && current.Value is { } same &&
                       string.Equals(same.Address, address, StringComparison.Ordinal) && same.ContainerId == container;
        if (!samePin)
        {
            NodeReadResult? old = null;
            if (current.IsOk && current.Value is { } pinned)
            {
                old = new NodeStateReader(_nodes).Read(pinned.ContainerId, pinned.Address);
                if (!old.Listed)
                {
                    steps.AddRange(old.Steps);
                    return new VerbResult(GateExitCode.Failed, null);
                }

                if (old.Nodes.Any(n => n.Status == NodeBlockStatus.Disabled || n.ConfigFlagsDisabledBit))
                {
                    steps.Add(StepOutcomes.NotAttempted("set-device", "The device pinned now is still blocked. Allow it first."));
                    return new VerbResult(GateExitCode.OtherDeviceBlocked, null);
                }

                if (old.Unread.Count > 0)
                {
                    steps.AddRange(old.Steps.Where(s => !s.Ok));
                    steps.Add(StepOutcomes.NotAttempted("set-device",
                        "Whether the device pinned now is still blocked could not be read, so the pin was not moved."));
                    return new VerbResult(GateExitCode.Failed, null);
                }
            }

            GateRead<ProtectionRecord> protection = _store.ReadProtection();
            if (protection.Status != GateReadStatus.Missing && !(protection.IsOk && protection.Value is { DisabledServices.Count: 0 }))
            {
                // The entries of a device removed from Windows are void (ProtectionGateRunner.IsRemovedFromThisPc):
                // its pairing, and the service state with it, is gone, so they must not hold the pin for good.
                if (protection.IsOk && old is not null && current.Value is { } gone &&
                    ProtectionGateRunner.IsRemovedFromThisPc(old, _bluetooth, gone.Address, steps))
                {
                    if (ProtectionGateRunner.VoidRecord(_store, gone.Address, steps, _log) != GateExitCode.Success)
                    {
                        return new VerbResult(GateExitCode.Failed, null);
                    }
                }
                else
                {
                    steps.Add(protection.Step);
                    steps.Add(StepOutcomes.NotAttempted("set-device",
                        protection.IsOk
                            ? "protection.json lists services turned off on the device pinned now. Turn them back on (protect-off) first."
                            : "protection.json could not be read, so it may list services turned off on the device pinned now."));
                    return new VerbResult(GateExitCode.OtherDeviceProtected, null);
                }
            }
        }

        StepOutcome written = _store.WriteDevice(new DeviceIdentity { Address = address, ContainerId = container });
        steps.Add(written);
        return new VerbResult(written.Ok ? GateExitCode.Success : GateExitCode.Failed, null);
    }

    // The instance id prefix of an A2DP sink service node: BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_...
    internal const string AudioSinkNodePrefix = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_";

    private enum SinkNode
    {
        Found,       // a node with the A2DP sink prefix, the address and the device's container
        None,        // every candidate was read, and none is in the device's container
        Unreadable,  // none found, and a candidate carrying the address could not be located or its container read
    }

    // Looks for a node with the A2DP sink prefix, the address and the device's container (present or not). A
    // candidate that cannot be read is a failed step and makes the answer Unreadable unless another one is found.
    private SinkNode FindAudioSinkNode(string[] ids, Guid container, string address, List<StepOutcome> steps)
    {
        bool unreadable = false;
        foreach (string id in ids.Where(i => i.StartsWith(AudioSinkNodePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (!id.Contains(address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint cr = _nodes.Locate(id, includeNonPresent: true, out uint devInst);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, cr));
                unreadable = true;
                continue;
            }

            cr = _nodes.GetContainerId(devInst, out Guid nodeContainer);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-container:" + id, cr));
                unreadable = true;
                continue;
            }

            if (NodeMatch.IsDisableTarget(id, nodeContainer, container, address))
            {
                return SinkNode.Found;
            }
        }

        return unreadable ? SinkNode.Unreadable : SinkNode.None;
    }

    // The BootBlock task. Blocks only when config.json says BlockAtBoot is true; a missing or invalid
    // config is not taken as permission.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iboottrigger
    private VerbResult Boot(List<StepOutcome> steps)
    {
        GateRead<GateConfig> config = _store.ReadConfig();
        steps.Add(config.Step);
        if (!config.IsOk || config.Value is null)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        if (!config.Value.BlockAtBoot)
        {
            steps.Add(StepOutcomes.NotAttempted("boot-block", "Block at boot is off."));
            return new VerbResult(GateExitCode.Success, null);
        }

        return Block(steps);
    }

    private VerbResult Protect(GateRequest request, bool protect, List<StepOutcome> steps)
    {
        DeviceIdentity? identity = ReadIdentity(steps);
        if (identity is null)
        {
            return new VerbResult(GateExitCode.NoIdentity, null);
        }

        var ctx = new GateRunContext(request.Verb, request.Nonce, identity, _nodes, _store, _log, steps, _bluetooth);
        RunProtectVerb(ctx, protect);
        if (!ctx.Handled)
        {
            steps.Add(StepOutcomes.NotAvailable(request.Verb, NullResults.NotAvailableMessage));
            return new VerbResult(GateExitCode.NotAvailable, null);
        }

        return new VerbResult(ctx.Outcome, ctx.State);
    }

    // The device change lock for a node change, waiting DeviceChangeLock.NodeChangeLockTimeout. Null (with a
    // failed step) means change nothing.
    private DeviceChangeLock? AcquireForNodeChange(List<StepOutcome> steps) =>
        DeviceChangeLock.TryAcquire(_store.Folder, DeviceChangeLockAccess.For(_nodes), DeviceChangeLock.NodeChangeLockTimeout, LockWait, steps);

    // Runs the protection restore hook for uninstall. False when this build has no protection feature.
    public static bool TryRestoreProtection(GateRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        RunProtectionRestore(ctx);
        return ctx.Handled;
    }

    // Implemented by the audio protection feature in its own file (a part of this class). protect-on and
    // protect-off change the Handsfree and Headset service state; without an implementation they exit with
    // GateExitCode.NotAvailable.
    static partial void RunProtectVerb(GateRunContext ctx, bool protect);

    // Implemented by the audio protection feature: re-enables the services listed in protection.json.
    static partial void RunProtectionRestore(GateRunContext ctx);

    private string ReadState(DeviceIdentity identity)
    {
        NodeReadResult after = new NodeStateReader(_nodes).Read(identity.ContainerId, identity.Address);
        return BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, after).ToString();
    }

    // Persistently disables each target. Service nodes go first and the device node last; that order is a
    // choice (the device node stays up while its services go), not a documented requirement. A node already
    // disabled with the persistent flag is left alone; one disabled without it is disabled again with
    // CM_DISABLE_PERSIST.
    public static NodeChangeSummary ApplyBlock(INodeApi nodes, IReadOnlyList<TargetNode> targets, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        int changed = 0, already = 0, notPresent = 0, failed = 0;
        foreach (TargetNode target in targets.OrderBy(t => t.IsDeviceNode).ThenBy(t => t.InstanceId, StringComparer.Ordinal))
        {
            string id = target.InstanceId;
            if (!TryLocatePresent(nodes, id, "cm-disable:", steps, ref notPresent, ref failed, out uint devInst, out bool disabled))
            {
                continue;
            }

            if (disabled)
            {
                uint flagsCr = nodes.GetConfigFlags(devInst, out uint flags);
                if (flagsCr == CfgMgr32.CR_SUCCESS && (flags & CfgMgr32.CONFIGFLAG_DISABLED) != 0)
                {
                    steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, flagsCr, "Already disabled."));
                    already++;
                    continue;
                }
            }

            uint cr = nodes.Disable(devInst, BlockDisableFlags);
            steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, cr, DescribeDisable(cr)));
            if (cr == CfgMgr32.CR_SUCCESS)
            {
                changed++;
            }
            else
            {
                failed++;
            }
        }

        return new NodeChangeSummary(targets.Count, changed, already, notPresent, failed);
    }

    // Enables each target at problem 22 (CM_PROB_DISABLED), and each enabled target that still carries
    // CONFIGFLAG_DISABLED, which would come back disabled at the next restart. The device node goes first, then
    // its services. After each enable the flag is read again: an enable that leaves it set is a failure, since
    // the allow may not last a restart. A non-present target cannot be enabled: without the flag it comes back
    // enabled and counts as not present; with the flag it would come back disabled and counts as failed.
    // CM_DISABLE_PERSIST works by setting CONFIGFLAG_DISABLED (cfgmgr32.h); that CM_Enable_DevNode clears it is
    // checked here rather than assumed.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    public static NodeChangeSummary ApplyAllow(INodeApi nodes, IReadOnlyList<TargetNode> targets, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        int changed = 0, already = 0, notPresent = 0, failed = 0;
        foreach (TargetNode target in targets.OrderBy(t => !t.IsDeviceNode).ThenBy(t => t.InstanceId, StringComparer.Ordinal))
        {
            string id = target.InstanceId;
            int notPresentBefore = notPresent;
            if (!TryLocatePresent(nodes, id, "cm-enable:", steps, ref notPresent, ref failed, out uint devInst, out bool disabled))
            {
                if (notPresent > notPresentBefore && ReadFlag(nodes, target.PhantomDevInst, "cm-configflags:" + id, steps) is not false)
                {
                    // Flagged (or unreadable): it would come back disabled, so the allow is not complete.
                    notPresent--;
                    failed++;
                    steps.Add(StepOutcomes.NotAttempted("cm-enable:" + id,
                        "Not present and still marked disabled, so it may come back disabled. Connect the device to this PC once."));
                }

                continue;
            }

            if (!disabled)
            {
                bool? flagged = ReadFlag(nodes, devInst, "cm-configflags:" + id, steps);
                if (flagged is false)
                {
                    steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, CfgMgr32.CR_SUCCESS, "Already enabled."));
                    already++;
                    continue;
                }

                if (flagged is null)
                {
                    steps.Add(StepOutcomes.NotAttempted("cm-enable:" + id,
                        "Enabled, but whether it stays enabled after a restart could not be read, so it was not changed."));
                    failed++;
                    continue;
                }
            }

            uint cr = nodes.Enable(devInst);
            steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, cr, DescribeEnable(cr) + (disabled ? "" : " It was enabled but still marked disabled.")));
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                failed++;
                continue;
            }

            if (ReadFlag(nodes, devInst, "cm-configflags:" + id, steps) is not false)
            {
                steps.Add(StepOutcomes.NotAttempted("cm-enable:" + id, "Enabled, but still marked disabled, so it may be disabled again after a restart."));
                failed++;
                continue;
            }

            changed++;
        }

        return new NodeChangeSummary(targets.Count, changed, already, notPresent, failed);
    }

    // CONFIGFLAG_DISABLED on a node: true or false, or null when the flags could not be read (recorded as a
    // step). A node without the property has no flags set.
    private static bool? ReadFlag(INodeApi nodes, uint devInst, string step, IList<StepOutcome> steps)
    {
        uint cr = nodes.GetConfigFlags(devInst, out uint flags);
        if (cr == CfgMgr32.CR_NO_SUCH_VALUE)
        {
            return false;
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(step, cr, "The config flags could not be read."));
            return null;
        }

        return (flags & CfgMgr32.CONFIGFLAG_DISABLED) != 0;
    }

    // Locates a target in the live tree and reads whether it is at CM_PROB_DISABLED. A node that is not
    // present (CR_NO_SUCH_DEVNODE with CM_LOCATE_DEVNODE_NORMAL) cannot be changed and is reported as such.
    private static bool TryLocatePresent(
        INodeApi nodes, string id, string stepPrefix, IList<StepOutcome> steps,
        ref int notPresent, ref int failed, out uint devInst, out bool disabled)
    {
        disabled = false;
        uint cr = nodes.Locate(id, includeNonPresent: false, out devInst);
        if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            steps.Add(StepOutcomes.FromConfigRet(stepPrefix + id, cr, "Not present, so it cannot be changed. Connect the device to this PC once."));
            notPresent++;
            return false;
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(stepPrefix + id, cr, "Could not be located."));
            failed++;
            return false;
        }

        cr = nodes.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(stepPrefix + id, cr, "Status unreadable, so it was not changed."));
            failed++;
            return false;
        }

        disabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
        return true;
    }

    internal static string? DescribeDisable(uint cr) => cr switch
    {
        CfgMgr32.CR_SUCCESS => "Disabled until it is allowed again.",
        CfgMgr32.CR_NO_SUCH_DEVNODE => "Not present, so it cannot be disabled.",
        CfgMgr32.CR_NOT_DISABLEABLE => "Windows does not allow this node to be disabled.",
        CfgMgr32.CR_REMOVE_VETOED => "A driver or app refused to let the node go.",
        CfgMgr32.CR_ACCESS_DENIED => "Access denied.",
        CfgMgr32.CR_NEED_RESTART => "Windows needs a restart to finish.",
        _ => null,
    };

    internal static string DescribeEnable(uint cr) => cr switch
    {
        CfgMgr32.CR_SUCCESS => "Enabled.",
        CfgMgr32.CR_NO_SUCH_DEVNODE => "Not present, so it cannot be enabled.",
        CfgMgr32.CR_ACCESS_DENIED => "Access denied.",
        CfgMgr32.CR_NEED_RESTART => "Windows needs a restart to finish.",
        _ => "Windows did not enable it.",
    };

    internal static string Describe(StepOutcome step) =>
        step.Step + " " + step.CodeName + (step.Detail is null ? "" : " (" + step.Detail + ")");
}
