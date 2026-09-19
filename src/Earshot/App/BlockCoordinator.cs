using System.Globalization;
using System.Runtime.ExceptionServices;
using Earshot.Audio.Connect;
using Earshot.AudioProtection;
using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.App;

// The single owner of connect, disconnect, block, allow and protection sequencing, and of the rule that puts
// the device nodes back to blocked when the AirPods are not in use. It keeps the at-rest invariant: with Block at
// boot on, the AirPods' device nodes are enabled only while the AirPods are in use on this PC, and persistently
// disabled at every other time, so a power cycle never finds them enabled.
//
// Threading. UI thread only: every public member is called there, every event it handles is raised there, and
// every await comes back there, so no state needs a lock. Long work (Core Audio, Task Scheduler, the gate) runs
// behind the controllers and is awaited, never waited on. Timers come from the TimeProvider and their
// continuations return to the UI thread.
//
// One operation at a time. A connect, disconnect, protection change, device change, menu action or automatic
// block waits for the one in flight. Cancelling an operation cancels its waits for the device and for reads,
// never a request already sent, and its task completes only after its clean-up (a re-block included) has finished.
//
// Gate changes are waited for. An allow, block, protect-on, protect-off or set-device is never cancelled once
// sent: the gate runs it whatever the tray does, a node read taken before it ends can say Blocked just before an
// allow lands, and a block sent while a protect verb still runs would disable the nodes under a Bluetooth service
// change, which the order below forbids. A cancellation is acted on at the next step. A result that does not show
// the run ended (the gate's wait ran out, or a state read failed while it was polled) may still run for
// UnsettledGateWindow: no block is sent while a protect verb may still run, no block or allow is sent while a
// set-device may still move the pin (the gate acts on whichever device it pins when the request runs), the pin
// does not move while any change may still run, a Block at boot setting read as off is not taken as settled while
// a change to it may still land, and the state is read again until the window has passed.
//
// Connect.
//   1. ConnectAsync. Confirmed: the AirPods are in use, the nodes stay enabled, protection is checked again.
//   2. NodesBlocked: allow first only when the node read says Blocked or Mixed for this device. Show "Allowing",
//      allow, wait for a render endpoint to come back (EndpointWait), connect again.
//   3. Not confirmed, the A2DP filter turned the request down, protection is on and a good read shows render not
//      ACTIVE: turn protection off, connect again, put the saved protection setting back (the Hands-Free
//      assisted way).
//   4. Anything else that did not reach ACTIVE, after an allow, a timeout, a failure or a cancellation: block again
//      when Block at boot is on, then report. A failed click never leaves the nodes enabled, and a card never says
//      the connect is still going once the nodes are blocked again.
// Disconnect. KS disconnect, then, with Block at boot on and the nodes enabled, block whatever the request
// returned. With Block at boot off the result is reported as it is; there is no other way.
// Idle. When a good read shows the nodes enabled, Block at boot on and render not ACTIVE, and that holds for
// IdleGrace with nothing in flight, block. Any operation, or render going ACTIVE, restarts the wait. A block that
// does not take is shown once and tried again after a longer wait (up to IdleRetryLimit), so the nodes are never
// left enabled for good and a gate that keeps failing is not run every IdleGrace.
// Reads again. Only a good read can rule out enabled nodes (Block at boot off, not set up, or blocked), whatever an
// earlier read showed. A read that fails, or shows a node or device state nothing can act on, is followed by
// another after RecheckDelay (doubling up to IdleRetryLimit). One failed read never turns the idle rule off until
// some other notification happens to arrive.
// Every automatic block checks again, just before the block is sent, that Block at boot is still on and the
// AirPods are still not in use, since protection may have been changed first and that takes time.
// Start-up. Once the first good snapshot and the first node read are in: nodes enabled and not in use, block at
// once; in use, log the evidence and leave them enabled for the idle rule. Then check protection.
// Session end. On WM_QUERYENDSESSION, with Block at boot on and the nodes not known to be blocked, start a block
// and return at once, unless a protect verb or a set-device this tray started may still be running. A windowless
// app can be ended about five seconds in, so this is a backstop only.
// https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession
// Exit. The operation in flight is cancelled; a gate change it already sent is waited for, then its clean-up
// runs, and a Block sequence starts no new protection step and blocks. Then, with Block at boot on, nodes enabled
// and the AirPods not in use, the nodes are blocked before Earshot closes. Whether to block is decided from a read
// taken once the operation in flight has ended, since that operation (setup, or turning Block at boot on) may
// change exactly what an earlier read showed. Closing while they are in use, or
// while a change may still run, is logged and noted for a card; the BootBlock task is then what blocks them.
//
// Nothing is decided from a read that failed or has not run: an unknown snapshot or node state never leads to a
// block, an allow or a protection change on its own. The one exception is putting back an allow this coordinator
// issued itself, which is undone even when the node read after it fails.
//
// Protection. Every change of services or nodes follows ProtectionPolicy, so a service state is only changed
// while the nodes are enabled. The saved Protect audio quality setting is the request: with it off, services
// that still read protected are restored at the next connect or start-up, so a restore that could not be applied
// is not lost when Earshot restarts. The microphone notice is shown once, after the first protect-on that leaves
// the services protected, whatever started it. It is remembered only once it was on screen: a card nobody clicked
// for can be held back by Windows, and then the notice follows the next click that leaves protection on.
internal sealed class BlockCoordinator : IDisposable
{
    public const string AllowingStatus = "Allowing";
    public const string CouldNotReachDriverMessage = "Could not reach the AirPods audio driver. Try again.";
    public const string TryingAnotherWayMessage = "Could not reach the AirPods audio driver. Trying another way.";
    public const string DidNotComeBackMessage = "The AirPods did not come back in time. Try again.";
    public const string DidNotConnectMessage = "The AirPods did not connect in time. Try again.";
    public const string NotAvailableMessage = "The AirPods are not available. Check that Bluetooth is on.";
    public const string BlockStatusUnreadableMessage = "Could not read the boot block status. Try again.";
    public const string CouldNotBlockMessage = "Could not block the AirPods. Try again.";
    public const string OtherDeviceMessage = "Boot block is set for another device. Choose device again.";
    public const string ConnectedNotProtectedMessage = "Connected, but audio quality protection did not apply.";
    public const string ProtectionCheckFailedMessage = "Could not check audio quality protection.";
    public const string ConnectedAtStartUpMessage = "Connected at start-up. The boot block did not hold.";
    public const string ClosedWhileInUseMessage = "Closed while in use, so the AirPods are not blocked.";
    public const string ClosedBeforeChangeEndedMessage = "Closed before the current change finished. The AirPods may not be blocked.";
    public const string ChangeStillRunningMessage = "Another change is still running. Try again shortly.";
    public const string SavedForNextConnectMessage = "Saved. It applies when the AirPods next connect.";
    public const string AlreadyProtectedMessage = "Audio quality is already protected";
    public const string AlreadyOffMessage = "Audio quality protection is already off";
    public const string SomethingWentWrongMessage = "Something went wrong. See the log.";
    public const string BlockAtBootOffMessage = "Block at boot is off";
    public const string BlockAtBootOffStillBlockedMessage = "Block at boot is off, but the AirPods are still blocked. Connect to allow them.";
    public const string BlockAtBootOffMayStillBeBlockedMessage = "Block at boot is off, but the AirPods may still be blocked. Connect to allow them.";
    public const string SessionEndingMessage = "Windows is shutting down or signing out, so nothing was changed. If it does not, restart Earshot.";
    public const string ChangesNotWatchedMessage ="Earshot cannot see audio device changes, so blocking may be late.";

    // How long render must stay not ACTIVE, with nothing in flight, before the nodes are blocked again. A
    // conservative waiting budget, not a measured figure: long enough that the churn of a protection change or a
    // brief drop does not disconnect someone who is listening. The owner's live test tunes it.
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(30);

    // The longest wait before an automatic block that did not take is tried again. The wait doubles from
    // IdleGrace after each failure in a row, up to this. A waiting budget, not a measured figure.
    public static readonly TimeSpan IdleRetryLimit = TimeSpan.FromMinutes(15);

    // How long after a read that failed, or showed a state nothing can act on, the state is read again while the
    // nodes may be enabled. It doubles after each such read in a row, up to IdleRetryLimit. A waiting budget, not
    // a measured figure.
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(30);

    // While watching for endpoint changes has failed, nothing says when the AirPods stop being used, so the device is
    // read again on a timer while the nodes may be enabled: RecheckDelay, doubling up to this. A waiting budget, not
    // a measured figure: it bounds how late the idle block can start.
    public static readonly TimeSpan UnwatchedRecheckLimit = TimeSpan.FromMinutes(2);

    // How long a gate change whose end was not seen may still run: the \Earshot\Protect task's PT5M execution
    // limit (the longer of the Gate and Protect limits), plus a margin for a run the scheduler queued behind
    // another. A waiting budget, not a measured figure.
    // https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-executiontimelimit
    public static readonly TimeSpan UnsettledGateWindow = TimeSpan.FromMinutes(6);

    // How long to wait after an allow for a render endpoint to come back before connecting. A waiting budget, not
    // a measured figure; nothing documents how long enabling a device node takes to bring its endpoints back.
    public static readonly TimeSpan EndpointWait = TimeSpan.FromSeconds(10);

    // How long a refresh of the audio devices is waited for. A refresh runs on the audio worker, behind any call
    // still inside the audio driver (ConnectionController leaves a KsProperty call that has not returned to finish,
    // and every later item queues behind it), so without a limit a stalled driver would hold the operation in
    // flight for good, a re-block after the coordinator's own allow included. A refresh that does not come back in
    // time is a read that failed: the device state is not known. A waiting budget, not a measured figure.
    public static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(5);

    // How long a read of the boot block status, the Bluetooth services or the kept protection request is waited
    // for. These reads call Task Scheduler COM, CfgMgr32 and the Bluetooth API on the thread pool, and nothing
    // documents a limit for any of them, so without one a read that never returns would hold the operation in flight
    // for good, a re-block included, and every later click and Exit behind it. A read that does not come back in
    // time is a read that failed: the state is not known and is read again later. A waiting budget, not a measured
    // figure.
    public static readonly TimeSpan StatusReadBudget = TimeSpan.FromSeconds(30);

    // ProtectionPolicy runs each change at most once per sequence, so a sequence ends well within this many steps.
    internal const int MaxProtectionSteps = 16;

    private const string ClosingBlockName = "block before closing";
    private const string InUseReason = "the AirPods are in use";
    private const string ProtectMayRunReason = "an audio quality change this tray started may still be running";
    private const string PinMayMoveReason = "a device change this tray sent may still move the pin to another device";

    private readonly IDeviceMonitor _monitor;
    private readonly IConnectionController _connection;
    private readonly IBlockController _block;
    private readonly IAudioProtectionController _protection;
    private readonly ISettingsStore _settings;
    private readonly ICardPresenter _cards;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly CoordinatorOptions _options;

    private DeviceSnapshot _snapshot;
    private BootBlockStatus? _blockStatus;
    private bool _blockStatusFresh;
    private long _blockReadsStarted;
    private long _blockReadApplied;
    private AudioProtectionSnapshot? _protectionStatus;
    private long _serviceReadsStarted;
    private long _serviceReadApplied;

    // The protection request kept by this tray while it could not be applied (ProtectionPolicy KeepIntent, or a
    // change that did not take), or null.
    private bool? _pendingProtect;

    // An automatic restore (the saved setting off, the services protected) that the controller did not take this
    // run, for example because Handsfree was turned off outside Earshot, so the setting is not taken as a pending
    // restore again until Earshot restarts.
    private bool _settingRestoreDeclined;

    // Gate changes this tray sent whose end was not seen may still run until these times.
    private DateTimeOffset _allowSettlesAt = DateTimeOffset.MinValue;
    private DateTimeOffset _protectSettlesAt = DateTimeOffset.MinValue;
    private DateTimeOffset _pinSettlesAt = DateTimeOffset.MinValue;
    private DateTimeOffset _bootSettingSettlesAt = DateTimeOffset.MinValue;
    private int _protectVerbsRunning;

    // The last refresh of the device state failed or did not come back in time, and no snapshot has arrived since,
    // so the snapshot in hand may be out of date: the idle rule takes the device state as not known.
    private bool _deviceReadFailed;

    // Allows this tray has sent and is still waiting for. A node read taken before one ends can still say Blocked.
    private int _allowsRunning;

    // A good read this run has shown the gate tasks set up. A later read that says not set up is then unexpected
    // (another uninstall, or a read of a task that changed under it) and is read again rather than settled.
    private bool _tasksSeenInstalled;

    // The last protect verb's end was not seen. The next Block sequence then blocks without another protect-on,
    // so a Protect task that keeps running out of time cannot hold the block back for good.
    private bool _lastProtectUnsettled;

    private Task? _current;
    private string? _currentName;
    private CancellationTokenSource? _currentCancel;
    private Task? _sessionBlock;
    private bool _sessionEnding;
    private bool _sessionBlockIssued;
    private bool _sessionBlockWhileInUse;
    private Task? _closingBlock;
    private CancellationTokenSource? _idleWait;
    private CancellationTokenSource? _recheck;
    private int _rechecksInARow;

    // Automatic blocks in a row that did not take, whether their card was shown, and the node read after the last
    // one. Cleared when the AirPods are used, the user connects or disconnects, a block takes, or a later good read
    // shows the node state or Block at boot changed.
    private int _idleFailures;
    private bool _idleFailureShown;
    private BootBlockStatus? _statusAtIdleFailure;

    private bool _noticeShowing;
    private bool _unwatchedNoted;
    private bool _statusRefreshInFlight;
    private bool _statusRefreshAgain;
    private bool _started;
    private bool _startChecked;
    private bool _closing;
    private bool _disposed;

    public BlockCoordinator(
        IDeviceMonitor monitor,
        IConnectionController connection,
        IBlockController block,
        IAudioProtectionController protection,
        ISettingsStore settings,
        ICardPresenter cards,
        ILog log,
        TimeProvider time,
        CoordinatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        _monitor = monitor;
        _connection = connection;
        _block = block;
        _protection = protection;
        _settings = settings;
        _cards = cards;
        _log = log;
        _time = time;
        _options = options;
        _snapshot = monitor.Current;
    }

    // Raised on the UI thread when an operation starts or ends, a status read changes what is known, or the block
    // before closing is about to be sent.
    public event EventHandler? Changed;

    // True while an operation, a block started at session end or the block before closing is in flight.
    public bool IsBusy => _current is not null || _sessionBlock is { IsCompleted: false } || _closingBlock is { IsCompleted: false };

    // True while something other than the block before closing is in flight, or waiting to run before it.
    public bool IsBusyBeyondClosingBlock =>
        (_current is not null && _currentName != ClosingBlockName) || _sessionBlock is { IsCompleted: false };

    // True from the first message that says the session is ending until WM_ENDSESSION says it was cancelled (see
    // OnSessionEnding). While it is true a connect, an allow, a setting change, a device change and setup are
    // refused with SessionEndingMessage; the tray asks first so it can refuse before it saves anything.
    public bool SessionEndInProgress => _sessionEnding;

    // True once Earshot is closing and a block is about to be sent, so a card saying so is true.
    public bool BlockingBeforeClosing { get; private set; }

    // The last boot block status read that worked, or null before one has. It may be older than a later read
    // that failed; automatic actions only use it while the last read worked.
    public BootBlockStatus? BlockStatus => _blockStatus;

    // The last audio protection read that worked, or null before one has.
    public AudioProtectionSnapshot? ProtectionStatus => _protectionStatus;

    // What the user should know as Earshot closes (the AirPods were in use, a change may still run, or the block
    // before closing did not take), once the block before closing has finished; null when there is nothing to say.
    public string? ClosingNotice { get; private set; }

    // True while an idle wait is running, for tests and the log.
    internal bool IdleWaitRunning => _idleWait is not null;

    // True while a read of the state is scheduled because the last one could not settle it, for tests.
    internal bool RecheckRunning => _recheck is not null;

    // Automatic blocks in a row that did not take, for tests.
    internal int IdleFailures => _idleFailures;

    // The protection request this tray keeps, for tests.
    internal bool? PendingProtect => _pendingProtect;

    // An allow this tray sent is still being waited for, or may still enable the nodes.
    internal bool AllowMayLand => _allowsRunning > 0 || _time.GetUtcNow() < _allowSettlesAt;

    // A protect verb this tray sent is running or may still run.
    internal bool ProtectMayRun => _protectVerbsRunning > 0 || _time.GetUtcNow() < _protectSettlesAt;

    // A set-device this tray sent may still move the pin: its end was not seen.
    internal bool PinMayMove => _time.GetUtcNow() < _pinSettlesAt;

    // A Block at boot change this tray sent may still write config.json: its end was not seen.
    internal bool BootSettingMayChange => _time.GetUtcNow() < _bootSettingSettlesAt;

    private EarshotSettings Settings => _settings.Current;

    private BootBlockStatus? FreshStatus => _blockStatusFresh ? _blockStatus : null;

    // How long the idle rule waits now: IdleGrace, doubled after each automatic block in a row that did not take.
    private TimeSpan IdleDelay => Doubled(IdleGrace, _idleFailures);

    // Subscribes to the device monitor and settings and reads the status, which starts the start-up check once
    // the first good snapshot and node read are in. Call once, on the UI thread, before the monitor starts.
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Observe(_monitor.Current);
        _monitor.SnapshotChanged += OnSnapshotChanged;
        _settings.Changed += OnSettingsChanged;
        _ = RefreshStatusAsync();
    }

    // Earshot is closing (Exit): no idle wait, no reads again and no start-up check. The operation in flight is
    // cancelled; a gate change it sent is waited for, its clean-up still runs, and a Block sequence starts no new
    // protection step. Then, unless the nodes are known to be blocked or Block at boot is off, the block before
    // closing runs: it reads the state again and blocks enabled nodes that are not in use. WhenIdleAsync waits for
    // all of it.
    public void BeginShutdown()
    {
        if (!MarkClosing())
        {
            return;
        }

        if (!_started || _disposed)
        {
            return;
        }

        string why = ClosingBlockBlocker();
        if (why.Length > 0)
        {
            _log.Info("Closing: no block before closing, because " + why + ".");
            return;
        }

        _closingBlock = BlockBeforeClosingAsync();
        RaiseChanged();
    }

    // Earshot is closing without Exit, as the message loop ends: no idle wait, no start-up check, the operation in
    // flight is cancelled, and no block before closing is queued, since nothing would be left to run it. After
    // BeginShutdown this does nothing.
    public void Stop() => MarkClosing();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        MarkClosing();
        _disposed = true;
        if (_started)
        {
            _monitor.SnapshotChanged -= OnSnapshotChanged;
            _settings.Changed -= OnSettingsChanged;
        }
    }

    // Completes when nothing is in flight: the operation running now, any that were waiting behind it, the
    // session-end block and the block before closing.
    public async Task WhenIdleAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task? busy = _current ??
                         (_sessionBlock is { IsCompleted: false } session ? session : null) ??
                         (_closingBlock is { IsCompleted: false } closing ? closing : null);
            if (busy is null)
            {
                return;
            }

            await busy.WaitAsync(ct);
        }
    }

    // Reads the boot block and protection status in the background. Overlapping requests are merged into one more
    // pass. A read that fails is logged with its code and leaves the last good value for display only.
    public async Task RefreshStatusAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_statusRefreshInFlight)
        {
            _statusRefreshAgain = true;
            return;
        }

        _statusRefreshInFlight = true;
        try
        {
            do
            {
                _statusRefreshAgain = false;
                await ReadBlockStatusAsync(CancellationToken.None);
                await ReadServicesAsync(CancellationToken.None);
                EvaluateIdle();
                TryStartUpCheck();
            }
            while (_statusRefreshAgain && !_disposed);
        }
        finally
        {
            _statusRefreshInFlight = false;
        }
    }

    // Reads the boot block status now, within StatusReadBudget, and keeps it. Null when the read failed or did not
    // come back in time (logged either way). Throws OperationCanceledException only when ct is cancelled.
    public async Task<BootBlockStatus?> ReadBlockStatusAsync(CancellationToken ct = default)
    {
        long read = ++_blockReadsStarted;
        using var budget = new CancellationTokenSource(StatusReadBudget, _time);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            // As for a device refresh: the token takes a read that has not started, and WaitAsync ends the wait for
            // one that has, whatever the controller does with the token.
            BootBlockStatus status = await _block.GetStatusAsync(within.Token).WaitAsync(within.Token);
            if (read > _blockReadApplied)
            {
                _blockReadApplied = read;
                _blockStatus = status;
                _blockStatusFresh = true;
                _tasksSeenInstalled |= status.TasksInstalled;
                if (_statusAtIdleFailure is { } before &&
                    (before.State != status.State || before.BlockAtBoot != status.BlockAtBoot))
                {
                    ReArmIdleRule("the node read changed from " + Describe(before) + " to " + Describe(status));
                }

                RaiseChanged();
            }

            return status;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            if (read > _blockReadApplied)
            {
                _blockReadApplied = read;
                _blockStatusFresh = false;
                RaiseChanged();
            }

            if (ex is OperationCanceledException && budget.IsCancellationRequested)
            {
                LogReadTimeout("block-status", "The boot block status");
            }
            else
            {
                LogReadFailure("block-status", "The boot block status could not be read.", ex);
            }

            return null;
        }
    }

    // A left click or the menu toggle. The report comes back after every clean-up, a cancelled one included.
    public async Task<ToggleReport> ToggleAsync(ToggleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = request.Connect ? "connect" : "disconnect";

        // A connect may send the allow that enables the nodes, so it is refused while a session end is in
        // progress. A disconnect still runs: it sends no allow, its only gate change is the block that follows it
        // (and that Block sequence starts no protect verb while the session ends), so it cannot leave the nodes
        // more enabled than it found them.
        Func<ToggleReport>? refused = request.Connect ? () => RefusedAtSessionEnd(request) : null;
        if (_sessionEnding && refused is not null)
        {
            _log.Info(name + ": not started, because the session is ending.");
            return refused();
        }

        // The user has acted, so the start-up check has nothing left to reconcile.
        if (!_startChecked)
        {
            _startChecked = true;
            _log.Info("Start-up check skipped: a " + name + " was asked for first.");
        }

        try
        {
            return await RunExclusiveAsync(
                name,
                token => request.Connect ? ConnectCoreAsync(request, token) : DisconnectCoreAsync(request, token),
                ct,
                refused);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The operation body turns its own cancellation into a report, so this is a cancellation while it
            // waited for the operation before it. Nothing was shown or sent for it.
            _log.Info(name + ": cancelled before it started.");
            return new ToggleReport(request.Connect, OpStatus.NotAttempted, "", Array.Empty<StepOutcome>(), Cancelled: true);
        }
    }

    // The Protect audio quality menu toggle. The caller has saved the setting. Throws OperationCanceledException
    // when ct is cancelled before the change starts, while it waits for a read, or between its steps; a protect
    // verb already sent is waited for first.
    public Task<ControllerResult> SetProtectionAsync(bool protect, CardPlace place, CancellationToken ct = default) =>
        RunExclusiveAsync(
            protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff,
            async token =>
            {
                ProtectionRun run = await RunProtectionAsync(ProtectionGoal.SetProtection, protect, place, notice: true, token);
                if (protect)
                {
                    ShowNoticeIfOwed(place);
                }

                if (run.ServiceChange is { } change)
                {
                    return change with { Steps = run.Steps };
                }

                if (run.IntentKept && run.Status?.State == BlockState.NotSetUp)
                {
                    // Protection only runs through the gate, which setup installs, so nothing would apply a kept
                    // request before then. The saved setting is what the checks after setup act on.
                    _pendingProtect = null;
                    run.Steps.Add(StepOutcomes.NotAttempted("protect-intent", "The boot block is not set up, so there is no gate to change the services."));
                    return new ControllerResult(OpStatus.NotAttempted, AudioProtectionController.NotSetUpMessage, run.Steps);
                }

                if (run.IntentKept)
                {
                    run.Steps.Add(StepOutcomes.NotAttempted("protect-intent", "The node state could not be read, so the request is kept until the AirPods next connect."));
                    return new ControllerResult(OpStatus.NotAttempted, SavedForNextConnectMessage, run.Steps);
                }

                return new ControllerResult(OpStatus.AlreadyInState, protect ? AlreadyProtectedMessage : AlreadyOffMessage, run.Steps);
            },
            ct,
            () => RefusedAtSessionEnd(protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff));

    // Choose device. Waits for the operation in flight, its clean-up included, so a re-block after an allow
    // finishes against the device the gate pins now before the pin moves. Refused while a change this tray sent
    // may still run, since the pin would move under it.
    //
    // The gate will not move the pin away from a device that is still blocked, or whose services protection.json
    // lists as turned off, since nothing in Earshot could undo either afterwards. With Block at boot on the
    // device pinned now is blocked at rest, so those refusals are worked through here, in ProtectionPolicy's
    // order: allow its nodes, turn its services back on while they are enabled, and ask again. Each step runs at
    // most once and only on a good read. Once the pin has moved the previous device is left allowed, as Windows
    // would have it without Earshot. If the pin does not move, what this change did is put back: the block when
    // Block at boot is on (a Block sequence, which turns protection back on first), otherwise protection alone.
    public Task<ControllerResult> ChangeDeviceAsync(string address12, CancellationToken ct = default) =>
        RunExclusiveAsync(GateVerbs.SetDevice, token => ChangeDeviceCoreAsync(address12, token), ct, () => RefusedAtSessionEnd(GateVerbs.SetDevice));

    // The Block at boot menu toggle. Turning it off also allows nodes a good read shows blocked, in the same
    // operation and in ProtectionPolicy's Allow order (the nodes, then protection again if Handsfree came back): with
    // the setting off the nodes are enabled at rest, as Windows has them, and nothing else would allow them before
    // the next connect. Turning it on changes no node; the idle rule blocks enabled nodes that are not in use.
    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CardPlace place, CancellationToken ct = default) =>
        RunExclusiveAsync(
            blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff,
            async token =>
            {
                // A gate change, so its result is waited for (see the class comment).
                ControllerResult set = await _block.SetBlockAtBootAsync(blockAtBoot, CancellationToken.None);
                if (CoordinatorRules.MayStillRun(set))
                {
                    // The queued run may still write config.json with no notification to say so, so a read that
                    // shows the setting off is read again until it could have ended.
                    _bootSettingSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
                    _log.Warn((blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff) +
                        ": the gate run did not end while it was waited for, so it may still change Block at boot until " + Utc(_bootSettingSettlesAt) + ".");
                }

                // Read at once, however the change ended, so the idle rule acts on the setting as it is now as soon as
                // this operation ends, never on the read from before the change.
                BootBlockStatus? status = await ReadBlockStatusAsync(CancellationToken.None);
                if (!set.IsSuccess)
                {
                    return set;
                }

                if (blockAtBoot)
                {
                    return set;
                }

                var steps = new List<StepOutcome>(set.Steps);
                if (status is null)
                {
                    _log.Warn("setboot-off: the nodes could not be read, so whether they are still blocked is not known.");
                    return new ControllerResult(OpStatus.Partial, BlockAtBootOffMayStillBeBlockedMessage, steps);
                }

                if (status.State is not (BlockState.Blocked or BlockState.Mixed))
                {
                    return set;
                }

                _log.Info("setboot-off: the nodes read " + Describe(status) + ", so they are allowed now.");
                ProtectionRun run = await RunProtectionAsync(ProtectionGoal.Allow, Settings.ProtectAudioQuality, place, notice: true, token);
                steps.AddRange(run.Steps);
                if (CoordinatorRules.PhaseOf(run.Status) != NodePhase.Allowed)
                {
                    return new ControllerResult(OpStatus.Partial, BlockAtBootOffStillBlockedMessage, steps);
                }

                return run.ServiceChangeFailed
                    ? new ControllerResult(OpStatus.Partial, run.ServiceChange?.UserMessage ?? BlockAtBootOffMessage, steps)
                    : ControllerResult.Ok(BlockAtBootOffMessage, steps);
            },
            ct,
            () => RefusedAtSessionEnd(blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff));

    // Runs a menu action (setup) as an operation, so it never overlaps a connect or a block. Such an action changes
    // what the boot block status reads (setup installs the tasks), so the status is read again before the
    // operation ends, however it ended: the idle rule then evaluates the state the action left, never the one read
    // before it.
    public Task<ControllerResult> RunAsync(string name, Func<CancellationToken, Task<ControllerResult>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunExclusiveAsync(
            name,
            async token =>
            {
                try
                {
                    return await operation(token);
                }
                finally
                {
                    await ReadBlockStatusAsync(CancellationToken.None);
                }
            },
            ct,
            () => RefusedAtSessionEnd(name));
    }

    // The result of a change that was not started because a session end is in progress. The tray shows it as a
    // card, as it does any result that is not a success.
    private static ControllerResult RefusedAtSessionEnd(string action) =>
        new(OpStatus.NotAttempted, SessionEndingMessage, [StepOutcomes.NotAttempted(action, "The session is ending, so nothing was sent.")]);

    // The same for a connect. Reported as cancelled because the session is ending, so the tray logs that reason
    // and treats it neither as a connect that worked nor as one that failed.
    private ToggleReport RefusedAtSessionEnd(ToggleRequest request)
    {
        ShowCard(request, SessionEndingMessage);
        return new ToggleReport(request.Connect, OpStatus.NotAttempted, SessionEndingMessage,
            [StepOutcomes.NotAttempted("connect", "The session is ending, so nothing was sent.")], Cancelled: true)
        {
            CancelledBecause = "the session is ending",
        };
    }

    // WM_QUERYENDSESSION and WM_ENDSESSION. Never waits: the block is started and its result logged later, if the
    // session survives long enough.
    //
    // Every such message that says the session is ending sets _sessionEnding, whether or not a block is sent for
    // it, and only WM_ENDSESSION with wParam FALSE clears it. What Windows documents: a window that answers TRUE
    // to WM_QUERYENDSESSION (ShellMessageWindow always does) "receives the WM_ENDSESSION message, regardless of
    // how the other applications respond", with wParam "set to the results of the WM_QUERYENDSESSION message", so
    // a session end another application refuses does reach here as wParam FALSE.
    // https://learn.microsoft.com/windows/win32/shutdown/wm-queryendsession
    // https://learn.microsoft.com/windows/win32/shutdown/wm-endsession
    // What those pages do not promise: how long after the query WM_ENDSESSION arrives, or any message at all when
    // a session end is abandoned after WM_ENDSESSION with wParam TRUE ("the session can end any time after all
    // applications have returned from processing this message" is all it says). So there is no documented moment
    // or time after which a session that was said to be ending is known not to be, and nothing here guesses one:
    // the flag then stays set until Earshot is restarted, which SessionEndingMessage says.
    public void OnSessionEnding(SessionEndingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.IsQuery && !e.Ending)
        {
            if (_sessionEnding && !_sessionBlockIssued)
            {
                _log.Info("The session end was cancelled. Connects and changes are taken again.");
            }

            if (_sessionBlockIssued)
            {
                _log.Info("The session end was cancelled after a block was queued for it.");
                if (_sessionBlockWhileInUse)
                {
                    // Whether a block drops an active link is a live-test item; if it does, this user was disconnected
                    // for a shutdown that did not happen, and the nodes stay blocked until the next connect allows them.
                    _log.Warn("That block was issued while the AirPods were in use, so they may have been disconnected. The nodes stay blocked until the next connect.");
                }
            }

            _sessionEnding = false;
            _sessionBlockIssued = false;
            _sessionBlockWhileInUse = false;
            return;
        }

        if (!_sessionEnding)
        {
            _sessionEnding = true;
            _log.Info("Session ending: no connect, allow, setting change or device change starts until Windows says the session end was cancelled.");
        }

        if (_sessionBlockIssued || _disposed)
        {
            return;
        }

        string why = SessionBlockBlocker();
        if (why.Length > 0)
        {
            _log.Info("Session ending: no block issued, because " + why + ".");
            if (_blockStatus is { BlockAtBoot: true, State: not BlockState.NotSetUp } && _currentCancel is { } inFlight)
            {
                // With Block at boot on, the operation in flight must not go on to send an allow as the session ends; it
                // stops at its next step, and its clean-up blocks again anything it already allowed.
                _log.Info("Session ending: " + (_currentName ?? "the operation in flight") + " is stopped.");
                inFlight.Cancel();
            }

            return;
        }

        _sessionBlockIssued = true;
        _sessionBlockWhileInUse = CoordinatorRules.RenderOf(_snapshot, WatchedContainer()) == RenderState.Active;
        CancelIdleWait("the session is ending");

        // A queued allow must not run after the block: the operation in flight is cancelled, and its own
        // clean-up blocks again anyway. An allow already sent runs first on the same gate queue.
        _currentCancel?.Cancel();

        DateTimeOffset issued = _time.GetUtcNow();
        Task<ControllerResult> block;
        try
        {
            block = _block.BlockAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("session-end-block", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Session ending: the block could not be started. " + TrayReport.DescribeStep(step), ex);
            return;
        }

        // Queued, not sent: the block runs on the system worker, behind any gate request already there. The gate
        // logs its own line with a UTC time once RunEx returns (TaskSchedulerGate).
        _log.Info("Session ending: block queued at " + Utc(issued) + " (" + (e.IsQuery ? "WM_QUERYENDSESSION" : "WM_ENDSESSION") + ")" +
            (_sessionBlockWhileInUse ? ", while the AirPods were in use." : "."));
        _sessionBlock = LogSessionBlockAsync(block, issued);
        RaiseChanged();
    }

    private bool MarkClosing()
    {
        if (_closing)
        {
            return false;
        }

        _closing = true;
        CancelIdleWait("Earshot is closing");
        StopRechecks(settled: false);
        _currentCancel?.Cancel();
        return true;
    }

    private string SessionBlockBlocker()
    {
        BootBlockStatus? status = _blockStatus;
        if (status is null)
        {
            return "the boot block status was never read";
        }

        if (!status.BlockAtBoot)
        {
            return BlockAtBootOffReason(status);
        }

        if (status.State == BlockState.NotSetUp)
        {
            return "the boot block is not set up";
        }

        if (_blockStatusFresh && status.State == BlockState.Blocked && !AllowMayLand)
        {
            return "the nodes are already blocked";
        }

        // A block must never disable the nodes under a Bluetooth service change (see ProtectionPolicy). The BootBlock task
        // blocks them at the next start instead.
        if (ProtectMayRun)
        {
            return ProtectMayRunReason + ", and a block now would disable the nodes under it";
        }

        // The gate would block whichever device it pins when the block runs; the BootBlock task blocks the pinned one.
        if (PinMayMove)
        {
            return PinMayMoveReason;
        }

        return "";
    }

    // Why no block before closing is queued, from what is known now, or "" when one is. The block itself reads the
    // state again once the operation in flight has finished, so while one is in flight, or a Block at boot change
    // may still land, it is always queued: setup and turning Block at boot on change exactly what the read before
    // them showed.
    private string ClosingBlockBlocker()
    {
        if (_sessionBlockIssued)
        {
            return "a block was issued for the session end";
        }

        if (_current is not null || BootSettingMayChange)
        {
            return "";
        }

        BootBlockStatus? status = _blockStatus;
        if (status is null)
        {
            return "the boot block status was never read";
        }

        if (!status.BlockAtBoot)
        {
            return BlockAtBootOffReason(status);
        }

        if (status.State == BlockState.NotSetUp)
        {
            return "the boot block is not set up";
        }

        if (_blockStatusFresh && status.State == BlockState.Blocked && !AllowMayLand)
        {
            return "the nodes are already blocked";
        }

        return "";
    }

    private async Task LogSessionBlockAsync(Task<ControllerResult> block, DateTimeOffset issued)
    {
        try
        {
            ControllerResult result = await block;
            string text = TrayReport.Describe("session-end block (queued " + Utc(issued) + ")", result.Status, result.UserMessage, result.Steps);
            _log.Write(result.IsSuccess ? LogLevel.Info : LogLevel.Warn, text);
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("session-end-block", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Session ending: the block queued at " + Utc(issued) + " failed. " + TrayReport.DescribeStep(step), ex);
        }
        finally
        {
            RaiseChanged();
            if (!_disposed)
            {
                _ = RefreshStatusAsync();
            }
        }
    }

    // Waits for the operation in flight, then runs body as the one in flight. The task body returns completes only
    // after body has, clean-up included.
    //
    // refusedAtSessionEnd is given by every operation that could enable the nodes or change a setting, the device
    // or the set-up (connect, protect, setboot, set-device, setup), and left null by those that can only make the
    // at-rest state safer or change nothing (disconnect, the idle block, the start-up check, the block before
    // closing). While a session end is in progress the first kind is refused with that result, at once when asked
    // for then and as soon as the work it waited behind ends when the session end began meanwhile: if the process
    // is killed by the shutdown, an allow sent now would leave the nodes enabled across it. Nothing waits for the
    // session end to be cancelled, so every wait here ends when the work in flight does, and that work has limits
    // of its own (see the class comment).
    private async Task<T> RunExclusiveAsync<T>(string name, Func<CancellationToken, Task<T>> body, CancellationToken ct, Func<T>? refusedAtSessionEnd = null)
    {
        while (true)
        {
            if (_sessionEnding && refusedAtSessionEnd is not null)
            {
                _log.Info(name + ": not started, because the session is ending.");
                return refusedAtSessionEnd();
            }

            if (_current is null && _sessionBlock is not { IsCompleted: false })
            {
                break;
            }

            Task waitFor = _current ?? _sessionBlock!;
            _log.Write(LogLevel.Debug, name + ": waiting for " + (_currentName ?? "the session-end block") + " to finish.");
            await waitFor.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _current = done.Task;
        _currentName = name;
        _currentCancel = cancel;
        CancelIdleWait(name + " started");
        _log.Write(LogLevel.Debug, name + ": started.");
        RaiseChanged();
        try
        {
            return await body(cancel.Token);
        }
        finally
        {
            _current = null;
            _currentName = null;
            _currentCancel = null;
            done.SetResult();
            _log.Write(LogLevel.Debug, name + ": finished.");
            RaiseChanged();
            EvaluateIdle();
        }
    }

    private async Task<ControllerResult> ChangeDeviceCoreAsync(string address12, CancellationToken ct)
    {
        string? why = AllowMayLand ? "an allow this tray sent may still enable the nodes of the device the gate pins now"
            : ProtectMayRun ? ProtectMayRunReason
            : PinMayMove ? PinMayMoveReason
            : null;
        if (why is not null)
        {
            _log.Warn("set-device: not sent, because " + why + " (until " + Utc(Max(Max(_allowSettlesAt, _protectSettlesAt), _pinSettlesAt)) + ").");
            return new ControllerResult(
                OpStatus.NotAttempted,
                ChangeStillRunningMessage,
                [StepOutcomes.NotAttempted(GateVerbs.SetDevice, "Not sent, because " + why + ".")]);
        }

        var steps = new List<StepOutcome>();
        var undo = new DeviceChangeUndo();
        try
        {
            while (true)
            {
                // Nothing more is sent once the change has been cancelled; what was sent is put back below.
                ct.ThrowIfCancellationRequested();

                // A gate change, so its result is waited for (see the class comment).
                ControllerResult pin = await _block.SetDeviceAsync(address12, CancellationToken.None);
                Record(steps, GateVerbs.SetDevice, pin);
                if (pin.IsSuccess)
                {
                    if (undo.AllowIssued || undo.ProtectOffIssued)
                    {
                        _log.Info("set-device: the pin moved to " + address12 + "; the previous device is left " +
                            (undo.AllowIssued ? "allowed" : "with its services on") + ", as Windows would have it without Earshot.");
                    }

                    return pin with { Steps = steps };
                }

                if (CoordinatorRules.MayStillRun(pin))
                {
                    // The queued run may still move the pin, and every gate request after it acts on whichever device
                    // it pins then: nothing is put back or blocked until it could have ended (UndoDeviceChangeAsync,
                    // BlockSkipReasonAsync), and the state is read again until then.
                    _pinSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
                    _log.Warn("set-device: the gate run did not end while it was waited for, so it may still move the pin to " + address12 +
                        " until " + Utc(_pinSettlesAt) + ".");
                    return await FailDeviceChangeAsync(pin.Status, pin.UserMessage, steps, undo);
                }

                SetDeviceRefusal refusal = CoordinatorRules.SetDeviceRefusalOf(pin);
                bool handledBefore = refusal switch
                {
                    SetDeviceRefusal.OldDeviceBlocked => undo.AllowIssued,
                    SetDeviceRefusal.OldDeviceProtected => undo.ProtectOffIssued,
                    _ => true,
                };
                if (handledBefore)
                {
                    return await FailDeviceChangeAsync(pin.Status, pin.UserMessage, steps, undo);
                }

                ct.ThrowIfCancellationRequested();
                BootBlockStatus? status = await ReadBlockStatusAsync(ct);
                if (status is null)
                {
                    return await FailDeviceChangeAsync(OpStatus.Failed, BlockStatusUnreadableMessage, steps, undo);
                }

                if (refusal == SetDeviceRefusal.OldDeviceBlocked)
                {
                    // Only on a good read that shows the nodes blocked: a node the gate counts as disabled that is not
                    // present cannot be allowed from here, and the refusal says what to do then.
                    if (status.State is not (BlockState.Blocked or BlockState.Mixed))
                    {
                        _log.Warn("set-device: the gate still counts the device pinned now as blocked, but the nodes read " + Describe(status) + ", so nothing is allowed.");
                        return await FailDeviceChangeAsync(pin.Status, pin.UserMessage, steps, undo);
                    }

                    undo.AllowIssued = true;
                    undo.BlockAtBoot = status.BlockAtBoot;
                    undo.Container = status.TargetContainerId;
                    _log.Info("set-device: the device pinned now is blocked (" + Describe(status) + "), so it is allowed before the pin moves.");
                    ControllerResult allow = await SendAllowAsync("allow (device change)", steps);
                    if (!allow.IsSuccess || CoordinatorRules.MayStillRun(allow))
                    {
                        return await FailDeviceChangeAsync(OpStatus.Failed, allow.UserMessage, steps, undo);
                    }
                }
                else
                {
                    // A service state only changes while every node is enabled (ProtectionPolicy).
                    if (!ProtectionPolicy.MayChangeServices(CoordinatorRules.PhaseOf(status)))
                    {
                        _log.Warn("set-device: protection.json lists services turned off on the device pinned now, but the nodes read " + Describe(status) + ", so they are not turned back on.");
                        return await FailDeviceChangeAsync(pin.Status, pin.UserMessage, steps, undo);
                    }

                    undo.ProtectOffIssued = true;
                    undo.Container = status.TargetContainerId;
                    _log.Info("set-device: protection.json lists services turned off on the device pinned now, so they are turned back on before the pin moves.");
                    ControllerResult off = await SendProtectionAsync(false);
                    Record(steps, GateVerbs.ProtectOff + " (device change)", off);
                    if (off.Status is OpStatus.Failed or OpStatus.NotAttempted || CoordinatorRules.MayStillRun(off))
                    {
                        return await FailDeviceChangeAsync(OpStatus.Failed, off.UserMessage, steps, undo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            bool cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (cancelled)
            {
                _log.Info("set-device: cancelled; putting back what it changed before it ends.");
            }
            else
            {
                _log.Error("set-device: unexpected error; putting back what it changed before it is reported.", ex);
            }

            await UndoDeviceChangeAsync(steps, undo);
            throw;
        }
    }

    private async Task<ControllerResult> FailDeviceChangeAsync(OpStatus status, string message, List<StepOutcome> steps, DeviceChangeUndo undo)
    {
        await UndoDeviceChangeAsync(steps, undo);
        return new ControllerResult(status is OpStatus.NotAttempted or OpStatus.Partial ? status : OpStatus.Failed, message, steps);
    }

    // Puts back what a device change that did not move the pin changed, while the pin still names that device. Never
    // throws: a failure is recorded as a step with its code and logged.
    private async Task UndoDeviceChangeAsync(List<StepOutcome> steps, DeviceChangeUndo undo)
    {
        try
        {
            if ((undo.AllowIssued || undo.ProtectOffIssued) && PinMayMove)
            {
                // The pin may no longer name the device this change allowed or turned the services on for, so a
                // block or protect-on now could land on the device chosen instead. The idle rule reads the state again
                // and blocks whatever device the gate pins once the run could have ended.
                steps.Add(StepOutcomes.NotAttempted("set-device-clean-up", "Not put back, because " + PinMayMoveReason + "."));
                _log.Warn("set-device clean-up: nothing is put back, because " + PinMayMoveReason + " until " + Utc(_pinSettlesAt) +
                    ". The device pinned before is left " + (undo.AllowIssued ? "allowed" : "with its services on") + " until the idle rule reads the state again.");
                undo.AllowIssued = false;
                undo.ProtectOffIssued = false;
                return;
            }

            if (undo.AllowIssued && undo.BlockAtBoot)
            {
                undo.AllowIssued = false;
                undo.ProtectOffIssued = false;
                _log.Info("set-device clean-up: blocking the device pinned now again.");
                ProtectionRun run = await RunBlockSequenceAsync(BlockReason.DeviceChangeCleanUp, undo.Container, CardPlace.NearTray, CancellationToken.None);
                steps.AddRange(run.Steps);
                if (run.Skipped is { } skipped)
                {
                    _log.Info("set-device clean-up: the nodes are not blocked again, because " + skipped + ".");
                }
                else if (!run.BlockTook)
                {
                    _log.Warn("set-device clean-up: the nodes could not be blocked again: " + (run.NodeChange?.UserMessage ?? "no block result") + ".");
                }

                return;
            }

            if (undo.ProtectOffIssued)
            {
                undo.ProtectOffIssued = false;
                await PutProtectionBackAsync(steps);
            }
        }
        catch (Exception ex)
        {
            steps.Add(StepOutcomes.FromHResult("set-device-clean-up", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false));
            _log.Error("set-device clean-up failed; the device pinned now may be left allowed or with its services on.", ex);
        }
    }

    private async Task<ToggleReport> ConnectCoreAsync(ToggleRequest request, CancellationToken ct)
    {
        // Shown once the connect is under way, not while it waits behind an operation it may never follow. Safe
        // mode refuses the connect, so no card promises it.
        if (!_options.SafeMode)
        {
            ShowCard(request, TrayStatus.CardConnecting);
        }

        var steps = new List<StepOutcome>();
        var cleanup = new ConnectCleanup();
        ReArmIdleRule("a connect was asked for");
        try
        {
            ConnectResult result = await _connection.ConnectAsync(request.Container, ct);
            steps.AddRange(result.Steps);
            if (result.Confirmed)
            {
                return await ConnectedAsync(request, steps, allowed: false, ct);
            }

            if (result.Outcome == ConnectOutcome.NodesBlocked)
            {
                if (PinMayMove)
                {
                    // The allow would enable whichever device the gate pins when it runs, which may be another one.
                    _log.Warn("connect: nothing is allowed, because " + PinMayMoveReason + " until " + Utc(_pinSettlesAt) + ".");
                    steps.Add(StepOutcomes.NotAttempted("connect-allow", "Not sent, because " + PinMayMoveReason + "."));
                    return Finish(request, OpStatus.Failed, ChangeStillRunningMessage, steps);
                }

                BootBlockStatus? status = await ReadBlockStatusAsync(ct);
                string? refusal = CoordinatorRules.AllowFirstRefusal(status, request.Container);
                if (refusal is not null)
                {
                    _log.Warn("connect: every render endpoint is NOTPRESENT and the nodes read " + (status is null ? "nothing" : Describe(status)) +
                        " for container " + request.Container.ToString("D") + ", so nothing is allowed.");
                    return Finish(request, OpStatus.Failed, refusal, steps);
                }

                // The allow cannot be taken back once sent, so a cancellation that came while the status was read
                // ends the connect before it.
                ct.ThrowIfCancellationRequested();
                cleanup.BlockAtBoot = status!.BlockAtBootKnown ? status.BlockAtBoot : null;
                ShowCard(request, AllowingStatus);
                cleanup.AllowIssued = true;
                ControllerResult allow = await SendAllowAsync("allow (connect)", steps);
                if (allow.Status is OpStatus.Failed or OpStatus.NotAttempted)
                {
                    return await FailConnectAsync(request, cleanup, steps, allow.UserMessage, ct);
                }

                await ReadBlockStatusAsync(ct);
                if (!await WaitForSnapshotAsync(request.Container, CoordinatorRules.RenderEndpointPresent, EndpointWait, ct))
                {
                    _log.Warn("connect: no render endpoint came back within " + Seconds(EndpointWait) + " of the allow.");
                    return await FailConnectAsync(request, cleanup, steps, DidNotComeBackMessage, ct);
                }

                result = await _connection.ConnectAsync(request.Container, ct);
                steps.AddRange(result.Steps);
                if (result.Confirmed)
                {
                    return await ConnectedAsync(request, steps, allowed: true, ct);
                }
            }

            // Only on observations: the A2DP filter was sent the request and refused it, a good read shows render
            // not ACTIVE, and a good read shows the services protected.
            if (CoordinatorRules.A2dpRejected(result) &&
                await RenderNotActiveAsync(request.Container) &&
                await ProtectionIsOnAsync(ct))
            {
                return await HandsFreeAssistedConnectAsync(request, steps, cleanup, ct);
            }

            return await FailConnectAsync(request, cleanup, steps, result, ct);
        }
        catch (Exception ex)
        {
            // A connect cancelled once its walk to the filters began carries what was sent to which filter.
            if (ex is ConnectCancelledException sent)
            {
                steps.AddRange(sent.Steps);
            }

            bool cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (cancelled)
            {
                _log.Info("connect: cancelled; cleaning up before it ends.");
            }
            else
            {
                _log.Error("connect: unexpected error; cleaning up before it is reported.", ex);
            }

            await CleanUpConnectAsync(request, cleanup, steps);
            if (cancelled)
            {
                return CancelledReport(connect: true, OpStatus.Failed, "", steps);
            }

            throw;
        }
    }

    // A connect that did not reach ACTIVE: clean up, then report what is true once the clean-up is done.
    private async Task<ToggleReport> FailConnectAsync(ToggleRequest request, ConnectCleanup cleanup, List<StepOutcome> steps, ConnectResult result, CancellationToken ct)
    {
        ConnectCleanUpOutcome outcome = await CleanUpConnectAsync(request, cleanup, steps);
        return outcome == ConnectCleanUpOutcome.InUse
            ? await ConnectedAsync(request, steps, allowed: cleanup.AllowIssued, ct)
            : Finish(request, OpStatus.Failed, CoordinatorRules.ConnectFailureMessage(result, blockedAgain: outcome == ConnectCleanUpOutcome.Blocked), steps);
    }

    private async Task<ToggleReport> FailConnectAsync(ToggleRequest request, ConnectCleanup cleanup, List<StepOutcome> steps, string message, CancellationToken ct)
    {
        ConnectCleanUpOutcome outcome = await CleanUpConnectAsync(request, cleanup, steps);
        return outcome == ConnectCleanUpOutcome.InUse
            ? await ConnectedAsync(request, steps, allowed: cleanup.AllowIssued, ct)
            : Finish(request, OpStatus.Failed, message, steps);
    }

    // Render reached ACTIVE: the nodes stay enabled, and protection is checked again (an allow may have brought
    // Hands-Free back, and Windows can turn it back on when the AirPods reconnect).
    private async Task<ToggleReport> ConnectedAsync(ToggleRequest request, List<StepOutcome> steps, bool allowed, CancellationToken ct)
    {
        ShowCard(request, TrayStatus.CardConnected);
        ReArmIdleRule("the AirPods connected");
        ProtectionRun run;
        try
        {
            run = await RunProtectionAsync(allowed ? ProtectionGoal.Allow : ProtectionGoal.Reverify, Settings.ProtectAudioQuality, request.Place, notice: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info("connect: connected; the protection check was cancelled.");
            return CancelledReport(connect: true, OpStatus.Success, TrayStatus.CardConnected, steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(StepOutcomes.FromHResult("protection-check", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false));
            _log.Error("connect: connected, but the protection check failed.", ex);
            return Finish(request, OpStatus.Partial, ConnectedNotProtectedMessage, steps);
        }

        steps.AddRange(run.Steps);
        if (run.ServiceChangeFailed)
        {
            return Finish(request, OpStatus.Partial, ConnectedNotProtectedMessage, steps);
        }

        ShowNoticeIfOwed(request.Place);
        return Finish(request, OpStatus.Success, TrayStatus.CardConnected, steps, showCard: false);
    }

    // The A2DP filter turned the request down while protection is on, so the Hands-Free filter is not there to
    // take it. Protection comes off, the connect is tried once more, and the saved protection setting is put back.
    // Inside the one operation, so none of the driver churn can start an idle block.
    private async Task<ToggleReport> HandsFreeAssistedConnectAsync(ToggleRequest request, List<StepOutcome> steps, ConnectCleanup cleanup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ShowCard(request, TryingAnotherWayMessage);
        cleanup.ProtectOffIssued = true;
        cleanup.ReblockAfterFallback = true;
        ControllerResult off = await SendProtectionAsync(false);
        Record(steps, "protect-off (connect)", off);
        if (off.Status is OpStatus.Failed or OpStatus.NotAttempted)
        {
            return await FailConnectAsync(request, cleanup, steps, CouldNotReachDriverMessage, ct);
        }

        if (!await WaitForSnapshotAsync(request.Container, CoordinatorRules.CaptureEndpointPresent, EndpointWait, ct))
        {
            _log.Warn("connect: the Hands-Free endpoint did not come back within " + Seconds(EndpointWait) + "; connecting anyway.");
        }

        ConnectResult again = await _connection.ConnectAsync(request.Container, ct);
        steps.AddRange(again.Steps);
        if (!again.Confirmed)
        {
            return await FailConnectAsync(request, cleanup, steps, again, ct);
        }

        ShowCard(request, TrayStatus.CardConnected);
        cleanup.ProtectOffIssued = false;
        cleanup.ReblockAfterFallback = false;
        ReArmIdleRule("the AirPods connected");

        // What goes back is the saved setting, the newest intent: a restore the user asked for while the AirPods
        // were blocked is not undone. It is kept until it is back, so a put-back that throws or is cancelled is
        // applied at the next connect. No microphone notice: this only restores what was there.
        bool protect = Settings.ProtectAudioQuality;
        _pendingProtect = protect;
        ProtectionRun back = await RunProtectionAsync(ProtectionGoal.SetProtection, protect, request.Place, notice: false, ct);
        steps.AddRange(back.Steps);
        bool restored = !back.ServiceChangeFailed && !back.IntentKept &&
                        (back.ServiceChange is not null
                            ? _pendingProtect is null
                            : back.Services is { } services && ProtectionPolicy.IsSatisfied(protect, services.State));
        if (!restored)
        {
            _pendingProtect = protect;
            _log.Warn("connect: connected the other way, but protection could not be put back as saved (" + (protect ? "on" : "off") + "); it is kept for the next connect.");
            return Finish(request, OpStatus.Partial, ConnectedNotProtectedMessage, steps);
        }

        _pendingProtect = null;
        return Finish(request, OpStatus.Success, TrayStatus.CardConnected, steps, showCard: false);
    }

    // Puts back what a connect that did not reach ACTIVE changed: protection it turned off, then the nodes it
    // allowed, when Block at boot is on. Never throws: a failure here is logged with its code and recorded, so the
    // report or the exception of the connect itself is what the caller sees.
    private async Task<ConnectCleanUpOutcome> CleanUpConnectAsync(ToggleRequest request, ConnectCleanup cleanup, List<StepOutcome> steps)
    {
        try
        {
            if (cleanup.ProtectOffIssued)
            {
                cleanup.ProtectOffIssued = false;
                await PutProtectionBackAsync(steps);
            }

            if (!cleanup.AllowIssued && !cleanup.ReblockAfterFallback)
            {
                return ConnectCleanUpOutcome.NothingChanged;
            }

            DeviceSnapshot? now = await RefreshSnapshotAsync();
            if (now is not null && CoordinatorRules.RenderOf(now, request.Container) == RenderState.Active)
            {
                _log.Info("connect clean-up: the AirPods are in use after all, so the nodes stay enabled.");
                return ConnectCleanUpOutcome.InUse;
            }

            // Every gate change this connect sent has ended or has been given up on, so this read is not taken
            // before an allow that is still on its way; one whose end was not seen is read again later.
            //
            // Only a setting read as off leaves this connect's own allow in place. One that could not be read now
            // (config.json unreadable, or the whole read failed) falls back to the setting read before the allow, and
            // when that was not known either, the allow is still put back: the nodes were blocked before the click.
            BootBlockStatus? status = await ReadBlockStatusAsync(CancellationToken.None);
            bool? blockAtBootNow = status is { BlockAtBootKnown: true } ? status.BlockAtBoot : null;
            bool blockAtBoot = blockAtBootNow ?? cleanup.BlockAtBoot ?? cleanup.AllowIssued;
            if (!blockAtBoot)
            {
                bool known = blockAtBootNow is not null || cleanup.BlockAtBoot is not null;
                _log.Info("connect clean-up: Block at boot is " + (known ? "off" : "not known") + ", so the nodes are not blocked.");
                return ConnectCleanUpOutcome.NodesLeftEnabled;
            }

            if (blockAtBootNow is null)
            {
                _log.Warn("connect clean-up: Block at boot could not be read now, so the setting read before the allow is used (" +
                    (cleanup.BlockAtBoot is { } before ? (before ? "on" : "off") : "not known either, and the allow is put back") + ").");
            }

            if (status?.State == BlockState.Blocked)
            {
                if (AllowMayLand)
                {
                    _log.Warn("connect clean-up: the nodes read Blocked, but the allow may still enable them, so they are read again until " + Utc(_allowSettlesAt) + ".");
                }

                return ConnectCleanUpOutcome.Blocked;
            }

            // Only an allow this operation issued is undone without a good node read.
            if (!cleanup.AllowIssued && !CoordinatorRules.NodesEnabled(status))
            {
                _log.Info("connect clean-up: the nodes read " + (status is null ? "nothing" : Describe(status)) + ", so nothing is blocked.");
                return ConnectCleanUpOutcome.NodesLeftEnabled;
            }

            _log.Info("connect clean-up: blocking the nodes again.");
            ProtectionRun run = await RunBlockSequenceAsync(BlockReason.ConnectCleanUp, request.Container, request.Place, CancellationToken.None);
            steps.AddRange(run.Steps);
            if (run.Skipped is { } why)
            {
                _log.Info("connect clean-up: the nodes are not blocked again, because " + why + ".");
                return why == InUseReason ? ConnectCleanUpOutcome.InUse : ConnectCleanUpOutcome.NodesLeftEnabled;
            }

            if (!run.BlockTook)
            {
                _log.Warn("connect clean-up: the nodes could not be blocked again: " + (run.NodeChange?.UserMessage ?? "no block result") + ".");
                return ConnectCleanUpOutcome.NodesLeftEnabled;
            }

            return ConnectCleanUpOutcome.Blocked;
        }
        catch (Exception ex)
        {
            steps.Add(StepOutcomes.FromHResult("connect-clean-up", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false));
            _log.Error("connect clean-up failed; the nodes may still be enabled.", ex);
            return ConnectCleanUpOutcome.NodesLeftEnabled;
        }
    }

    // The Hands-Free assisted way turned protection off and did not connect: the saved setting goes back while the
    // nodes are still enabled. While Earshot closes or the session ends no new protect verb is started, so the block
    // is not delayed and never runs beside one; the setting is applied at the next connect or start-up.
    private async Task PutProtectionBackAsync(List<StepOutcome> steps)
    {
        bool protect = Settings.ProtectAudioQuality;
        if (!protect)
        {
            _log.Info("connect clean-up: Protect audio quality is off in settings, so protection stays off.");
            return;
        }

        if (_closing || _sessionEnding)
        {
            _pendingProtect = true;
            _log.Info("connect clean-up: " + (_closing ? "Earshot is closing" : "the session is ending") + ", so protection is put back at the next connect.");
            return;
        }

        ControllerResult on = await SendProtectionAsync(true);
        Record(steps, "protect-on (clean-up)", on);
        if (!CoordinatorRules.ProtectedByResult(on) && on.Status != OpStatus.Partial)
        {
            _pendingProtect = true;
        }
    }

    private async Task<ToggleReport> DisconnectCoreAsync(ToggleRequest request, CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        ReArmIdleRule("a disconnect was asked for");
        ConnectResult? result = null;
        ExceptionDispatchInfo? fault = null;
        bool cancelled = false;
        try
        {
            result = await _connection.DisconnectAsync(request.Container, ct);
            steps.AddRange(result.Steps);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // A disconnect cancelled once its walk to the filters began carries what was sent to which filter.
            if (ex is ConnectCancelledException sent)
            {
                steps.AddRange(sent.Steps);
            }

            cancelled = true;
            _log.Info("disconnect: cancelled; the block still follows when Block at boot is on.");
        }
        catch (Exception ex)
        {
            fault = ExceptionDispatchInfo.Capture(ex);
            _log.Error("disconnect: unexpected error; the block still follows when Block at boot is on.", ex);
        }

        if (result is { Confirmed: true } && !cancelled)
        {
            ShowCard(request, TrayStatus.CardDisconnected);
        }

        // Blocking is the at-rest action, so it follows whatever the request returned. It is not cancellable.
        BlockAttempt block = await BlockAfterDisconnectAsync(request, steps);
        fault?.Throw();
        if (cancelled)
        {
            return CancelledReport(connect: false, OpStatus.Failed, "", steps);
        }

        DeviceSnapshot? now = await RefreshSnapshotAsync();
        RenderState render = now is null ? RenderState.Unknown : CoordinatorRules.RenderOf(now, request.Container);
        bool disconnected = render == RenderState.NotActive || (render == RenderState.Unknown && result is { Confirmed: true });
        if (!disconnected)
        {
            return Finish(request, OpStatus.Failed, CoordinatorRules.DisconnectFailureMessage(result), steps);
        }

        if (block.Issued && !block.Took)
        {
            return Finish(request, OpStatus.Partial, block.Message, steps);
        }

        return Finish(request, OpStatus.Success, TrayStatus.CardDisconnected, steps, showCard: result is not { Confirmed: true });
    }

    private async Task<BlockAttempt> BlockAfterDisconnectAsync(ToggleRequest request, List<StepOutcome> steps)
    {
        try
        {
            BootBlockStatus? status = await ReadBlockStatusAsync(CancellationToken.None);
            if (status is null)
            {
                _log.Warn("disconnect: the nodes are not blocked, because the boot block status could not be read.");
                return BlockAttempt.NotIssued;
            }

            if (!status.BlockAtBoot || status.State == BlockState.Blocked)
            {
                return BlockAttempt.NotIssued;
            }

            if (!CoordinatorRules.NodesEnabled(status))
            {
                _log.Info("disconnect: the nodes read " + status.State + ", so nothing is blocked.");
                return BlockAttempt.NotIssued;
            }

            ProtectionRun run = await RunBlockSequenceAsync(BlockReason.Disconnect, request.Container, request.Place, CancellationToken.None);
            steps.AddRange(run.Steps);
            if (run.Skipped is { } why)
            {
                _log.Info("disconnect: the nodes are not blocked now, because " + why + ".");
                return BlockAttempt.NotIssued;
            }

            return new BlockAttempt(Issued: true, run.BlockTook, run.NodeChange?.UserMessage ?? CouldNotBlockMessage);
        }
        catch (Exception ex)
        {
            steps.Add(StepOutcomes.FromHResult("disconnect-block", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false));
            _log.Error("disconnect: the block failed; the nodes may still be enabled.", ex);
            return new BlockAttempt(Issued: true, Took: false, CouldNotBlockMessage);
        }
    }

    // Block goal: protection first while the nodes are enabled, then the nodes, with the checks for why the
    // block is being issued made again just before it is sent.
    private Task<ProtectionRun> RunBlockSequenceAsync(BlockReason reason, Guid container, CardPlace place, CancellationToken ct) =>
        RunProtectionAsync(ProtectionGoal.Block, Settings.ProtectAudioQuality, place, notice: true, ct, new BlockCheck(reason, container));

    // Steps ProtectionPolicy for goal until it is done, running each action and refreshing the facts after it.
    // Every gate change is waited for (see the class comment).
    //
    // A Block goal: nothing in it is cancelled, since the block is the at-rest action. Once ct is cancelled, the
    // session is ending or Earshot starts closing, it starts no new protection step and blocks straight away. Any other goal ends with
    // OperationCanceledException at the first step boundary after ct is cancelled.
    private async Task<ProtectionRun> RunProtectionAsync(ProtectionGoal goal, bool protect, CardPlace place, bool notice, CancellationToken ct, BlockCheck? check = null)
    {
        bool blockGoal = goal == ProtectionGoal.Block;
        CancellationToken readCt = blockGoal ? CancellationToken.None : ct;

        var run = new ProtectionRun { Status = blockGoal && _closing ? FreshStatus : await ReadBlockStatusAsync(readCt) };
        bool? kept = blockGoal ? null : await ReadPendingProtectAsync(ct);
        bool intentPending = _pendingProtect is not null || kept is not null;
        bool settingIsTheRequest = false;
        ProtectionProgress progress = default;

        // With the setting off, services that still read protected are a restore not applied yet: one kept only in
        // memory while the nodes were unreadable, or one that did not take, is not lost across a restart. The
        // controller only turns back on what Earshot turned off.
        if (!intentPending && !protect && !_settingRestoreDeclined && goal is ProtectionGoal.Allow or ProtectionGoal.Reverify)
        {
            run.Services = await ReadServicesAsync(ct);
            if (run.Services is not null)
            {
                progress = progress.After(ProtectionAction.ReadServices);
            }

            settingIsTheRequest = run.Services?.State is AudioProtectionState.Protected or AudioProtectionState.Partial;
            intentPending = settingIsTheRequest;
        }

        for (int stepCount = 0; ; stepCount++)
        {
            if (stepCount == MaxProtectionSteps)
            {
                _log.Warn("protection " + goal + ": stopped after " + MaxProtectionSteps.ToString(CultureInfo.InvariantCulture) + " steps.");
                break;
            }

            if (!blockGoal)
            {
                ct.ThrowIfCancellationRequested();
            }

            var facts = new ProtectionFacts(CoordinatorRules.PhaseOf(run.Status), run.Services?.State ?? AudioProtectionState.Unknown, protect, intentPending);
            ProtectionAction action = ProtectionPolicy.Next(goal, facts, progress);
            if (blockGoal && (_closing || _sessionEnding || ct.IsCancellationRequested) && action is ProtectionAction.ReadServices or ProtectionAction.ProtectOn)
            {
                string cutShort = _closing ? "Earshot is closing" : _sessionEnding ? "the session is ending" : "the block was cut short";
                _log.Info("protection Block: " + cutShort + ", so the nodes are blocked without changing protection first.");
                action = ProtectionAction.BlockNodes;
            }
            else if (blockGoal && action == ProtectionAction.ProtectOn && _lastProtectUnsettled)
            {
                _log.Info("protection Block: the last protect verb did not end while it was waited for, so the nodes are blocked without another.");
                action = ProtectionAction.BlockNodes;
            }

            if (action == ProtectionAction.Done)
            {
                break;
            }

            if (action == ProtectionAction.BlockNodes && check is { } why)
            {
                string? skip = await BlockSkipReasonAsync(why, run.Status, refreshSnapshot: progress.ServiceChangeTried);
                if (skip is not null)
                {
                    run.Skipped = skip;
                    _log.Info("protection Block (" + why.Reason + "): no block sent, because " + skip + ".");
                    break;
                }
            }

            _log.Write(LogLevel.Debug, "protection " + goal + ": " + action + " (nodes " + facts.Nodes + ", services " + facts.Services +
                ", protect " + protect + ", pending " + intentPending + ").");
            switch (action)
            {
                case ProtectionAction.ReadServices:
                    run.Services = await ReadServicesAsync(readCt);
                    break;

                case ProtectionAction.ProtectOn:
                case ProtectionAction.ProtectOff:
                case ProtectionAction.StoreIntent:
                {
                    bool on = action switch
                    {
                        ProtectionAction.ProtectOn => true,
                        ProtectionAction.ProtectOff => false,
                        _ => protect,
                    };
                    ControllerResult change = await SendProtectionAsync(on);
                    Record(run.Steps, (on ? GateVerbs.ProtectOn : GateVerbs.ProtectOff) + " (" + goal + ")", change);
                    run.ServiceChange = change;
                    if (settingIsTheRequest && !on && change.Status == OpStatus.NotAttempted)
                    {
                        _settingRestoreDeclined = true;
                        _log.Info("protection " + goal + ": the restore was not taken (" + change.UserMessage + "), so it is not asked for again in this run.");
                    }

                    await NoteServiceChangeAsync(run, change, on, protect, action == ProtectionAction.StoreIntent, place, notice);
                    break;
                }

                case ProtectionAction.KeepIntent:
                    _pendingProtect = protect;
                    run.IntentKept = true;
                    _log.Info("protection: the node state is not known, so " + (protect ? "protect" : "restore") + " is kept until the AirPods next connect.");
                    break;

                case ProtectionAction.BlockNodes:
                {
                    if (_closing && !BlockingBeforeClosing)
                    {
                        BlockingBeforeClosing = true;
                        RaiseChanged();
                    }

                    ControllerResult block = await _block.BlockAsync(CancellationToken.None);
                    Record(run.Steps, "block (" + goal + (check is { } reason ? ", " + reason.Reason : "") + ")", block);
                    run.NodeChange = block;
                    if (CoordinatorRules.MayStillRun(block))
                    {
                        _log.Warn("block: the gate run did not end while it was waited for, so it may still change the nodes.");
                    }

                    break;
                }

                case ProtectionAction.AllowNodes:
                    run.NodeChange = await SendAllowAsync("allow (" + goal + ")", run.Steps);
                    break;
            }

            progress = progress.After(action);
            if (ProtectionPolicy.IsDeviceChange(action))
            {
                // A node change makes the old node read stale. A service change only removes or adds the service's
                // own nodes, so a failed read after one keeps the node state that was known.
                BootBlockStatus? after = await ReadBlockStatusAsync(readCt);
                bool nodeChange = action is ProtectionAction.BlockNodes or ProtectionAction.AllowNodes;
                run.Status = after ?? (nodeChange ? null : run.Status);
                if (action == ProtectionAction.BlockNodes && run.NodeChange is { IsSuccess: false } partly && after?.State == BlockState.Blocked)
                {
                    // The node read is the ground truth: a result that also counts non-present nodes (a phantom node
                    // of a service protection removed) does not make a block that holds a failure.
                    string notPresent = string.Join(", ", after.Nodes.Where(n => !n.IsPresent).Select(n => n.InstanceId));
                    _log.Info("block: the result was " + partly.Status + " (\"" + partly.UserMessage + "\"), but the nodes read Blocked, so it took" +
                        (notPresent.Length > 0 ? "; not present: " + notPresent + "." : "."));
                }
            }
        }

        if (goal == ProtectionGoal.Allow && CoordinatorRules.PhaseOf(run.Status) == NodePhase.Allowed && !run.ServiceChangeFailed)
        {
            _pendingProtect = null;
        }

        return run;
    }

    // Sends the allow and waits for its result, never cancelled (see the class comment). An allow whose end was not
    // seen may still enable the nodes, so the state is read again until UnsettledGateWindow has passed.
    private async Task<ControllerResult> SendAllowAsync(string action, List<StepOutcome> steps)
    {
        ControllerResult allow;
        _allowsRunning++;
        try
        {
            allow = await _block.AllowAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _allowSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
            _log.Warn(action + ": failed, and the gate may still have been started, so it is taken as possibly still running until " + Utc(_allowSettlesAt) + ".", ex);
            throw;
        }
        finally
        {
            _allowsRunning--;
        }

        Record(steps, action, allow);
        if (CoordinatorRules.MayStillRun(allow))
        {
            _allowSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
            _log.Warn(action + ": the gate run did not end while it was waited for, so it may still enable the nodes until " + Utc(_allowSettlesAt) + ".");
        }

        return allow;
    }

    // Sends a protect verb and waits for its result, never cancelled (see the class comment). While it runs, and
    // until UnsettledGateWindow has passed after one whose end was not seen, no block is sent.
    private async Task<ControllerResult> SendProtectionAsync(bool on)
    {
        string verb = on ? GateVerbs.ProtectOn : GateVerbs.ProtectOff;
        _protectVerbsRunning++;
        try
        {
            // The block started for a session end runs outside the operations; a protect verb never runs beside it.
            if (_sessionBlock is { IsCompleted: false } sessionBlock)
            {
                _log.Info(verb + ": waiting for the session-end block to finish first.");
                await sessionBlock;
            }

            ControllerResult change = await _protection.ApplyAsync(on, CancellationToken.None);
            _lastProtectUnsettled = CoordinatorRules.MayStillRun(change);
            if (_lastProtectUnsettled)
            {
                _protectSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
                _log.Warn(verb + ": the gate run did not end while it was waited for, so no block is sent until " + Utc(_protectSettlesAt) + ".");
            }

            return change;
        }
        catch (Exception ex)
        {
            _lastProtectUnsettled = true;
            _protectSettlesAt = _time.GetUtcNow() + UnsettledGateWindow;
            _log.Warn(verb + ": failed, and the gate may still have been started, so no block is sent until " + Utc(_protectSettlesAt) + ".", ex);
            throw;
        }
        finally
        {
            _protectVerbsRunning--;
        }
    }

    // Why a block that is about to be sent no longer should be, or null when it should. Automatic blocks (idle,
    // start-up, closing) need good reads: Block at boot on, the nodes enabled, and render not ACTIVE. A connect
    // clean-up undoes its own allow unless Block at boot is now off or the AirPods are in use after all. A
    // disconnect blocks unless Block at boot is now off: the block is what drops a link the request did not. None
    // is sent while a protect verb this tray started may still run, or while a set-device it sent may still move the
    // pin.
    //
    // An automatic block acts only on reads taken just before it is sent: the nodes and Block at boot are read
    // again, then the device, and nothing is awaited between the last read and the block. The reads the sequence
    // took when it started may be older than a protect verb that ran since, and one that failed after it would
    // otherwise leave the read from before it in hand.
    private async Task<string?> BlockSkipReasonAsync(BlockCheck check, BootBlockStatus? status, bool refreshSnapshot)
    {
        bool automatic = check.Reason is BlockReason.Idle or BlockReason.StartUp or BlockReason.Closing;
        if (automatic)
        {
            status = await ReadBlockStatusAsync(CancellationToken.None);
        }

        // Putting back this coordinator's own allow is held back only by a setting read as off: one that could not
        // be read is no reason to leave the nodes it enabled (the clean-up has already decided from the reads it has).
        bool undoesOwnAllow = check.Reason is BlockReason.ConnectCleanUp or BlockReason.DeviceChangeCleanUp;
        if (status is { BlockAtBoot: false } && (status.BlockAtBootKnown || !undoesOwnAllow))
        {
            return status.BlockAtBootKnown ? "Block at boot is now off" : BlockAtBootOffReason(status);
        }

        if (ProtectMayRun)
        {
            return ProtectMayRunReason;
        }

        // The gate acts on whichever device it pins when the block runs.
        if (PinMayMove)
        {
            return PinMayMoveReason;
        }

        if (automatic && status is null)
        {
            return "the node state is not known";
        }

        if (automatic && !CoordinatorRules.NodesEnabled(status))
        {
            return "the nodes are not known to be enabled";
        }

        if (check.Reason == BlockReason.Disconnect)
        {
            return null;
        }

        // The device is read again when protection was changed first, which takes time and churns the endpoints,
        // and before every automatic block, which acts only on what a read shows now: a notification can be late,
        // and none comes at all when watching for changes failed.
        RenderState render;
        if (refreshSnapshot || automatic)
        {
            DeviceSnapshot? now = await RefreshSnapshotAsync();
            render = now is null ? RenderState.Unknown : CoordinatorRules.RenderOf(now, check.Container);
        }
        else
        {
            render = SnapshotRender(check.Container);
        }

        if (render == RenderState.Active)
        {
            return InUseReason;
        }

        return automatic && render == RenderState.Unknown ? "the device state is not known" : null;
    }

    private async Task NoteServiceChangeAsync(ProtectionRun run, ControllerResult change, bool on, bool protect, bool storeIntent, CardPlace place, bool notice)
    {
        if (storeIntent && change.Status == OpStatus.NotAttempted)
        {
            // The gate kept the request because a node is disabled; the next allow applies it.
            _pendingProtect = protect;
            run.IntentKept = true;
            return;
        }

        bool took;
        if (on)
        {
            took = CoordinatorRules.ProtectedByResult(change);
            if (!took && change.Status == OpStatus.Partial)
            {
                run.Services = await ReadServicesAsync(CancellationToken.None);
                took = run.Services?.State == AudioProtectionState.Protected;
            }
        }
        else
        {
            took = change.Status is OpStatus.Success or OpStatus.AlreadyInState or OpStatus.Partial;
        }

        if (took)
        {
            if (on == protect)
            {
                _pendingProtect = null;
            }

            if (on && notice)
            {
                ShowMicrophoneNoticeOnce(place);
            }

            return;
        }

        if (change.Status is OpStatus.Failed or OpStatus.Partial)
        {
            run.ServiceChangeFailed = true;
            _pendingProtect = protect;
        }
    }

    // The one-time caveat, after the first protect-on that leaves the services protected. It is remembered only
    // once it was on screen.
    private void ShowMicrophoneNoticeOnce(CardPlace place)
    {
        if (Settings.ProtectAudioNoticeShown || _noticeShowing || _closing)
        {
            return;
        }

        _noticeShowing = true;
        _ = ShowMicrophoneNoticeAsync(place);
    }

    // A notice that was due but not seen (Windows held back a card nobody clicked for) goes with the next card
    // that follows a click, once a read shows the services protected with the setting on.
    private void ShowNoticeIfOwed(CardPlace place)
    {
        if (place.Anchor == CardAnchor.NearCursor &&
            Settings.ProtectAudioQuality &&
            _protectionStatus?.State == AudioProtectionState.Protected)
        {
            ShowMicrophoneNoticeOnce(place);
        }
    }

    private async Task ShowMicrophoneNoticeAsync(CardPlace place)
    {
        bool shown;
        try
        {
            shown = await place.ShowAsync(_cards, TrayStatus.DeviceName(_snapshot, Settings), TrayStatus.MicrophoneNotice);
        }
        catch (Exception ex)
        {
            _log.Error("The microphone notice could not be shown.", ex);
            shown = false;
        }
        finally
        {
            _noticeShowing = false;
        }

        if (!shown)
        {
            _log.Info("The microphone notice was not shown now, so it follows the next click that leaves audio quality protected.");
            return;
        }

        try
        {
            _settings.Update(s => s.ProtectAudioNoticeShown = true);
            _log.Info("The microphone notice was shown.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error("The microphone notice was shown, but that could not be saved, so it may be shown again.", ex);
        }
    }

    private async Task<bool> ProtectionIsOnAsync(CancellationToken ct) =>
        (await ReadServicesAsync(ct))?.State == AudioProtectionState.Protected;

    // True only when a good read shows render not ACTIVE for container: the latest snapshot when it is one and no
    // refresh has failed since, otherwise a fresh one.
    private async Task<bool> RenderNotActiveAsync(Guid container)
    {
        DeviceSnapshot? now = _snapshot.ReadStatus == SnapshotReadStatus.Ok && !_deviceReadFailed ? _snapshot : await RefreshSnapshotAsync();
        return now is not null && CoordinatorRules.RenderOf(now, container) == RenderState.NotActive;
    }

    // The render side of container in the snapshot in hand, or Unknown when a refresh has failed since it arrived.
    private RenderState SnapshotRender(Guid container) =>
        _deviceReadFailed ? RenderState.Unknown : CoordinatorRules.RenderOf(_snapshot, container);

    // Reads the protection status now, within StatusReadBudget, and keeps it. Null when the read failed or did not
    // come back in time (logged either way).
    private async Task<AudioProtectionSnapshot?> ReadServicesAsync(CancellationToken ct)
    {
        long read = ++_serviceReadsStarted;
        using var budget = new CancellationTokenSource(StatusReadBudget, _time);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            AudioProtectionSnapshot services = await _protection.GetStatusAsync(within.Token).WaitAsync(within.Token);
            if (read > _serviceReadApplied)
            {
                _serviceReadApplied = read;
                _protectionStatus = services;
                RaiseChanged();
            }

            return services;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogReadTimeout("protection-status", "The audio protection status");
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogReadFailure("protection-status", "The audio protection status could not be read.", ex);
            return null;
        }
    }

    // The kept protection request, within StatusReadBudget. Null when there is none, or the read failed or did not
    // come back in time (logged either way).
    private async Task<bool?> ReadPendingProtectAsync(CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(StatusReadBudget, _time);
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            return await _protection.GetPendingProtectAsync(within.Token).WaitAsync(within.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogReadTimeout("protection-intent", "The kept protection request");
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogReadFailure("protection-intent", "The kept protection request could not be read.", ex);
            return null;
        }
    }

    // Refreshes the device monitor within RefreshBudget. Null when the refresh failed or did not come back in time
    // (logged either way), which callers take as a device state that is not known.
    private async Task<DeviceSnapshot?> RefreshSnapshotAsync()
    {
        using var budget = new CancellationTokenSource(RefreshBudget, _time);
        try
        {
            // The token takes a refresh that is still queued off the worker; WaitAsync ends the wait for one that
            // is not, whatever the monitor does with the token.
            DeviceSnapshot snapshot = await _monitor.RefreshAsync(budget.Token).WaitAsync(budget.Token);
            Observe(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _deviceReadFailed = true;
            _log.Warn("The device state was not refreshed within " + Seconds(RefreshBudget) +
                ", so it is not known. The audio worker may still be inside a driver call.");
            return null;
        }
        catch (Exception ex)
        {
            _deviceReadFailed = true;
            LogReadFailure("device-refresh", "The device state could not be refreshed.", ex);
            return null;
        }
    }

    // Waits for a snapshot in which ready holds for container: armed first, then refreshed, so a state reached
    // before the wait began is seen. The refresh is not waited for on its own, so the timeout holds even when
    // the audio worker is inside a driver call, and a notification that arrives first ends the wait. False at the
    // timeout; OperationCanceledException when ct is cancelled.
    private async Task<bool> WaitForSnapshotAsync(Guid container, Func<DeviceSnapshot, Guid, bool> ready, TimeSpan timeout, CancellationToken ct)
    {
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSnapshot(object? sender, DeviceSnapshotEventArgs e)
        {
            if (ready(e.Snapshot, container))
            {
                seen.TrySetResult(true);
            }
        }

        _monitor.SnapshotChanged += OnSnapshot;
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout, _time);
            using var refreshCancel = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
            using CancellationTokenRegistration onTimeout = timeoutSource.Token.Register(() => seen.TrySetResult(false));
            using CancellationTokenRegistration onCancel = ct.Register(() => seen.TrySetCanceled(ct));
            _ = RefreshForWaitAsync(container, ready, seen, refreshCancel.Token);
            return await seen.Task;
        }
        finally
        {
            _monitor.SnapshotChanged -= OnSnapshot;
        }
    }

    // The refresh that starts a wait for a snapshot. Never throws: a refresh that fails is logged and the wait goes
    // on for a notification, and one still queued or running when the wait ends is left, since the wait has
    // already reported how it ended.
    private async Task RefreshForWaitAsync(Guid container, Func<DeviceSnapshot, Guid, bool> ready, TaskCompletionSource<bool> seen, CancellationToken ct)
    {
        try
        {
            DeviceSnapshot now = await _monitor.RefreshAsync(ct);
            Observe(now);
            if (ready(now, container))
            {
                seen.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The wait ended (timed out or cancelled) while the refresh was queued; the caller reports how.
            _log.Write(LogLevel.Debug, "The refresh for a device wait had not run when the wait ended.");
        }
        catch (Exception ex)
        {
            LogReadFailure("device-refresh", "The device state could not be refreshed while waiting for the AirPods.", ex);
        }
    }

    private void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Observe(e.Snapshot);
        EvaluateIdle();
        TryStartUpCheck();
        _ = RefreshStatusAsync();
    }

    private void OnSettingsChanged(object? sender, EarshotSettings e)
    {
        if (!_disposed)
        {
            EvaluateIdle();
        }
    }

    // Keeps the newest snapshot. A snapshot from an older enumeration never replaces a newer one, and a failed
    // refresh is only put behind by a snapshot that is kept.
    private void Observe(DeviceSnapshot snapshot)
    {
        if (snapshot.Sequence != 0 && _snapshot.Sequence > snapshot.Sequence)
        {
            return;
        }

        _snapshot = snapshot;
        _deviceReadFailed = false;
        if (CoordinatorRules.RenderOf(snapshot, WatchedContainer()) == RenderState.Active)
        {
            ReArmIdleRule("the AirPods are in use");
        }
    }

    private Guid WatchedContainer() => CoordinatorRules.WatchedContainer(_blockStatus, Settings, _snapshot);

    // Whether the idle block is due now and, when it is not, why, and whether a later read could make it due with
    // no notification to say so.
    private (string Why, IdleVerdict Verdict) IdleCheck()
    {
        if (_disposed || _closing)
        {
            return ("Earshot is closing", IdleVerdict.Held);
        }

        if (IsBusy)
        {
            return ("an operation is in flight", IdleVerdict.Held);
        }

        // Only the latest node read, and only when it worked, can rule out enabled nodes: Block at boot read off, the
        // boot block not set up, or the nodes blocked with no allow of this tray's still on its way. Anything else
        // (a read that failed or has not run, nodes Unknown or NotFound, a setting that did not read) leaves them
        // possibly enabled, whatever an earlier read showed, since no notification may follow (the AirPods are on
        // the phone, or the nodes were enabled outside Earshot). Such a state is read again on the timer, the wait
        // doubling up to IdleRetryLimit, so the idle rule never stops with nothing scheduled.
        if (ProtectMayRun)
        {
            return (ProtectMayRunReason, IdleVerdict.Unsettled);
        }

        if (PinMayMove)
        {
            return (PinMayMoveReason, IdleVerdict.Unsettled);
        }

        if (!_blockStatusFresh || _blockStatus is null)
        {
            return ("the boot block status is not known", IdleVerdict.Unsettled);
        }

        // Nothing can block without the gate tasks, so before setup reading again changes nothing; the read after setup
        // finishes evaluates the rule afresh. Once a read this run has shown them set up, one that says they are not
        // is read again: the nodes may still be enabled, and a later read may show the tasks again.
        if (_blockStatus.State == BlockState.NotSetUp)
        {
            return _tasksSeenInstalled
                ? ("the boot block read as not set up after it read as set up", IdleVerdict.Unsettled)
                : ("the boot block is not set up", IdleVerdict.Settled);
        }

        // Read again while the nodes may be enabled: the setting may read later (a file that was in use), or be
        // written again from the menu.
        if (!_blockStatus.BlockAtBootKnown && _blockStatus.State != BlockState.Blocked)
        {
            return (BlockAtBootOffReason(_blockStatus), IdleVerdict.Unsettled);
        }

        // A Block at boot change this tray sent whose end was not seen may still turn the setting on, with no
        // notification to say so.
        if (!_blockStatus.BlockAtBoot)
        {
            return BootSettingMayChange && _blockStatus.State != BlockState.Blocked
                ? (BlockAtBootOffReason(_blockStatus) + ", but a change to it this tray sent may still land", IdleVerdict.Unsettled)
                : (BlockAtBootOffReason(_blockStatus), IdleVerdict.Settled);
        }

        if (_blockStatus.State == BlockState.Blocked)
        {
            return ("the nodes are blocked", AllowMayLand ? IdleVerdict.Unsettled : IdleVerdict.Settled);
        }

        if (!CoordinatorRules.NodesEnabled(_blockStatus))
        {
            return ("the nodes read " + _blockStatus.State, IdleVerdict.Unsettled);
        }

        // In use settles nothing while changes are not watched: no notification would say when that ends. After a
        // refresh that failed or did not come back in time, the snapshot in hand may be out of date, so the state is
        // read again later, less often each time, rather than an idle wait started on it again and again.
        return SnapshotRender(WatchedContainer()) switch
        {
            RenderState.Unknown => ("the device state is not known", IdleVerdict.Unsettled),
            RenderState.Active when _monitor.WatchFailed => (InUseReason + " and changes are not watched", IdleVerdict.Unsettled),
            RenderState.Active => (InUseReason, IdleVerdict.Settled),
            _ => ("", IdleVerdict.Due),
        };
    }

    private void EvaluateIdle()
    {
        NoteUnwatched();
        (string why, IdleVerdict verdict) = IdleCheck();
        if (verdict != IdleVerdict.Due)
        {
            CancelIdleWait(why);
            switch (verdict)
            {
                case IdleVerdict.Unsettled:
                    ScheduleRecheck(why);
                    break;
                case IdleVerdict.Settled:
                    StopRechecks(settled: true);
                    break;
                default:
                    // Busy or closing: whatever holds it evaluates again when it ends.
                    StopRechecks(settled: false);
                    break;
            }

            return;
        }

        StopRechecks(settled: true);
        if (_idleWait is not null)
        {
            return;
        }

        TimeSpan delay = IdleDelay;
        _idleWait = new CancellationTokenSource();
        _log.Info("The nodes are enabled and the AirPods are not in use. They are blocked in " + Seconds(delay) + " unless that changes" +
            (_idleFailures > 0 ? " (" + _idleFailures.ToString(CultureInfo.InvariantCulture) + " automatic block(s) in a row did not take)." : "."));
        _ = IdleWaitAsync(_idleWait, delay);
    }

    private void CancelIdleWait(string why)
    {
        CancellationTokenSource? wait = _idleWait;
        if (wait is null)
        {
            return;
        }

        _idleWait = null;
        _log.Write(LogLevel.Debug, "Idle wait stopped: " + why + ".");
        wait.Cancel();
    }

    // Watching for endpoint changes failed, which the idle rule depends on to see the AirPods leave: it reads the
    // device on a timer instead (IdleCheck, RecheckAsync). Logged and shown once, and only where the boot block
    // could act at all: set up, with Block at boot on.
    private void NoteUnwatched()
    {
        if (_unwatchedNoted || !_monitor.WatchFailed || _closing || _disposed ||
            _blockStatus is not { BlockAtBoot: true } status || status.State == BlockState.NotSetUp)
        {
            return;
        }

        _unwatchedNoted = true;
        _log.Warn("Audio device changes are not watched, so the AirPods are read again every " + Seconds(RecheckDelay) + " to " +
            Seconds(UnwatchedRecheckLimit) + " while the nodes may be enabled, and an automatic block may start late.");
        CardPlace.NearTray.Show(_cards, TrayStatus.DeviceName(_snapshot, Settings), ChangesNotWatchedMessage);
    }

    // Reads the state again later, because the last read could not settle whether the nodes need blocking. The
    // wait doubles after each such read in a row, and ends early when a change that may still run is due to end.
    private void ScheduleRecheck(string why)
    {
        if (_recheck is not null)
        {
            return;
        }

        TimeSpan delay = Doubled(RecheckDelay, _rechecksInARow, _monitor.WatchFailed ? UnwatchedRecheckLimit : IdleRetryLimit);
        DateTimeOffset now = _time.GetUtcNow();
        delay = EndsSooner(delay, _allowSettlesAt - now);
        delay = EndsSooner(delay, _protectSettlesAt - now);
        delay = EndsSooner(delay, _pinSettlesAt - now);
        delay = EndsSooner(delay, _bootSettingSettlesAt - now);

        _recheck = new CancellationTokenSource();
        _log.Write(_rechecksInARow == 0 ? LogLevel.Info : LogLevel.Debug,
            "The state is read again in " + Seconds(delay) + ": " + why + ", and nothing read so far rules out enabled nodes.");
        _ = RecheckAsync(_recheck, delay);
    }

    private void StopRechecks(bool settled)
    {
        if (settled)
        {
            _rechecksInARow = 0;
        }

        CancellationTokenSource? recheck = _recheck;
        if (recheck is null)
        {
            return;
        }

        _recheck = null;
        recheck.Cancel();
    }

    private async Task RecheckAsync(CancellationTokenSource wait, TimeSpan delay)
    {
        try
        {
            try
            {
                await Task.Delay(delay, _time, wait.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                return;
            }

            if (wait.IsCancellationRequested)
            {
                return;
            }

            if (_recheck == wait)
            {
                _recheck = null;
            }

            if (_disposed || _closing)
            {
                return;
            }

            _rechecksInARow++;
            if (_monitor.WatchFailed || SnapshotRender(WatchedContainer()) == RenderState.Unknown)
            {
                await RefreshSnapshotAsync();
            }

            // Evaluates the idle rule again, which reads again later if this read does not settle it either.
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("recheck", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Reading the state again failed. " + TrayReport.DescribeStep(step), ex);
        }
        finally
        {
            wait.Dispose();
        }
    }

    // Something has changed that a retry after a failed automatic block should not wait out: the wait starts
    // again from IdleGrace, and a failure after it is shown again.
    private void ReArmIdleRule(string why)
    {
        if (_idleFailures == 0 && !_idleFailureShown && _statusAtIdleFailure is null)
        {
            return;
        }

        _idleFailures = 0;
        _idleFailureShown = false;
        _statusAtIdleFailure = null;
        _log.Info("Idle rule re-armed: " + why + ".");
        if (_idleWait is not null)
        {
            CancelIdleWait("the idle rule was re-armed");
            EvaluateIdle();
        }
    }

    private async Task IdleWaitAsync(CancellationTokenSource wait, TimeSpan delay)
    {
        try
        {
            try
            {
                await Task.Delay(delay, _time, wait.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                return;
            }

            // The wait can end just as it is stopped; a stopped wait never blocks, even if a new one has started.
            if (wait.IsCancellationRequested)
            {
                return;
            }

            if (_idleWait == wait)
            {
                _idleWait = null;
            }

            (string why, IdleVerdict verdict) = IdleCheck();
            if (verdict != IdleVerdict.Due)
            {
                _log.Write(LogLevel.Debug, "Idle block not issued: " + why + ".");
                EvaluateIdle();
                return;
            }

            _log.Info("Blocking the nodes: the AirPods were not in use for " + Seconds(delay) + " with nothing in flight (" + Utc(_time.GetUtcNow()) + ").");
            // CancellationToken.None cannot wait for ever: RunExclusiveAsync only waits for the operation in flight
            // and the session-end block, which end by limits of their own, never for a session end to be cancelled.
            // Not refused at a session end: a block only makes the at-rest state safer.
            await RunExclusiveAsync("idle block", token => AutomaticBlockAsync("idle block", BlockReason.Idle, WatchedContainer(), token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            NoteAutomaticBlock("idle block", ProtectionRun.Faulted("idle-block", ex, _log));
        }
        finally
        {
            wait.Dispose();
        }
    }

    // Runs one automatic block (idle, start-up) and notes how it went. Never throws: a failure is recorded as a
    // step with its code, logged and shown.
    private async Task<bool> AutomaticBlockAsync(string name, BlockReason reason, Guid container, CancellationToken ct)
    {
        ProtectionRun run;
        try
        {
            run = await RunBlockSequenceAsync(reason, container, CardPlace.NearTray, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info(name + ": stopped before the block, because " + (_closing ? "Earshot is closing" : "the session is ending") + ".");
            return false;
        }
        catch (Exception ex)
        {
            run = ProtectionRun.Faulted(name.Replace(' ', '-'), ex, _log);
        }

        NoteAutomaticBlock(name, run);
        return run.BlockTook;
    }

    // An automatic block that did not take is shown once and tried again after a longer wait (IdleDelay), so a
    // gate that keeps failing is not run every IdleGrace and the nodes are never left enabled for good.
    private void NoteAutomaticBlock(string name, ProtectionRun run)
    {
        if (run.Skipped is not null)
        {
            return;
        }

        if (run.BlockTook)
        {
            ReArmIdleRule(name + " took");
            return;
        }

        _idleFailures++;
        _statusAtIdleFailure = FreshStatus;
        string message = run.NodeChange?.UserMessage ?? CouldNotBlockMessage;
        _log.Warn(name + ": the nodes are still enabled (" + message + "). The idle rule tries again after " + Seconds(IdleDelay) +
            " if they are still enabled and not in use.");
        if (_idleFailureShown || _closing)
        {
            return;
        }

        _idleFailureShown = true;
        _ = ShowAutomaticFailureAsync(message);
    }

    private async Task ShowAutomaticFailureAsync(string message)
    {
        bool shown;
        try
        {
            shown = await CardPlace.NearTray.ShowAsync(_cards, TrayStatus.DeviceName(_snapshot, Settings), message);
        }
        catch (Exception ex)
        {
            _log.Error("The card for an automatic block that did not take could not be shown.", ex);
            shown = false;
        }

        if (!shown)
        {
            // Held back (Windows is not taking notifications): the next failure tries the card again.
            _idleFailureShown = false;
        }
    }

    private void TryStartUpCheck()
    {
        if (!_started || _startChecked || _closing || _disposed)
        {
            return;
        }

        if (_snapshot.ReadStatus != SnapshotReadStatus.Ok || !_blockStatusFresh || _blockStatus is null)
        {
            return;
        }

        _startChecked = true;
        _ = StartUpCheckAsync();
    }

    private async Task StartUpCheckAsync()
    {
        try
        {
            // CancellationToken.None cannot wait for ever: the only waits in RunExclusiveAsync are for the operation
            // in flight and the session-end block, each of which ends by limits of its own, and nothing there waits
            // for a session end to be cancelled. Not refused at a session end: all it can send is a block, and its
            // protection check is skipped then (StartUpCheckCoreAsync).
            await RunExclusiveAsync("start-up check", StartUpCheckCoreAsync, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("start-up-check", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("The start-up check failed. " + TrayReport.DescribeStep(step), ex);
            if (!_closing)
            {
                CardPlace.NearTray.Show(_cards, TrayStatus.AppName, SomethingWentWrongMessage);
            }
        }
    }

    private async Task<bool> StartUpCheckCoreAsync(CancellationToken ct)
    {
        BootBlockStatus? status = FreshStatus;
        Guid container = WatchedContainer();
        RenderState render = CoordinatorRules.RenderOf(_snapshot, container);
        if (status is { BlockAtBoot: true, TasksInstalled: true })
        {
            if (render == RenderState.Active)
            {
                _log.Info("connected at start-up: render ACTIVE in container " + container.ToString("D") + " at " + Utc(_time.GetUtcNow()) +
                    " (snapshot " + Utc(_snapshot.TakenUtc) + "), nodes " + status.State + ", Block at boot on, " +
                    (_options.StartedAtLogon ? "started at sign-in" : "started by hand") + ". The nodes stay enabled while in use.");
                if (_options.StartedAtLogon)
                {
                    CardPlace.NearTray.Show(_cards, TrayStatus.DeviceName(_snapshot, Settings), ConnectedAtStartUpMessage);
                }
            }
            else if (render == RenderState.NotActive && CoordinatorRules.NodesEnabled(status))
            {
                _log.Info("Start-up check: the nodes are " + status.State + " and the AirPods are not in use (" + Utc(_time.GetUtcNow()) + "). Blocking.");
                await AutomaticBlockAsync("start-up block", BlockReason.StartUp, container, ct);
            }
        }

        if (_sessionEnding)
        {
            // Reverify never allows the nodes, but it may start a protect verb, which is a device change like any
            // other; the check after the next connect or start-up applies it instead.
            _log.Info("Start-up check: the session is ending, so protection is not checked now.");
            return true;
        }

        ProtectionRun check;
        try
        {
            check = await RunProtectionAsync(ProtectionGoal.Reverify, Settings.ProtectAudioQuality, CardPlace.NearTray, notice: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info("Start-up check: the protection check was stopped.");
            return true;
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("start-up-protection-check", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Start-up check: the protection check failed. " + TrayReport.DescribeStep(step), ex);
            if (!_closing)
            {
                CardPlace.NearTray.Show(_cards, TrayStatus.DeviceName(_snapshot, Settings), ProtectionCheckFailedMessage);
            }

            return true;
        }

        if (check.ServiceChangeFailed && check.ServiceChange is { } failed && !_closing)
        {
            CardPlace.NearTray.Show(_cards, TrayStatus.DeviceName(_snapshot, Settings), failed.UserMessage);
        }

        return true;
    }

    // Queued by BeginShutdown. Runs after the operation in flight, its clean-up included, and reads the state again.
    private async Task BlockBeforeClosingAsync()
    {
        try
        {
            // CancellationToken.None cannot wait for ever: RunExclusiveAsync only waits for the operation in flight
            // (cancelled by MarkClosing, with limits of its own) and the session-end block, never for a session end
            // to be cancelled, and Exit bounds the whole wait besides (TrayContext.ExitAsync). Not refused at a
            // session end: it is a block.
            await RunExclusiveAsync(ClosingBlockName, BlockBeforeClosingCoreAsync, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("closing-block", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Closing: the block before closing failed; the nodes may still be enabled. " + TrayReport.DescribeStep(step), ex);
            ClosingNotice = CouldNotBlockMessage;
        }
    }

    private async Task<bool> BlockBeforeClosingCoreAsync(CancellationToken ct)
    {
        BootBlockStatus? status = await ReadBlockStatusAsync(CancellationToken.None);
        if (status is null)
        {
            _log.Warn("Closing: the nodes are not blocked, because the boot block status could not be read.");
            return false;
        }

        if (!status.BlockAtBoot || !CoordinatorRules.NodesEnabled(status))
        {
            if (status.BlockAtBoot && AllowMayLand)
            {
                _log.Warn("Closing: the nodes read " + status.State + ", but an allow this tray sent may still enable them until " + Utc(_allowSettlesAt) +
                    ", and nothing blocks them again until Earshot runs again or the BootBlock task runs.");
                ClosingNotice = ClosedBeforeChangeEndedMessage;
            }
            else if (!status.BlockAtBoot && BootSettingMayChange && CoordinatorRules.NodesEnabled(status))
            {
                _log.Warn("Closing: Block at boot reads " + (status.BlockAtBootKnown ? "off" : "not known") + ", but the change to it this tray sent may still turn it on until " +
                    Utc(_bootSettingSettlesAt) + ", and the nodes read " + status.State + ". Nothing blocks them again until Earshot runs again or the BootBlock task runs.");
                ClosingNotice = ClosedBeforeChangeEndedMessage;
            }

            _log.Info("Closing: nothing to block (" + Describe(status) + ").");
            return false;
        }

        Guid container = WatchedContainer();
        DeviceSnapshot? now = await RefreshSnapshotAsync();
        RenderState render = now is null ? RenderState.Unknown : CoordinatorRules.RenderOf(now, container);
        if (render == RenderState.Active)
        {
            _log.Warn("Closing while the AirPods are in use (render ACTIVE in container " + container.ToString("D") + " at " + Utc(_time.GetUtcNow()) +
                "): the nodes stay enabled, and nothing blocks them again until Earshot runs again.");
            ClosingNotice = ClosedWhileInUseMessage;
            return false;
        }

        if (render == RenderState.Unknown)
        {
            _log.Warn("Closing: the nodes are not blocked, because the device state could not be read.");
            return false;
        }

        _log.Info("Closing: the nodes are enabled and the AirPods are not in use. Blocking before Earshot closes (" + Utc(_time.GetUtcNow()) + ").");
        ProtectionRun run = await RunBlockSequenceAsync(BlockReason.Closing, container, CardPlace.NearTray, ct);
        if (run.Skipped is { } skipped)
        {
            if (skipped is ProtectMayRunReason or PinMayMoveReason)
            {
                _log.Warn("Closing: the nodes are not blocked, because " + skipped + "; the BootBlock task blocks them at the next start.");
                ClosingNotice = ClosedBeforeChangeEndedMessage;
            }
            else if (skipped == InUseReason)
            {
                ClosingNotice = ClosedWhileInUseMessage;
            }
        }
        else if (!run.BlockTook)
        {
            ClosingNotice = run.NodeChange?.UserMessage ?? CouldNotBlockMessage;
        }

        return run.BlockTook;
    }

    // A connect or disconnect that was cancelled, with the reason when the coordinator cancelled it itself: the
    // caller's own token is not cancelled then, so the caller cannot tell why.
    private ToggleReport CancelledReport(bool connect, OpStatus status, string message, List<StepOutcome> steps) =>
        new(connect, status, message, steps, Cancelled: true)
        {
            CancelledBecause = _sessionEnding ? "the session is ending" : _closing ? "Earshot is closing" : null,
        };

    private ToggleReport Finish(ToggleRequest request, OpStatus status, string message, List<StepOutcome> steps, bool showCard = true)
    {
        if (showCard)
        {
            ShowCard(request, message);
        }

        return new ToggleReport(request.Connect, status, message, steps, Cancelled: false);
    }

    private void ShowCard(ToggleRequest request, string status)
    {
        if (!_closing)
        {
            request.Place.Show(_cards, request.DeviceName, status);
        }
    }

    private void Record(List<StepOutcome> steps, string action, ControllerResult result)
    {
        steps.AddRange(result.Steps);
        string text = TrayReport.Describe(action, result.Status, result.UserMessage, result.Steps);
        _log.Write(result.IsSuccess ? LogLevel.Info : LogLevel.Warn, text);
    }

    private void LogReadFailure(string step, string message, Exception ex)
    {
        StepOutcome outcome = StepOutcomes.FromHResult(step, ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
        _log.Error(message + " " + TrayReport.DescribeStep(outcome), ex);
    }

    // A read that did not come back within StatusReadBudget. It may still be running on the thread pool; its result
    // is not used.
    private void LogReadTimeout(string step, string what)
    {
        StepOutcome outcome = StepOutcomes.NotAttempted(step, "No result within " + Seconds(StatusReadBudget) + ".");
        _log.Warn(what + " was not read within " + Seconds(StatusReadBudget) + ", so it is not known. " + TrayReport.DescribeStep(outcome));
    }

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TimeSpan Doubled(TimeSpan start, int times, TimeSpan? limit = null)
    {
        TimeSpan most = limit ?? IdleRetryLimit;
        TimeSpan delay = start;
        for (int i = 0; i < times && delay < most; i++)
        {
            delay += delay;
        }

        return delay < most ? delay : most;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // delay, or untilSettled when a change that may still run is due to end before delay has passed.
    private static TimeSpan EndsSooner(TimeSpan delay, TimeSpan untilSettled) =>
        untilSettled > TimeSpan.Zero && untilSettled < delay ? untilSettled : delay;

    private static string Describe(BootBlockStatus status) =>
        status.State + ", Block at boot " + (!status.BlockAtBootKnown ? "not known" : status.BlockAtBoot ? "on" : "off");

    // Why nothing is blocked for a status whose BlockAtBoot is false.
    private static string BlockAtBootOffReason(BootBlockStatus status) =>
        status.BlockAtBootKnown ? "Block at boot is off" : "Block at boot could not be read";

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private enum IdleVerdict
    {
        Due,        // every condition for the idle block holds
        Settled,    // a good read shows nothing to block: Block at boot off, the nodes blocked, the AirPods in use
        Held,       // Earshot is busy or closing; whatever holds it evaluates again when it ends
        Unsettled,  // a read failed or shows a state nothing can act on, or a change may still run: read again later
    }

    // What the clean-up of a connect that did not reach ACTIVE left, which decides what its card may say.
    private enum ConnectCleanUpOutcome
    {
        NothingChanged,    // nothing was allowed or turned off, so nothing was put back
        InUse,             // render is ACTIVE after all
        Blocked,           // the nodes are blocked again, or read blocked, so the connect cannot still be going
        NodesLeftEnabled,  // Block at boot off, a block that did not take, or a block held back
    }

    private readonly record struct BlockAttempt(bool Issued, bool Took, string Message)
    {
        public static BlockAttempt NotIssued => new(Issued: false, Took: false, "");
    }

    // Why a Block sequence runs and which device its render check watches.
    private readonly record struct BlockCheck(BlockReason Reason, Guid Container);

    // What a device change has done to the device pinned now, to put back if the pin does not move.
    private sealed class DeviceChangeUndo
    {
        public bool AllowIssued { get; set; }

        // Block at boot as read before the allow.
        public bool BlockAtBoot { get; set; }

        public bool ProtectOffIssued { get; set; }

        // The device pinned now, as the status read before the change named it.
        public Guid Container { get; set; }
    }

    // What a connect has changed so far and must put back if it does not reach ACTIVE.
    private sealed class ConnectCleanup
    {
        public bool AllowIssued { get; set; }

        // Block at boot as read before the allow, for a clean-up whose own read fails or cannot read the setting;
        // null when it was not known then either.
        public bool? BlockAtBoot { get; set; }

        public bool ProtectOffIssued { get; set; }

        // The Hands-Free assisted way ran: a failure there blocks again with Block at boot on even without an allow.
        public bool ReblockAfterFallback { get; set; }
    }

    private sealed class ProtectionRun
    {
        public List<StepOutcome> Steps { get; } = new();

        public BootBlockStatus? Status { get; set; }

        // The last good read of the services in this sequence, or null.
        public AudioProtectionSnapshot? Services { get; set; }

        public ControllerResult? NodeChange { get; set; }

        public ControllerResult? ServiceChange { get; set; }

        public bool ServiceChangeFailed { get; set; }

        public bool IntentKept { get; set; }

        // Why the block was not sent after all (Block at boot now off, the AirPods in use, a read that failed, a
        // protect verb that may still run), or null when it was sent or none was needed.
        public string? Skipped { get; set; }

        // The nodes ended blocked: the block succeeded (the controller confirms that from the nodes), a node read
        // after it shows them blocked whatever the result said, or none was needed because they already were.
        public bool BlockTook => Skipped is null &&
                                 (Status?.State == BlockState.Blocked || NodeChange is { IsSuccess: true });

        // A sequence that threw: the exception as a step with its code, logged.
        public static ProtectionRun Faulted(string step, Exception ex, ILog log)
        {
            var run = new ProtectionRun();
            StepOutcome outcome = StepOutcomes.FromHResult(step, ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            run.Steps.Add(outcome);
            log.Error(step + " failed; the nodes may still be enabled. " + TrayReport.DescribeStep(outcome), ex);
            return run;
        }
    }
}
