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

    // CONFIGFLAG_DISABLED on a node by instance id, present or not (SetupAPI; see SetupApiConfigFlags). The only way to
    // change a node that has left the tree.
    ConfigFlagsChange ClearDisabledFlag(string instanceId);

    ConfigFlagsChange SetDisabledFlag(string instanceId);

    // Pauses between two polls of the tree. Returning false ends the wait at once. A real machine sleeps; a test moves its
    // own clock.
    bool Pause(TimeSpan delay);
}

internal sealed class CfgMgr32NodeApi : CfgMgr32NodeReader, INodeApi
{
    private readonly SetupApiConfigFlagsWriter _flags = new();

    public uint Disable(uint devInst, uint flags) => CfgMgr32.CM_Disable_DevNode(devInst, flags);

    public uint Enable(uint devInst) => CfgMgr32.CM_Enable_DevNode(devInst, 0);

    public ConfigFlagsChange ClearDisabledFlag(string instanceId) => _flags.ClearDisabledFlag(instanceId);

    public ConfigFlagsChange SetDisabledFlag(string instanceId) => _flags.SetDisabledFlag(instanceId);

    public bool Pause(TimeSpan delay) => DeviceChangeLock.SleepAndContinue(delay);
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
    DeviceMismatch = 15,     // install with a container that is not the container of the address's device node
    UnsafeEnvironment = 16,  // a gate, install or uninstall run whose environment sets a variable Program.UnsafeRuntimeVariables names
    NoConfig = 17,           // a node change, set-device or protect verb while config.json is missing: not set up, or uninstall kept only the records
    Rejected = 20,          // the command line did not validate; nothing was done
    NotElevated = 21,        // not SYSTEM or an elevated administrator
    RunningAsSystem = 22,    // install or uninstall started as SYSTEM
    NotAService = 23,        // the service run mode started by anything other than the service control manager
    NotFromInstallFolder = 24, // the service run mode from an image outside %ProgramFiles%\Earshot
    Busy = 25,               // install, update or repair while another of them still holds the machine-wide lock; nothing was changed
    // 26 (AlreadyInstalled) and 27 (StillRunning) belong to the installer.
    InUse = 28,              // an automatic block (idle, undo-allow) found the AirPods connected; nothing was changed
    InProgress = 29,         // only in a status file: an allow wrote its change list and has not finished
    NotAirPods = 30,         // adopt-device for an address that is not AirPods by the identity rule
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
        [GateExitCode.DeviceMismatch] = "device-mismatch",
        [GateExitCode.UnsafeEnvironment] = "unsafe-environment",
        [GateExitCode.NoConfig] = "no-config",
        [GateExitCode.Rejected] = "rejected",
        [GateExitCode.NotElevated] = "not-elevated",
        [GateExitCode.RunningAsSystem] = "running-as-system",
        [GateExitCode.NotAService] = "not-a-service",
        [GateExitCode.NotFromInstallFolder] = "not-from-install-folder",
        [GateExitCode.Busy] = "busy",
        [GateExitCode.InUse] = "in-use",
        [GateExitCode.InProgress] = "in-progress",
        [GateExitCode.NotAirPods] = "not-airpods",
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

internal sealed record GateRequest(string Verb, string Nonce, string? Address, GateMode Mode = GateMode.Gate, string? AllowNonce = null);

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
    // The nodes an allow changed, in order, with how each stood before. Empty for every other call.
    public IReadOnlyList<GateChange> Changes { get; init; } = Array.Empty<GateChange>();

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
        new(new CfgMgr32NodeApi(), MachineStore(machineFolder), new NtfsFolderSecurity(), log, TimeProvider.System, new MachineGateMutex(), new BluetoothServiceApi());

    // The store of the real machine folder: its status files are written only into a folder held open and checked at that
    // moment.
    internal static GateStore MachineStore(string machineFolder) =>
        new(machineFolder, new WindowsFolderPinner(new NtfsFolderSecurity(), AclCheck.CheckMachineFolder));

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

    // Changes is what an allow changed, kept for the status file; null for every other verb.
    private sealed record VerbResult(GateExitCode Outcome, string? State, IReadOnlyList<GateChange>? Changes = null);

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
            result = held is null ? new VerbResult(GateExitCode.Failed, null)
                : NeedsConfig(request.Verb) && !ConfigPresent(steps) ? new VerbResult(GateExitCode.NoConfig, null)
                : request.Verb switch
                {
                    GateVerbs.Block => Block(steps, onlyIfIdle: false),
                    GateVerbs.BlockIdle => Block(steps, onlyIfIdle: true),
                    GateVerbs.Allow => Allow(request, started, steps),
                    GateVerbs.UndoAllow => UndoAllow(request, steps),
                    GateVerbs.AdoptDevice => AdoptDevice(request.Address ?? "", steps),
                    GateVerbs.Status => Status(steps),
                    GateVerbs.SetBootOn => SetBoot(true, steps),
                    GateVerbs.SetBootOff => SetBoot(false, steps),
                    GateVerbs.SetHandBackOn => SetHandBack(true, steps),
                    GateVerbs.SetHandBackOff => SetHandBack(false, steps),
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

            // An allow that stopped part-way still names what it had changed, so undo-allow can put exactly that back.
            result = new VerbResult(GateExitCode.Failed, null, request.Verb == GateVerbs.Allow ? _allowChanges : null);
        }

        if (held is not null)
        {
            steps.AddRange(_store.PruneStatusFiles(_time.GetUtcNow()));
        }

        GateExitCode outcome = result.Outcome;
        var status = new GateStatusFile(
            GateStore.StatusSchemaVersion, request.Nonce, request.Verb, started, _time.GetUtcNow(),
            GateExitCodes.ResultName(outcome), (int)outcome, result.State, steps, StepsTruncated: false, result.Changes);
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

    // The verbs that change a device or the pin, and the hand-back setting. Boot refuses without a valid config.json
    // on its own, status only reads, and set-boot-on and set-boot-off are what write the file when it is missing. The
    // hand-back setting is not: a request queued behind uninstall must not make a config.json again, which the
    // hand-back service would take as the sign that Earshot is set up.
    private static bool NeedsConfig(string verb) =>
        verb is GateVerbs.Block or GateVerbs.BlockIdle or GateVerbs.Allow or GateVerbs.UndoAllow or GateVerbs.SetDevice
            or GateVerbs.AdoptDevice or GateVerbs.ProtectOn or GateVerbs.ProtectOff or GateVerbs.SetHandBackOn or GateVerbs.SetHandBackOff;

    // Install writes config.json, and uninstall deletes it. An uninstall that could not restore everything keeps only
    // device.json and protection.json, so a run that was queued behind it, or one started although setup never
    // finished, finds no config.json and changes nothing: there are no tasks left to undo what it would do. A file
    // that is present but not valid or not readable does not stop an allow or a restore; only a missing one does.
    private bool ConfigPresent(List<StepOutcome> steps)
    {
        GateRead<GateConfig> config = _store.ReadConfig();
        if (config.Status != GateReadStatus.Missing)
        {
            return true;
        }

        steps.Add(config.Step);
        steps.Add(StepOutcomes.NotAttempted("config-check", "config.json is missing, so Earshot is not set up here and nothing was changed."));
        return false;
    }

    private DeviceIdentity? ReadIdentity(List<StepOutcome> steps)
    {
        GateRead<DeviceIdentity> read = _store.ReadDevice();
        steps.Add(read.Step);
        return read.IsOk ? read.Value : null;
    }

    // onlyIfIdle: an automatic block (the idle rule) that must not cut a live link. It reads the device node's link bit
    // under the device change lock, just before the first disable, and refuses with InUse instead.
    private VerbResult Block(List<StepOutcome> steps, bool onlyIfIdle)
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

        if (onlyIfIdle && LinkIsUp(scan, steps))
        {
            return new VerbResult(GateExitCode.InUse, ReadState(identity));
        }

        NodeChangeSummary summary = ApplyBlock(_nodes, scan.Targets, steps);
        return new VerbResult(WithUnreadable(summary.Outcome, scan), ReadState(identity));
    }

    // The step LinkIsUp records.
    internal const string InUseStep = "in-use-check";

    // True when the device node reports a live link: it is in the tree, not disabled, and DN_DEVICE_DISCONNECTED is clear.
    // The bit comes from a header comment ("the function driver ... reported that the device is not connected"), seen to
    // follow the link on this PC, not a documented contract. So it is a veto the tray corroborates and caps, never a
    // reason to leave the nodes enabled for good, and a node that is disabled, absent or whose status cannot be read is
    // not taken as connected.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/retrieving-the-status-and-problem-code-for-a-device-instance
    private bool LinkIsUp(NodeScanResult scan, List<StepOutcome> steps)
    {
        TargetNode? device = scan.Targets.FirstOrDefault(t => t.IsDeviceNode);
        if (device is null)
        {
            return false;
        }

        uint cr = _nodes.Locate(device.InstanceId, includeNonPresent: false, out uint devInst);
        if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            return false;
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(InUseStep, cr, "The device node could not be located, so a live link was not checked."));
            return false;
        }

        cr = _nodes.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(InUseStep, cr, "The device node status could not be read, so a live link was not checked."));
            return false;
        }

        bool disabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
        bool connected = !disabled && (status & CfgMgr32.DN_DEVICE_DISCONNECTED) == 0;
        steps.Add(new StepOutcome(
            InUseStep, !connected, connected ? (int)GateExitCode.InUse : 0, connected ? "in-use" : "S_OK",
            connected ? "The AirPods are connected, so nothing was changed." : "The AirPods are not connected."));
        return connected;
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

    // The change list of the allow in progress, as last written ahead, for a run that stops with an exception.
    private IReadOnlyList<GateChange>? _allowChanges;

    // The longest an allow's change list is trusted by undo-allow. A waiting budget, not a measured figure: a connect
    // ends within a minute, so a list older than this belongs to a run nobody is waiting on.
    internal static readonly TimeSpan UndoAllowMaxAge = TimeSpan.FromMinutes(10);

    // How long an allow waits for nodes that were not in the tree to be reported once the device node is enabled. Not
    // measured: the only observation is that they were back "within about 10 s". The time each one takes is written to
    // its step, so a live run can replace this with a figure.
    internal static readonly TimeSpan AppearWait = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan AppearPoll = TimeSpan.FromMilliseconds(100);

    // Allows the pinned device. The change list is written to the status file before each change, so a run that dies
    // part-way still leaves the list undo-allow needs.
    private VerbResult Allow(GateRequest request, DateTimeOffset started, List<StepOutcome> steps)
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

        var options = new AllowOptions
        {
            Time = _time,
            BeforeChange = changes =>
            {
                _allowChanges = changes.ToList();
                var snapshot = new GateStatusFile(
                    GateStore.StatusSchemaVersion, request.Nonce, request.Verb, started, _time.GetUtcNow(),
                    GateExitCodes.ResultName(GateExitCode.InProgress), (int)GateExitCode.InProgress, null, steps.ToList(), StepsTruncated: false, changes);
                return _store.WriteStatus(snapshot);
            },
        };
        NodeChangeSummary summary = ApplyAllow(_nodes, scan.Targets, steps, options);
        return new VerbResult(WithUnreadable(summary.Outcome, scan), ReadState(identity), summary.Changes);
    }

    // Puts the AirPods' nodes back as they stood before one allow, and nothing the allow did not change. The nodes come
    // from that allow's status file (second argument): present ones are disabled again with CM_DISABLE_PERSIST, ones that
    // had left the tree get the flag back. Each listed id must still pass the identity rule for the pinned device, or it is
    // left alone. Then the state is read, and anything the allow did not account for (a node that was enabled before it, or
    // one that is absent and unflagged) is blocked by the full block. A status file that is missing, not an allow's, or
    // older than UndoAllowMaxAge makes it the full block at once. Like the idle block it refuses with InUse, changing
    // nothing, when the AirPods are connected.
    private VerbResult UndoAllow(GateRequest request, List<StepOutcome> steps)
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

        if (LinkIsUp(scan, steps))
        {
            return new VerbResult(GateExitCode.InUse, ReadState(identity));
        }

        IReadOnlyList<GateChange>? listed = ReadAllowChanges(request.AllowNonce ?? "", steps);
        NodeChangeSummary summary;
        if (listed is null)
        {
            steps.Add(StepOutcomes.NotAttempted(UndoStep, "No usable allow record, so every node is blocked."));
            summary = ApplyBlock(_nodes, scan.Targets, steps);
        }
        else
        {
            var wanted = new List<TargetNode>();
            foreach (GateChange change in listed)
            {
                TargetNode? target = scan.Targets.FirstOrDefault(t => string.Equals(t.InstanceId, change.Id, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    steps.Add(StepOutcomes.NotAttempted(UndoStep, "A listed node is not one of this device's nodes, so it was left alone."));
                    continue;
                }

                wanted.Add(target);
            }

            summary = wanted.Count == 0 ? new NodeChangeSummary(0, 0, 0, 0, 0) : ApplyBlock(_nodes, wanted, steps);
            NodeReadResult after = new NodeStateReader(_nodes).Read(identity.ContainerId, identity.Address);
            if (after.Listed && !BlockStateClassifier.IsFullyBlocked(after))
            {
                steps.Add(StepOutcomes.NotAttempted(UndoStep, "The nodes are not all blocked after the undo, so the rest are blocked too."));
                summary = ApplyBlock(_nodes, scan.Targets, steps);
            }
        }

        GateExitCode outcome = summary.Total == 0 ? GateExitCode.Success : WithUnreadable(summary.Outcome, scan);
        return new VerbResult(outcome, ReadState(identity));
    }

    internal const string UndoStep = "undo-allow";

    // The change list of the allow with this nonce, or null when there is no usable one: the file is missing or unreadable,
    // is not an allow's, carries no list (a version 1 file), or is older than UndoAllowMaxAge.
    private IReadOnlyList<GateChange>? ReadAllowChanges(string allowNonce, List<StepOutcome> steps)
    {
        if (!BoundaryValidation.IsNonce(allowNonce))
        {
            steps.Add(StepOutcomes.NotAttempted(UndoStep, "The allow nonce is not valid."));
            return null;
        }

        GateRead<GateStatusFile> read = _store.ReadStatus(allowNonce);
        steps.Add(read.Step);
        if (!read.IsOk || read.Value is null)
        {
            return null;
        }

        GateStatusFile allow = read.Value;
        if (allow.Verb != GateVerbs.Allow || allow.Changed is null)
        {
            steps.Add(StepOutcomes.NotAttempted(UndoStep, "That status file is not an allow's, or lists no changes."));
            return null;
        }

        if (_time.GetUtcNow() - allow.FinishedUtc > UndoAllowMaxAge)
        {
            steps.Add(StepOutcomes.NotAttempted(UndoStep, "That allow is older than " + UndoAllowMaxAge.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " minutes."));
            return null;
        }

        return allow.Changed;
    }

    // Pins the AirPods with this address and blocks them, in one elevated run. The device must be AirPods by the identity
    // rule. The pin moves under the same guards as set-device (the device pinned now must not be left blocked with nothing
    // able to allow it, or hold services Earshot turned off). The new pair's nodes are then blocked, and the result is the
    // block's: a pin that moved with a block that failed is Partial, never Success.
    private VerbResult AdoptDevice(string address, List<StepOutcome> steps)
    {
        if (!BoundaryValidation.IsAddress12(address))
        {
            steps.Add(StepOutcomes.NotAttempted(GateVerbs.AdoptDevice, "The address is not 12 upper-case hex characters."));
            return new VerbResult(GateExitCode.Rejected, null);
        }

        using DeviceChangeLock? changing = AcquireForNodeChange(steps);
        if (changing is null)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
        steps.AddRange(found.Steps.Where(s => !s.Ok));
        if (!found.Listed)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        List<AirPodsCandidate> matches = found.Candidates.Where(c => c.Address == address).ToList();
        if (matches.Count == 0)
        {
            steps.Add(StepOutcomes.NotAttempted(GateVerbs.AdoptDevice, "No device with this address is AirPods by the identity rule, so nothing was pinned."));
            return new VerbResult(GateExitCode.NotAirPods, null);
        }

        if (matches.Count > 1)
        {
            steps.Add(StepOutcomes.NotAttempted(GateVerbs.AdoptDevice, "More than one container holds AirPods nodes with this address, so nothing was pinned."));
            return new VerbResult(GateExitCode.Failed, null);
        }

        Guid container = matches[0].ContainerId;
        GateExitCode move = CheckPinMove(_nodes, _bluetooth, _store, address, container, GateVerbs.AdoptDevice, steps, _log);
        if (move != GateExitCode.Success)
        {
            return new VerbResult(move, null);
        }

        var identity = new DeviceIdentity { Address = address, ContainerId = container };
        StepOutcome written = _store.WriteDevice(identity);
        steps.Add(written);
        if (!written.Ok)
        {
            return new VerbResult(GateExitCode.Failed, null);
        }

        NodeScanResult scan = NodeScan.FindTargets(_nodes, container, address);
        steps.AddRange(scan.Steps);
        if (!scan.Listed || scan.Targets.Count == 0)
        {
            return new VerbResult(GateExitCode.Partial, nameof(BlockState.Unknown));
        }

        NodeChangeSummary summary = ApplyBlock(_nodes, scan.Targets, steps);
        GateExitCode outcome = WithUnreadable(summary.Outcome, scan);
        return new VerbResult(outcome == GateExitCode.Success ? GateExitCode.Success : GateExitCode.Partial, ReadState(identity));
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
        GateConfig current = ReadCurrentConfig(steps);
        StepOutcome written = _store.WriteConfig(new GateConfig { BlockAtBoot = blockAtBoot, HandBackAtShutdown = current.HandBackAtShutdown });
        steps.Add(written);
        return new VerbResult(written.Ok ? GateExitCode.Success : GateExitCode.Failed, null);
    }

    // The hand-back service reads this setting from config.json, which the user's own settings file cannot reach.
    // The request carries one boolean and no path or address, and the other setting in the file is kept.
    private VerbResult SetHandBack(bool handBack, List<StepOutcome> steps)
    {
        GateConfig current = ReadCurrentConfig(steps);
        StepOutcome written = _store.WriteConfig(new GateConfig { BlockAtBoot = current.BlockAtBoot, HandBackAtShutdown = handBack });
        steps.Add(written);
        return new VerbResult(written.Ok ? GateExitCode.Success : GateExitCode.Failed, null);
    }

    // The settings in config.json now, so a write of one keeps the other. A file that is missing, unreadable or not
    // valid gives the defaults, as install does, and a file that is present but not valid is recorded as a step.
    private GateConfig ReadCurrentConfig(List<StepOutcome> steps)
    {
        GateRead<GateConfig> existing = _store.ReadConfig();
        if (existing.Status is GateReadStatus.Invalid or GateReadStatus.Unreadable)
        {
            steps.Add(existing.Step);
        }

        return existing.IsOk && existing.Value is not null ? existing.Value : new GateConfig();
    }

    // Re-pins device.json to the device with this address. The address is validated, but it is still
    // attacker-controlled: it only ever selects a BTHENUM\DEV_<address> node, and the container is read from
    // that node, never taken from the command line.
    //
    // Guards, each refusing with nothing written (the last two are CheckPinMove, which install applies too):
    //   - the device must be one ResolveAudioDevice accepts: headphones or speakers, never a paired phone;
    //   - moving the pin away from a device whose nodes are still disabled would leave it disabled with nothing
    //     in Earshot able to allow it again;
    //   - moving the pin while protection.json lists services Earshot turned off (or cannot be read) would make
    //     protect-off and the uninstall restore turn services on for the new device and never for the old one.
    //     The one exception is a device pinned now that has been removed from Windows (no node, not paired): its
    //     entries went with its pairing, so they are emptied here rather than hold the pin for good.
    // A node read that fails in either of the first two checks refuses with Failed: it says nothing about the
    // device, so it is neither "not an audio device" nor "not blocked".
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

        GateExitCode resolved = ResolveAudioDevice(_nodes, address, "set-device", steps, out Guid container);
        if (resolved != GateExitCode.Success)
        {
            return new VerbResult(resolved, resolved == GateExitCode.NotFound ? nameof(BlockState.NotFound) : null);
        }

        GateExitCode move = CheckPinMove(_nodes, _bluetooth, _store, address, container, "set-device", steps, _log);
        if (move != GateExitCode.Success)
        {
            return new VerbResult(move, null);
        }

        StepOutcome written = _store.WriteDevice(new DeviceIdentity { Address = address, ContainerId = container });
        steps.Add(written);
        return new VerbResult(written.Ok ? GateExitCode.Success : GateExitCode.Failed, null);
    }

    // Whether device.json may name the device with this address and container instead of the one it names now: the
    // guards set-device applies, shared with install, which writes device.json too. Success when nothing is pinned, the
    // same device is, or the pin may move. Otherwise nothing is written (except the voided record below):
    //   OtherDeviceBlocked    the device pinned now still has a disabled node, or one carrying the persistent flag,
    //                         which nothing in Earshot could allow again once the pin moves;
    //   OtherDeviceProtected  protection.json lists services turned off (or cannot be read), which protect-off and the
    //                         uninstall restore would then turn on for the new device and never for the old one. The
    //                         one exception is a device pinned now that has been removed from Windows (no node, not
    //                         paired): its entries went with its pairing, so they are emptied here;
    //   Failed                device.json or the old device's nodes could not be read, which says nothing either way.
    // step names the steps this adds (set-device, install-device).
    internal static GateExitCode CheckPinMove(
        INodeReader nodes, IBluetoothServiceReader? bluetooth, GateStore store, string address, Guid container, string step, IList<StepOutcome> steps, ILog log)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(log);
        GateRead<DeviceIdentity> current = store.ReadDevice();
        if (current.Status == GateReadStatus.Unreadable)
        {
            steps.Add(current.Step);
            return GateExitCode.Failed;
        }

        if (current.Status == GateReadStatus.Invalid)
        {
            steps.Add(current.Step);
        }

        bool samePin = current.IsOk && current.Value is { } same &&
                       string.Equals(same.Address, address, StringComparison.Ordinal) && same.ContainerId == container;
        if (samePin)
        {
            return GateExitCode.Success;
        }

        NodeReadResult? old = null;
        if (current.IsOk && current.Value is { } pinned)
        {
            old = new NodeStateReader(nodes).Read(pinned.ContainerId, pinned.Address);
            if (!old.Listed)
            {
                foreach (StepOutcome read in old.Steps)
                {
                    steps.Add(read);
                }

                return GateExitCode.Failed;
            }

            if (old.Nodes.Any(n => n.Status == NodeBlockStatus.Disabled || n.ConfigFlagsDisabledBit))
            {
                steps.Add(StepOutcomes.NotAttempted(step, "The device pinned now is still blocked. Allow it first."));
                return GateExitCode.OtherDeviceBlocked;
            }

            if (old.Unread.Count > 0)
            {
                foreach (StepOutcome read in old.Steps.Where(s => !s.Ok))
                {
                    steps.Add(read);
                }

                steps.Add(StepOutcomes.NotAttempted(step,
                    "Whether the device pinned now is still blocked could not be read, so the pin was not moved."));
                return GateExitCode.Failed;
            }
        }

        GateRead<ProtectionRecord> protection = store.ReadProtection();
        if (protection.Status == GateReadStatus.Missing || (protection.IsOk && protection.Value is { DisabledServices.Count: 0 }))
        {
            return GateExitCode.Success;
        }

        // The entries of a device removed from Windows are void (ProtectionGateRunner.IsRemovedFromThisPc): its
        // pairing, and the service state with it, is gone, so they must not hold the pin for good.
        if (protection.IsOk && old is not null && current.Value is { } gone &&
            ProtectionGateRunner.IsRemovedFromThisPc(old, bluetooth, gone.Address, steps))
        {
            return ProtectionGateRunner.VoidRecord(store, gone.Address, steps, log) == GateExitCode.Success
                ? GateExitCode.Success
                : GateExitCode.Failed;
        }

        steps.Add(protection.Step);
        steps.Add(StepOutcomes.NotAttempted(step,
            protection.IsOk
                ? "protection.json lists services turned off on the device pinned now. Turn them back on (protect-off) first."
                : "protection.json could not be read, so it may list services turned off on the device pinned now."));
        return GateExitCode.OtherDeviceProtected;
    }

    // The instance id prefix of an A2DP sink service node: BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_...
    internal const string AudioSinkNodePrefix = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_";

    // The device a pin names, from its address alone, for set-device and install. The address only ever selects
    // the BTHENUM\DEV_<address> node, and the container is read from that node. Success, with the container, only
    // for a device that has an A2DP sink service node (BTHENUM\{0000110B-...}) with the address in that same
    // container, so only headphones or speakers can be pinned and a paired phone never can:
    //   NotFound      no BTHENUM\DEV_ node has the address
    //   NotAudioSink  every candidate sink node was read, and none is the device's
    //   Failed        a read failed, more than one device node has the address, or the node's container is empty
    //                 or the PC container; a read that failed says nothing about the device
    // step names the steps this adds (set-device, install-device).
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio
    internal static GateExitCode ResolveAudioDevice(INodeReader nodes, string address, string step, IList<StepOutcome> steps, out Guid container)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentNullException.ThrowIfNull(steps);
        container = Guid.Empty;
        if (!BoundaryValidation.IsAddress12(address))
        {
            steps.Add(StepOutcomes.NotAttempted(step, "The address is not 12 upper-case hex characters."));
            return GateExitCode.Rejected;
        }

        uint cr = nodes.ListDeviceIds(out string[] ids);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-list", cr, "The device list could not be read."));
            return GateExitCode.Failed;
        }

        string prefix = @"BTHENUM\DEV_" + address + @"\";
        List<string> matches = ids.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            steps.Add(StepOutcomes.FromConfigRet(step + "-find", CfgMgr32.CR_NO_SUCH_DEVNODE, "No Bluetooth device node has this address."));
            return GateExitCode.NotFound;
        }

        if (matches.Count > 1)
        {
            steps.Add(StepOutcomes.NotAttempted(step + "-find", "More than one device node has this address."));
            return GateExitCode.Failed;
        }

        string deviceId = matches[0];
        cr = nodes.Locate(deviceId, includeNonPresent: true, out uint devInst);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + deviceId, cr));
            return GateExitCode.Failed;
        }

        cr = nodes.GetContainerId(devInst, out Guid found);
        steps.Add(StepOutcomes.FromConfigRet("cm-container:" + deviceId, cr));
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return GateExitCode.Failed;
        }

        if (!NodeMatch.IsDisableTarget(deviceId, found, found, address))
        {
            steps.Add(StepOutcomes.NotAttempted(step, "The node's container is empty or the PC container."));
            return GateExitCode.Failed;
        }

        switch (FindAudioSinkNode(nodes, ids, found, address, steps))
        {
            case SinkNode.None:
                steps.Add(StepOutcomes.NotAttempted(step,
                    "The device has no A2DP sink node, so it is not headphones or speakers and was not pinned."));
                return GateExitCode.NotAudioSink;

            case SinkNode.Unreadable:
                // A read that failed is not a claim about the device.
                steps.Add(StepOutcomes.NotAttempted(step,
                    "An A2DP sink node with this address could not be read, so whether the device plays audio is not known and it was not pinned."));
                return GateExitCode.Failed;
        }

        container = found;
        return GateExitCode.Success;
    }

    private enum SinkNode
    {
        Found,       // a node with the A2DP sink prefix, the address and the device's container
        None,        // every candidate was read, and none is in the device's container
        Unreadable,  // none found, and a candidate carrying the address could not be located or its container read
    }

    // Looks for a node with the A2DP sink prefix, the address and the device's container (present or not). A
    // candidate that cannot be read is a failed step and makes the answer Unreadable unless another one is found.
    private static SinkNode FindAudioSinkNode(INodeReader nodes, string[] ids, Guid container, string address, IList<StepOutcome> steps)
    {
        bool unreadable = false;
        foreach (string id in ids.Where(i => i.StartsWith(AudioSinkNodePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (!id.Contains(address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint cr = nodes.Locate(id, includeNonPresent: true, out uint devInst);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-locate-phantom:" + id, cr));
                unreadable = true;
                continue;
            }

            cr = nodes.GetContainerId(devInst, out Guid nodeContainer);
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

        return Block(steps, onlyIfIdle: false);
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

    // Persistently disables each target. Present service nodes go first and the device node after them; that order is a
    // choice (the device node stays up while its services go), not a documented requirement. A node already disabled
    // with the persistent flag is left alone; one disabled without it is disabled again with CM_DISABLE_PERSIST.
    //
    // A node that is not in the tree cannot be disabled (CM_Disable_DevNode needs a present node), but it keeps
    // CONFIGFLAG_DISABLED while it is away, and that flag decides what it does when Windows reports it again. So the
    // absent ones that are not flagged get the flag, last: the present nodes, which the time-capped paths (hand-back at
    // shut down and sleep) need first, are done before any SetupAPI write. Then every absent node is looked for again,
    // since one may have been reported meanwhile, and a node that is back and not disabled is disabled like any other.
    // A node with the flag already set is left alone.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    public static NodeChangeSummary ApplyBlock(INodeApi nodes, IReadOnlyList<TargetNode> targets, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        int changed = 0, already = 0, notPresent = 0, failed = 0;
        var absent = new List<TargetNode>();
        foreach (TargetNode target in targets.OrderBy(t => t.IsDeviceNode).ThenBy(t => t.InstanceId, StringComparer.Ordinal))
        {
            string id = target.InstanceId;
            NodeProbe probe = Probe(nodes, id, "cm-disable:", steps, ref failed, out uint devInst, out bool disabled);
            if (probe == NodeProbe.Absent)
            {
                absent.Add(target);
                continue;
            }

            if (probe == NodeProbe.Failed)
            {
                continue;
            }

            DisablePresent(nodes, id, devInst, disabled, steps, ref changed, ref already, ref failed);
        }

        // Each absent node counts once: as changed, already blocked or failed, whichever it ends as.
        var absentResult = new Dictionary<string, AbsentResult>(StringComparer.Ordinal);
        foreach (TargetNode target in absent)
        {
            string id = target.InstanceId;
            bool? flagged = ReadFlag(nodes, target.PhantomDevInst, "cm-configflags:" + id, steps);
            if (flagged is null)
            {
                absentResult[id] = AbsentResult.Failed;
                continue;
            }

            if (flagged.Value)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, CfgMgr32.CR_SUCCESS, "Not present, and already marked disabled."));
                absentResult[id] = AbsentResult.Already;
                continue;
            }

            ConfigFlagsChange set = nodes.SetDisabledFlag(id);
            bool took = set.Ok && set.DisabledAfter;
            steps.Add(FlagStep("cfg-set:" + id, set, took, "Not present, so it was marked disabled and stays disabled when Windows reports it."));
            absentResult[id] = took ? AbsentResult.Changed : AbsentResult.Failed;
        }

        // A node reported while the others were being changed is present now. Locating it again with the normal flag
        // is the only way to a CM_Disable_DevNode call, which is never made on a phantom instance.
        foreach (TargetNode target in absent)
        {
            string id = target.InstanceId;
            uint cr = nodes.Locate(id, includeNonPresent: false, out uint devInst);
            if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
            {
                continue;
            }

            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, cr, "Could not be located again after it was marked disabled."));
                absentResult[id] = AbsentResult.Failed;
                continue;
            }

            cr = nodes.GetStatus(devInst, out uint status, out uint problem);
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, cr, "Status unreadable after it was reported, so it was not disabled."));
                absentResult[id] = AbsentResult.Failed;
                continue;
            }

            bool nowDisabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
            int c = 0, a = 0, f = 0;
            DisablePresent(nodes, id, devInst, nowDisabled, steps, ref c, ref a, ref f);
            if (f > 0)
            {
                absentResult[id] = AbsentResult.Failed;
            }
            else if (c > 0 && absentResult.TryGetValue(id, out AbsentResult before) && before == AbsentResult.Already)
            {
                absentResult[id] = AbsentResult.Changed;
            }
        }

        changed += absentResult.Values.Count(r => r == AbsentResult.Changed);
        already += absentResult.Values.Count(r => r == AbsentResult.Already);
        failed += absentResult.Values.Count(r => r == AbsentResult.Failed);
        return new NodeChangeSummary(targets.Count, changed, already, notPresent, failed);
    }

    private enum AbsentResult
    {
        Changed,
        Already,
        Failed,
    }

    private static void DisablePresent(
        INodeApi nodes, string id, uint devInst, bool disabled, IList<StepOutcome> steps,
        ref int changed, ref int already, ref int failed)
    {
        if (disabled)
        {
            uint flagsCr = nodes.GetConfigFlags(devInst, out uint flags);
            if (flagsCr == CfgMgr32.CR_SUCCESS && (flags & CfgMgr32.CONFIGFLAG_DISABLED) != 0)
            {
                steps.Add(StepOutcomes.FromConfigRet("cm-disable:" + id, flagsCr, "Already disabled."));
                already++;
                return;
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

    // A SetupAPI flag change as a step: the raw Win32 error and where it stopped.
    private static StepOutcome FlagStep(string step, ConfigFlagsChange change, bool took, string done) =>
        StepOutcomes.FromWin32(
            step, change.Win32Error,
            took ? done : change.Ok
                ? "The flag change did not show when read back (flags " + change.Before.ToString("X", System.Globalization.CultureInfo.InvariantCulture) +
                  " then " + change.After.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")."
                : "Stopped at " + change.Stage + ".",
            ok: took);

    // How an allow goes about it, and what it needs from its caller.
    //   Time          the clock the appear wait is measured on
    //   AppearWait    how long to wait for absent nodes to be reported once the device node is enabled
    //   PollInterval  the pause between two looks (INodeApi.Pause)
    //   BeforeChange  called with the whole change list, including the change about to be made, BEFORE each change, so the
    //                 caller can write it down; a failing step is recorded and the change is still made, since the
    //                 undo then falls back to the full block
    internal sealed class AllowOptions
    {
        public TimeProvider Time { get; init; } = TimeProvider.System;

        public TimeSpan AppearWait { get; init; } = GateActions.AppearWait;

        public TimeSpan PollInterval { get; init; } = GateActions.AppearPoll;

        public Func<IReadOnlyList<GateChange>, StepOutcome?>? BeforeChange { get; init; }
    }

    // Enables each target. The device node goes first, then its services, and each node that is in the tree at problem 22
    // (CM_PROB_DISABLED), or enabled with CONFIGFLAG_DISABLED still set (which would send it back to disabled at the next
    // restart), is enabled with CM_Enable_DevNode and its flag read back: an enable that leaves the flag set is a failure.
    //
    // A node that is not in the tree cannot be enabled, and nothing here clears its flag: enabling the device node is what
    // makes Windows report its services again, so they are waited for (AppearWait, polled with INodeApi.Pause; the time
    // each took is in its step), and each that comes back at problem 22 or with the flag set is enabled like any other,
    // which clears the flag. A node that is absent and not flagged comes back enabled by itself, so it counts as already
    // allowed. One that is absent and flagged and never comes back is a failure: it would come back disabled. With no node
    // present at all, nothing is waited for.
    //
    // The change list (GateChange) names every node this call changed and how it stood before, in order; it is what
    // undo-allow puts back.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-configflags
    public static NodeChangeSummary ApplyAllow(INodeApi nodes, IReadOnlyList<TargetNode> targets, IList<StepOutcome> steps, AllowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(steps);
        var pass = new AllowPass(nodes, steps, options ?? new AllowOptions());
        var absent = new List<TargetNode>();
        bool anyPresent = false;
        foreach (TargetNode target in targets.OrderBy(t => !t.IsDeviceNode).ThenBy(t => t.InstanceId, StringComparer.Ordinal))
        {
            string id = target.InstanceId;
            NodeProbe probe = Probe(nodes, id, "cm-enable:", steps, ref pass.Failed, out uint devInst, out bool disabled);
            if (probe == NodeProbe.Failed)
            {
                continue;
            }

            if (probe == NodeProbe.Absent)
            {
                bool? flagged = ReadFlag(nodes, target.PhantomDevInst, "cm-configflags:" + id, steps);
                if (flagged is null)
                {
                    pass.Failed++;
                    continue;
                }

                if (!flagged.Value)
                {
                    // Not marked disabled: it comes back enabled by itself, so there is nothing to wait for.
                    steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, CfgMgr32.CR_SUCCESS, "Not present and not marked disabled, so it starts enabled when Windows reports it."));
                    pass.Already++;
                    continue;
                }

                absent.Add(target);
                continue;
            }

            anyPresent = true;
            pass.EnablePresent(id, devInst, disabled, GateChangeFrom.DisabledPresent);
        }

        if (absent.Count > 0)
        {
            pass.WaitForAbsent(absent, anyPresent);
        }

        return new NodeChangeSummary(targets.Count, pass.Changed, pass.Already, 0, pass.Failed) { Changes = pass.Changes };
    }

    // The counters and change list of one allow.
    private sealed class AllowPass(INodeApi nodes, IList<StepOutcome> steps, AllowOptions options)
    {
        public int Changed;
        public int Already;
        public int Failed;
        public List<GateChange> Changes { get; } = [];

        // Enables a node that is in the tree, unless it is enabled and carries no flag.
        public void EnablePresent(string id, uint devInst, bool disabled, GateChangeFrom from)
        {
            if (!disabled)
            {
                bool? flagged = ReadFlag(nodes, devInst, "cm-configflags:" + id, steps);
                if (flagged is false)
                {
                    steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, CfgMgr32.CR_SUCCESS, "Already enabled."));
                    Already++;
                    return;
                }

                if (flagged is null)
                {
                    steps.Add(StepOutcomes.NotAttempted("cm-enable:" + id,
                        "Enabled, but whether it stays enabled after a restart could not be read, so it was not changed."));
                    Failed++;
                    return;
                }
            }

            // Written down before the node is touched.
            Changes.Add(new GateChange(id, from));
            if (options.BeforeChange?.Invoke(Changes) is { Ok: false } writeFailed)
            {
                steps.Add(writeFailed);
            }

            uint cr = nodes.Enable(devInst);
            steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, cr, DescribeEnable(cr) + (disabled ? "" : " It was enabled but still marked disabled.")));
            if (cr != CfgMgr32.CR_SUCCESS)
            {
                Failed++;
                return;
            }

            if (ReadFlag(nodes, devInst, "cm-configflags:" + id, steps) is not false)
            {
                steps.Add(StepOutcomes.NotAttempted("cm-enable:" + id, "Enabled, but still marked disabled, so it may be disabled again after a restart."));
                Failed++;
                return;
            }

            Changed++;
        }

        // Waits for the nodes that were not in the tree. With no node present there is nothing that would bring them back.
        // Every node in absent carries CONFIGFLAG_DISABLED.
        public void WaitForAbsent(List<TargetNode> absent, bool anyPresent)
        {
            var waiting = new List<TargetNode>(absent);
            long start = options.Time.GetTimestamp();

            // The clock and the pauses both count, and the longer is the time spent: a pause that sleeps for real moves
            // both, and one that a test replaces moves only the pauses, so the wait always ends.
            TimeSpan paused = TimeSpan.Zero;
            TimeSpan Spent() => paused > options.Time.GetElapsedTime(start) ? paused : options.Time.GetElapsedTime(start);
            while (anyPresent && waiting.Count > 0 && Spent() < options.AppearWait)
            {
                if (!nodes.Pause(options.PollInterval))
                {
                    break;
                }

                paused += options.PollInterval;

                foreach (TargetNode item in waiting.ToList())
                {
                    string id = item.InstanceId;
                    uint cr = nodes.Locate(id, includeNonPresent: false, out uint devInst);
                    if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
                    {
                        continue;
                    }

                    waiting.Remove(item);
                    if (cr != CfgMgr32.CR_SUCCESS)
                    {
                        steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, cr, "Could not be located after it was reported."));
                        Failed++;
                        continue;
                    }

                    cr = nodes.GetStatus(devInst, out uint status, out uint problem);
                    if (cr != CfgMgr32.CR_SUCCESS)
                    {
                        steps.Add(StepOutcomes.FromConfigRet("cm-enable:" + id, cr, "Status unreadable after it was reported, so it was not changed."));
                        Failed++;
                        continue;
                    }

                    TimeSpan took = Spent();
                    steps.Add(StepOutcomes.FromConfigRet(
                        "cm-appear:" + id, CfgMgr32.CR_SUCCESS,
                        "Reported " + ((long)took.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " ms after the others were changed (the wait allows " + options.AppearWait.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                        " s, a budget that has not been measured)."));
                    bool nowDisabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
                    EnablePresent(id, devInst, nowDisabled, GateChangeFrom.FlaggedAbsent);
                }
            }

            foreach (TargetNode item in waiting)
            {
                steps.Add(StepOutcomes.NotAttempted("cm-enable:" + item.InstanceId,
                    anyPresent
                        ? "Not reported within " + options.AppearWait.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s and still marked disabled, so it comes back disabled."
                        : "Not present and still marked disabled, so it comes back disabled."));
                Failed++;
            }
        }
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

    private enum NodeProbe
    {
        Present,
        Absent,   // CR_NO_SUCH_DEVNODE with the normal locate: not in the tree now
        Failed,   // a failed step was recorded and the failure counted
    }

    // Locates a target in the live tree and reads whether it is at CM_PROB_DISABLED. A node that is not present
    // (CR_NO_SUCH_DEVNODE with CM_LOCATE_DEVNODE_NORMAL) is reported as Absent, with no step: what to do about it differs
    // between a block and an allow.
    private static NodeProbe Probe(
        INodeApi nodes, string id, string stepPrefix, IList<StepOutcome> steps, ref int failed, out uint devInst, out bool disabled)
    {
        disabled = false;
        uint cr = nodes.Locate(id, includeNonPresent: false, out devInst);
        if (cr == CfgMgr32.CR_NO_SUCH_DEVNODE)
        {
            return NodeProbe.Absent;
        }

        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(stepPrefix + id, cr, "Could not be located."));
            failed++;
            return NodeProbe.Failed;
        }

        cr = nodes.GetStatus(devInst, out uint status, out uint problem);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromConfigRet(stepPrefix + id, cr, "Status unreadable, so it was not changed."));
            failed++;
            return NodeProbe.Failed;
        }

        disabled = (status & CfgMgr32.DN_HAS_PROBLEM) != 0 && problem == CfgMgr32.CM_PROB_DISABLED;
        return NodeProbe.Present;
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
