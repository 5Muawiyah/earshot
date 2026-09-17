using System.Globalization;
using System.Runtime.ExceptionServices;
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
// block waits for the one in flight. Cancelling a connect or disconnect cancels its wait for the device, never a
// request already sent, and its task completes only after its clean-up (a re-block included) has finished.
//
// Connect.
//   1. ConnectAsync. Confirmed: the AirPods are in use, the nodes stay enabled, protection is checked again.
//   2. NodesBlocked: allow first only when the node read says Blocked or Mixed for this device. Show "Allowing",
//      allow, wait for a render endpoint to come back (EndpointWait), connect again.
//   3. Not confirmed, the A2DP filter turned the request down, protection is on and a good read shows render not
//      ACTIVE: turn protection off, connect again, put the saved protection setting back (the Hands-Free
//      assisted way).
//   4. Anything else that did not reach ACTIVE, after an allow, a timeout, a failure or a cancellation: block again
//      when Block at boot is on, then report. A failed click never leaves the nodes enabled.
// Disconnect. KS disconnect, then, with Block at boot on and the nodes enabled, block whatever the request
// returned. With Block at boot off the result is reported as it is; there is no other way.
// Idle. When a good read shows the nodes enabled, Block at boot on and render not ACTIVE, and that holds for
// IdleGrace with nothing in flight, block. Any operation, or render going ACTIVE, restarts the wait. A block that
// does not take is shown once and tried again after a longer wait (up to IdleRetryLimit), so the nodes are never
// left enabled for good and a gate that keeps failing is not run every IdleGrace.
// Every automatic block checks again, just before the block is sent, that Block at boot is still on and the
// AirPods are still not in use, since protection may have been changed first and that takes time.
// Start-up. Once the first good snapshot and the first node read are in: nodes enabled and not in use, block at
// once; in use, log the evidence and leave them enabled for the idle rule. Then check protection.
// Session end. On WM_QUERYENDSESSION, with Block at boot on and the nodes not known to be blocked, start a block
// and return at once. A windowless app can be ended about five seconds in, so this is a backstop only.
// https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession
// Exit. The operation in flight is cancelled and its clean-up still runs; a Block sequence stops changing
// protection and blocks straight away. Then, with Block at boot on, nodes enabled and the AirPods not in use,
// the nodes are blocked before Earshot closes. Closing while they are in use is logged and noted for a card.
//
// Nothing is decided from a read that failed or has not run: an unknown snapshot or node state never leads to a
// block, an allow or a protection change on its own. The one exception is putting back an allow this coordinator
// issued itself, which is undone even when the node read after it fails.
//
// Protection. Every change of services or nodes follows ProtectionPolicy, so a service state is only changed
// while the nodes are enabled. The microphone notice is shown once, after the first protect-on that leaves the
// services protected, whatever started it. It is remembered only once it was on screen: a card nobody clicked for
// can be held back by Windows, and then the notice follows the next click that leaves protection on.
internal sealed class BlockCoordinator : IDisposable
{
    public const string AllowingStatus = "Allowing";
    public const string CouldNotReachDriverMessage = "Could not reach the AirPods audio driver. Try again.";
    public const string TryingAnotherWayMessage = "Could not reach the AirPods audio driver. Trying another way.";
    public const string DidNotComeBackMessage = "The AirPods did not come back in time. Try again.";
    public const string NotAvailableMessage = "The AirPods are not available. Check that Bluetooth is on.";
    public const string BlockStatusUnreadableMessage = "Could not read the boot block status. Try again.";
    public const string CouldNotBlockMessage = "Could not block the AirPods. Try again.";
    public const string OtherDeviceMessage = "Boot block is set for another device. Choose device again.";
    public const string ConnectedNotProtectedMessage = "Connected, but audio quality protection did not apply.";
    public const string ProtectionCheckFailedMessage = "Could not check audio quality protection.";
    public const string ConnectedAtStartUpMessage = "Connected at start-up. The boot block did not hold.";
    public const string ClosedWhileInUseMessage = "Closed while in use, so the AirPods are not blocked.";
    public const string SavedForNextConnectMessage = "Saved. It applies when the AirPods next connect.";
    public const string AlreadyProtectedMessage = "Audio quality is already protected";
    public const string AlreadyOffMessage = "Audio quality protection is already off";
    public const string SomethingWentWrongMessage = "Something went wrong. See the log.";

    // How long render must stay not ACTIVE, with nothing in flight, before the nodes are blocked again. A
    // conservative waiting budget, not a measured figure: long enough that the churn of a protection change or a
    // brief drop does not disconnect someone who is listening. The owner's live test tunes it.
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(30);

    // The longest wait before an automatic block that did not take is tried again. The wait doubles from
    // IdleGrace after each failure in a row, up to this. A waiting budget, not a measured figure.
    public static readonly TimeSpan IdleRetryLimit = TimeSpan.FromMinutes(15);

    // How long to wait after an allow for a render endpoint to come back before connecting. A waiting budget, not
    // a measured figure; nothing documents how long enabling a device node takes to bring its endpoints back.
    public static readonly TimeSpan EndpointWait = TimeSpan.FromSeconds(10);

    // ProtectionPolicy runs each change at most once per sequence, so a sequence ends well within this many steps.
    internal const int MaxProtectionSteps = 16;

    private const string ClosingBlockName = "block before closing";

    private readonly IDeviceMonitor _monitor;
    private readonly IConnectionController _connection;
    private readonly IBlockController _block;
    private readonly IAudioProtectionController _protection;
    private readonly ISettingsStore _settings;
    private readonly ICardPresenter _cards;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly CoordinatorOptions _options;

    // Cancelled when Earshot starts closing: it cuts a Block sequence's protection steps short.
    private readonly CancellationTokenSource _closingSource = new();

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

    private Task? _current;
    private string? _currentName;
    private CancellationTokenSource? _currentCancel;
    private Task? _sessionBlock;
    private bool _sessionBlockIssued;
    private Task? _closingBlock;
    private CancellationTokenSource? _idleWait;

    // Automatic blocks in a row that did not take, whether their card was shown, and the node read after the last
    // one. Cleared when the AirPods are used, the user connects or disconnects, a block takes, or a later good read
    // shows the node state or Block at boot changed.
    private int _idleFailures;
    private bool _idleFailureShown;
    private BootBlockStatus? _statusAtIdleFailure;

    private bool _noticeShowing;
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

    // Raised on the UI thread when an operation starts or ends, or a status read changes what is known.
    public event EventHandler? Changed;

    // True while an operation, a block started at session end or the block before closing is in flight.
    public bool IsBusy => _current is not null || _sessionBlock is { IsCompleted: false } || _closingBlock is { IsCompleted: false };

    // True while something other than the block before closing is in flight, or waiting to run before it.
    public bool IsBusyBeyondClosingBlock =>
        (_current is not null && _currentName != ClosingBlockName) || _sessionBlock is { IsCompleted: false };

    // The last boot block status read that worked, or null before one has. It may be older than a later read
    // that failed; automatic actions only use it while the last read worked.
    public BootBlockStatus? BlockStatus => _blockStatus;

    // The last audio protection read that worked, or null before one has.
    public AudioProtectionSnapshot? ProtectionStatus => _protectionStatus;

    // What the user should know as Earshot closes (the AirPods were in use, or the block before closing did not
    // take), once the block before closing has finished; null when there is nothing to say.
    public string? ClosingNotice { get; private set; }

    // True while an idle wait is running, for tests and the log.
    internal bool IdleWaitRunning => _idleWait is not null;

    // Automatic blocks in a row that did not take, for tests.
    internal int IdleFailures => _idleFailures;

    // The protection request this tray keeps, for tests.
    internal bool? PendingProtect => _pendingProtect;

    private EarshotSettings Settings => _settings.Current;

    private BootBlockStatus? FreshStatus => _blockStatusFresh ? _blockStatus : null;

    // How long the idle rule waits now: IdleGrace, doubled after each automatic block in a row that did not take.
    private TimeSpan IdleDelay
    {
        get
        {
            TimeSpan delay = IdleGrace;
            for (int i = 0; i < _idleFailures && delay < IdleRetryLimit; i++)
            {
                delay += delay;
            }

            return delay < IdleRetryLimit ? delay : IdleRetryLimit;
        }
    }

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

    // Earshot is closing (Exit): no idle wait and no start-up check. The operation in flight is cancelled; its
    // clean-up still runs, and a Block sequence stops changing protection and blocks straight away. Then, unless
    // the nodes are known to be blocked or Block at boot is off, the block before closing runs: it reads the state
    // again and blocks enabled nodes that are not in use. WhenIdleAsync waits for all of it.
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

        _closingSource.Dispose();
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

    // Reads the boot block status now and keeps it. Null when the read failed (logged with its code). Throws
    // OperationCanceledException only when ct is cancelled.
    public async Task<BootBlockStatus?> ReadBlockStatusAsync(CancellationToken ct = default)
    {
        long read = ++_blockReadsStarted;
        try
        {
            BootBlockStatus status = await _block.GetStatusAsync(ct);
            if (read > _blockReadApplied)
            {
                _blockReadApplied = read;
                _blockStatus = status;
                _blockStatusFresh = true;
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

            LogReadFailure("block-status", "The boot block status could not be read.", ex);
            return null;
        }
    }

    // A left click or the menu toggle. The report comes back after every clean-up, a cancelled one included.
    public async Task<ToggleReport> ToggleAsync(ToggleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = request.Connect ? "connect" : "disconnect";

        // The user has acted, so the start-up check has nothing left to reconcile.
        if (!_startChecked)
        {
            _startChecked = true;
            _log.Info("Start-up check skipped: a " + name + " was asked for first.");
        }

        if (request.Connect && !_options.SafeMode)
        {
            ShowCard(request, TrayStatus.CardConnecting);
        }

        try
        {
            return await RunExclusiveAsync(
                name,
                token => request.Connect ? ConnectCoreAsync(request, token) : DisconnectCoreAsync(request, token),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The operation body turns its own cancellation into a report, so this is a cancellation while it
            // waited for the operation before it.
            _log.Info(name + ": cancelled before it started.");
            return new ToggleReport(request.Connect, OpStatus.NotAttempted, "", Array.Empty<StepOutcome>(), Cancelled: true);
        }
    }

    // The Protect audio quality menu toggle. The caller has saved the setting. Throws OperationCanceledException
    // when ct is cancelled before the change starts or while it waits.
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

                if (run.IntentKept)
                {
                    run.Steps.Add(StepOutcomes.NotAttempted("protect-intent", "The node state could not be read, so the request is kept until the AirPods next connect."));
                    return new ControllerResult(OpStatus.NotAttempted, SavedForNextConnectMessage, run.Steps);
                }

                return new ControllerResult(OpStatus.AlreadyInState, protect ? AlreadyProtectedMessage : AlreadyOffMessage, run.Steps);
            },
            ct);

    // Choose device. Waits for the operation in flight, its clean-up included, so a re-block after an allow
    // finishes against the device the gate pins now before the pin moves.
    public Task<ControllerResult> ChangeDeviceAsync(string address12, CancellationToken ct = default) =>
        RunExclusiveAsync(GateVerbs.SetDevice, token => _block.SetDeviceAsync(address12, token), ct);

    // Runs a menu action (setup, Block at boot) as an operation, so it never overlaps a connect or a block.
    public Task<ControllerResult> RunAsync(string name, Func<CancellationToken, Task<ControllerResult>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunExclusiveAsync(name, operation, ct);
    }

    // WM_QUERYENDSESSION and WM_ENDSESSION. Never waits: the block is started and its result logged later, if the
    // session survives long enough.
    public void OnSessionEnding(SessionEndingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!e.IsQuery && !e.Ending)
        {
            if (_sessionBlockIssued)
            {
                _log.Info("The session end was cancelled after a block was issued for it.");
            }

            _sessionBlockIssued = false;
            return;
        }

        if (_sessionBlockIssued || _disposed)
        {
            return;
        }

        string why = SessionBlockBlocker();
        if (why.Length > 0)
        {
            _log.Info("Session ending: no block issued, because " + why + ".");
            return;
        }

        _sessionBlockIssued = true;
        CancelIdleWait("the session is ending");

        // A queued allow must not run after the block: the operation in flight is cancelled, and its own
        // clean-up blocks again anyway.
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

        _log.Info("Session ending: block issued at " + Utc(issued) + " (" + (e.IsQuery ? "WM_QUERYENDSESSION" : "WM_ENDSESSION") + ").");
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
        _currentCancel?.Cancel();
        _closingSource.Cancel();
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
            return "Block at boot is off";
        }

        if (status.State == BlockState.NotSetUp)
        {
            return "the boot block is not set up";
        }

        if (_blockStatusFresh && status.State == BlockState.Blocked)
        {
            return "the nodes are already blocked";
        }

        return "";
    }

    // Why no block before closing is queued, from what is known now, or "" when one is. The block itself reads the
    // state again once the operation in flight has finished.
    private string ClosingBlockBlocker()
    {
        if (_sessionBlockIssued)
        {
            return "a block was issued for the session end";
        }

        BootBlockStatus? status = _blockStatus;
        if (status is null)
        {
            return "the boot block status was never read";
        }

        if (!status.BlockAtBoot)
        {
            return "Block at boot is off";
        }

        if (status.State == BlockState.NotSetUp)
        {
            return "the boot block is not set up";
        }

        if (_current is null && _blockStatusFresh && status.State == BlockState.Blocked)
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
            string text = TrayReport.Describe("session-end block (issued " + Utc(issued) + ")", result.Status, result.UserMessage, result.Steps);
            _log.Write(result.IsSuccess ? LogLevel.Info : LogLevel.Warn, text);
        }
        catch (Exception ex)
        {
            StepOutcome step = StepOutcomes.FromHResult("session-end-block", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
            _log.Error("Session ending: the block issued at " + Utc(issued) + " failed. " + TrayReport.DescribeStep(step), ex);
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
    private async Task<T> RunExclusiveAsync<T>(string name, Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        while (_current is not null || _sessionBlock is { IsCompleted: false })
        {
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

    private async Task<ToggleReport> ConnectCoreAsync(ToggleRequest request, CancellationToken ct)
    {
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
                BootBlockStatus? status = await ReadBlockStatusAsync(ct);
                string? refusal = CoordinatorRules.AllowFirstRefusal(status, request.Container);
                if (refusal is not null)
                {
                    _log.Warn("connect: every render endpoint is NOTPRESENT and the nodes read " + (status is null ? "nothing" : Describe(status)) +
                        " for container " + request.Container.ToString("D") + ", so nothing is allowed.");
                    return Finish(request, OpStatus.Failed, refusal, steps);
                }

                cleanup.BlockAtBoot = status!.BlockAtBoot;
                ShowCard(request, AllowingStatus);
                cleanup.AllowIssued = true;
                ControllerResult allow = await _block.AllowAsync(ct);
                Record(steps, "allow (connect)", allow);
                if (allow.Status is OpStatus.Failed or OpStatus.NotAttempted)
                {
                    await CleanUpConnectAsync(request, cleanup, steps);
                    return Finish(request, OpStatus.Failed, allow.UserMessage, steps);
                }

                await ReadBlockStatusAsync(ct);
                if (!await WaitForSnapshotAsync(request.Container, CoordinatorRules.RenderEndpointPresent, EndpointWait, ct))
                {
                    _log.Warn("connect: no render endpoint came back within " + Seconds(EndpointWait) + " of the allow.");
                    await CleanUpConnectAsync(request, cleanup, steps);
                    return Finish(request, OpStatus.Failed, DidNotComeBackMessage, steps);
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

            await CleanUpConnectAsync(request, cleanup, steps);
            return Finish(request, OpStatus.Failed, CoordinatorRules.ConnectFailureMessage(result), steps);
        }
        catch (Exception ex)
        {
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
                return new ToggleReport(Connect: true, OpStatus.Failed, "", steps, Cancelled: true);
            }

            throw;
        }
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
            return new ToggleReport(Connect: true, OpStatus.Success, TrayStatus.CardConnected, steps, Cancelled: true);
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
        ShowCard(request, TryingAnotherWayMessage);
        cleanup.ProtectOffIssued = true;
        cleanup.ReblockAfterFallback = true;
        ControllerResult off = await _protection.ApplyAsync(false, ct);
        Record(steps, "protect-off (connect)", off);
        if (off.Status is OpStatus.Failed or OpStatus.NotAttempted)
        {
            await CleanUpConnectAsync(request, cleanup, steps);
            return Finish(request, OpStatus.Failed, CouldNotReachDriverMessage, steps);
        }

        if (!await WaitForSnapshotAsync(request.Container, CoordinatorRules.CaptureEndpointPresent, EndpointWait, ct))
        {
            _log.Warn("connect: the Hands-Free endpoint did not come back within " + Seconds(EndpointWait) + "; connecting anyway.");
        }

        ConnectResult again = await _connection.ConnectAsync(request.Container, ct);
        steps.AddRange(again.Steps);
        if (!again.Confirmed)
        {
            await CleanUpConnectAsync(request, cleanup, steps);
            return Finish(request, OpStatus.Failed, CoordinatorRules.ConnectFailureMessage(again), steps);
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
    private async Task CleanUpConnectAsync(ToggleRequest request, ConnectCleanup cleanup, List<StepOutcome> steps)
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
                return;
            }

            DeviceSnapshot? now = await RefreshSnapshotAsync();
            if (now is not null && CoordinatorRules.RenderOf(now, request.Container) == RenderState.Active)
            {
                _log.Info("connect clean-up: the AirPods are in use after all, so the nodes stay enabled.");
                return;
            }

            BootBlockStatus? status = await ReadBlockStatusAsync(CancellationToken.None);
            bool blockAtBoot = status?.BlockAtBoot ?? cleanup.BlockAtBoot ?? false;
            if (!blockAtBoot)
            {
                _log.Info("connect clean-up: Block at boot is " + (status is null && cleanup.BlockAtBoot is null ? "not known" : "off") + ", so the nodes are not blocked.");
                return;
            }

            if (status?.State == BlockState.Blocked)
            {
                return;
            }

            // Only an allow this operation issued is undone without a good node read.
            if (!cleanup.AllowIssued && !CoordinatorRules.NodesEnabled(status))
            {
                _log.Info("connect clean-up: the nodes read " + (status is null ? "nothing" : Describe(status)) + ", so nothing is blocked.");
                return;
            }

            _log.Info("connect clean-up: blocking the nodes again.");
            ProtectionRun run = await RunBlockSequenceAsync(BlockReason.ConnectCleanUp, request.Container, request.Place, CancellationToken.None);
            steps.AddRange(run.Steps);
            if (run.Skipped is { } why)
            {
                _log.Info("connect clean-up: the nodes are not blocked again, because " + why + ".");
            }
            else if (!run.BlockTook)
            {
                _log.Warn("connect clean-up: the nodes could not be blocked again: " + (run.NodeChange?.UserMessage ?? "no block result") + ".");
            }
        }
        catch (Exception ex)
        {
            steps.Add(StepOutcomes.FromHResult("connect-clean-up", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false));
            _log.Error("connect clean-up failed; the nodes may still be enabled.", ex);
        }
    }

    // The Hands-Free assisted way turned protection off and did not connect: the saved setting goes back while the
    // nodes are still enabled. While Earshot closes this is left to the next connect, so the block is not delayed.
    private async Task PutProtectionBackAsync(List<StepOutcome> steps)
    {
        bool protect = Settings.ProtectAudioQuality;
        if (!protect)
        {
            _log.Info("connect clean-up: Protect audio quality is off in settings, so protection stays off.");
            return;
        }

        if (_closing)
        {
            _pendingProtect = true;
            _log.Info("connect clean-up: Earshot is closing, so protection is put back at the next connect.");
            return;
        }

        ControllerResult on;
        try
        {
            on = await _protection.ApplyAsync(true, _closingSource.Token);
        }
        catch (OperationCanceledException) when (_closing)
        {
            // Exit was chosen while it ran: the re-block after it must not wait, and must not be skipped.
            _pendingProtect = true;
            steps.Add(StepOutcomes.NotAttempted("protect-on (clean-up)", "Stopped waiting because Earshot is closing; the request may still finish."));
            _log.Info("connect clean-up: stopped waiting for protection because Earshot is closing; the nodes are blocked next.");
            return;
        }

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            return new ToggleReport(Connect: false, OpStatus.Failed, "", steps, Cancelled: true);
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
                _log.Info("disconnect: the nodes are not blocked, because " + why + ".");
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
    //
    // A Block goal: its reads and its block are never cancelled, since the block is the at-rest action. Its
    // protection steps are cut short when ct is cancelled or Earshot starts closing; while closing, the goal skips
    // protection and blocks straight away, with no status read after the block (the result is the evidence).
    private async Task<ProtectionRun> RunProtectionAsync(ProtectionGoal goal, bool protect, CardPlace place, bool notice, CancellationToken ct, BlockCheck? check = null)
    {
        bool blockGoal = goal == ProtectionGoal.Block;
        CancellationToken readCt = blockGoal ? CancellationToken.None : ct;
        using CancellationTokenSource? serviceCancel = blockGoal && !_disposed
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _closingSource.Token)
            : null;
        CancellationToken serviceCt = serviceCancel?.Token ?? ct;

        var run = new ProtectionRun { Status = blockGoal && _closing ? FreshStatus : await ReadBlockStatusAsync(readCt) };
        bool? kept = blockGoal ? null : await ReadPendingProtectAsync(ct);
        bool intentPending = _pendingProtect is not null || kept is not null;
        ProtectionProgress progress = default;

        for (int stepCount = 0; ; stepCount++)
        {
            if (stepCount == MaxProtectionSteps)
            {
                _log.Warn("protection " + goal + ": stopped after " + MaxProtectionSteps.ToString(CultureInfo.InvariantCulture) + " steps.");
                break;
            }

            var facts = new ProtectionFacts(CoordinatorRules.PhaseOf(run.Status), run.Services?.State ?? AudioProtectionState.Unknown, protect, intentPending);
            ProtectionAction action = ProtectionPolicy.Next(goal, facts, progress);
            if (blockGoal && _closing && action is ProtectionAction.ReadServices or ProtectionAction.ProtectOn)
            {
                _log.Info("protection Block: Earshot is closing, so the nodes are blocked without changing protection first.");
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
            try
            {
                switch (action)
                {
                    case ProtectionAction.ReadServices:
                        run.Services = await ReadServicesAsync(serviceCt);
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
                        ControllerResult change = await _protection.ApplyAsync(on, serviceCt);
                        Record(run.Steps, (on ? GateVerbs.ProtectOn : GateVerbs.ProtectOff) + " (" + goal + ")", change);
                        run.ServiceChange = change;
                        await NoteServiceChangeAsync(run, change, on, protect, action == ProtectionAction.StoreIntent, place, notice, serviceCt);
                        break;
                    }

                    case ProtectionAction.KeepIntent:
                        _pendingProtect = protect;
                        run.IntentKept = true;
                        _log.Info("protection: the node state is not known, so " + (protect ? "protect" : "restore") + " is kept until the AirPods next connect.");
                        break;

                    case ProtectionAction.BlockNodes:
                    {
                        ControllerResult block = await _block.BlockAsync(readCt);
                        Record(run.Steps, "block (" + goal + (check is { } reason ? ", " + reason.Reason : "") + ")", block);
                        run.NodeChange = block;
                        break;
                    }

                    case ProtectionAction.AllowNodes:
                    {
                        ControllerResult allow = await _block.AllowAsync(ct);
                        Record(run.Steps, "allow (" + goal + ")", allow);
                        run.NodeChange = allow;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (blockGoal && _closing && action is ProtectionAction.ReadServices or ProtectionAction.ProtectOn)
            {
                run.Steps.Add(StepOutcomes.NotAttempted(action + " (" + goal + ")", "Stopped waiting because Earshot is closing; the request may still finish."));
                _log.Info("protection Block: stopped waiting for " + action + " because Earshot is closing; the nodes are blocked next.");
            }

            progress = progress.After(action);
            if (ProtectionPolicy.IsDeviceChange(action) || action == ProtectionAction.StoreIntent)
            {
                if (blockGoal && _closing)
                {
                    continue;
                }

                // A node change makes the old node read stale. A service change only removes or adds the service's
                // own nodes, so a failed read after one keeps the node state that was known.
                BootBlockStatus? after = await ReadBlockStatusAsync(readCt);
                bool nodeChange = action is ProtectionAction.BlockNodes or ProtectionAction.AllowNodes;
                run.Status = after ?? (nodeChange ? null : run.Status);
            }
        }

        if (goal == ProtectionGoal.Allow && CoordinatorRules.PhaseOf(run.Status) == NodePhase.Allowed && !run.ServiceChangeFailed)
        {
            _pendingProtect = null;
        }

        return run;
    }

    // Why a block that is about to be sent no longer should be, or null when it should. Automatic blocks (idle,
    // start-up, closing) need good reads: Block at boot on, the nodes enabled, and render not ACTIVE. A connect
    // clean-up undoes its own allow unless Block at boot is now off or the AirPods are in use after all. A
    // disconnect blocks unless Block at boot is now off: the block is what drops a link the request did not.
    private async Task<string?> BlockSkipReasonAsync(BlockCheck check, BootBlockStatus? status, bool refreshSnapshot)
    {
        if (status is { BlockAtBoot: false })
        {
            return "Block at boot is now off";
        }

        bool automatic = check.Reason is BlockReason.Idle or BlockReason.StartUp or BlockReason.Closing;
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

        // Protection was changed first, which takes time and churns the endpoints, so the device is read again.
        DeviceSnapshot? now = refreshSnapshot ? await RefreshSnapshotAsync() : _snapshot;
        RenderState render = now is null ? RenderState.Unknown : CoordinatorRules.RenderOf(now, check.Container);
        if (render == RenderState.Active)
        {
            return "the AirPods are in use";
        }

        return automatic && render == RenderState.Unknown ? "the device state is not known" : null;
    }

    private async Task NoteServiceChangeAsync(ProtectionRun run, ControllerResult change, bool on, bool protect, bool storeIntent, CardPlace place, bool notice, CancellationToken ct)
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
                run.Services = await ReadServicesAsync(ct);
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

    // True only when a good read shows render not ACTIVE for container: the latest snapshot when it is one,
    // otherwise a fresh one.
    private async Task<bool> RenderNotActiveAsync(Guid container)
    {
        DeviceSnapshot? now = _snapshot.ReadStatus == SnapshotReadStatus.Ok ? _snapshot : await RefreshSnapshotAsync();
        return now is not null && CoordinatorRules.RenderOf(now, container) == RenderState.NotActive;
    }

    // Reads the protection status now and keeps it. Null when the read failed (logged with its code).
    private async Task<AudioProtectionSnapshot?> ReadServicesAsync(CancellationToken ct)
    {
        long read = ++_serviceReadsStarted;
        try
        {
            AudioProtectionSnapshot services = await _protection.GetStatusAsync(ct);
            if (read > _serviceReadApplied)
            {
                _serviceReadApplied = read;
                _protectionStatus = services;
                RaiseChanged();
            }

            return services;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogReadFailure("protection-status", "The audio protection status could not be read.", ex);
            return null;
        }
    }

    private async Task<bool?> ReadPendingProtectAsync(CancellationToken ct)
    {
        try
        {
            return await _protection.GetPendingProtectAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogReadFailure("protection-intent", "The kept protection request could not be read.", ex);
            return null;
        }
    }

    // Refreshes the device monitor. Null when the refresh failed (logged with its code).
    private async Task<DeviceSnapshot?> RefreshSnapshotAsync()
    {
        try
        {
            DeviceSnapshot snapshot = await _monitor.RefreshAsync(CancellationToken.None);
            Observe(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            LogReadFailure("device-refresh", "The device state could not be refreshed.", ex);
            return null;
        }
    }

    // Waits for a snapshot in which ready holds for container: armed first, then refreshed, so a state reached
    // before the wait began is seen. False at the timeout; OperationCanceledException when ct is cancelled.
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
            using CancellationTokenRegistration onTimeout = timeoutSource.Token.Register(() => seen.TrySetResult(false));
            using CancellationTokenRegistration onCancel = ct.Register(() => seen.TrySetCanceled(ct));
            DeviceSnapshot now = await _monitor.RefreshAsync(ct);
            Observe(now);
            if (ready(now, container))
            {
                seen.TrySetResult(true);
            }

            return await seen.Task;
        }
        finally
        {
            _monitor.SnapshotChanged -= OnSnapshot;
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

    // Keeps the newest snapshot. A snapshot from an older enumeration never replaces a newer one.
    private void Observe(DeviceSnapshot snapshot)
    {
        if (snapshot.Sequence != 0 && _snapshot.Sequence > snapshot.Sequence)
        {
            return;
        }

        _snapshot = snapshot;
        if (CoordinatorRules.RenderOf(snapshot, WatchedContainer()) == RenderState.Active)
        {
            ReArmIdleRule("the AirPods are in use");
        }
    }

    private Guid WatchedContainer() => CoordinatorRules.WatchedContainer(_blockStatus, Settings, _snapshot);

    // Why the idle block cannot be due now, or "" when every condition for it holds.
    private string IdleBlocker()
    {
        if (_disposed || _closing)
        {
            return "Earshot is closing";
        }

        if (IsBusy)
        {
            return "an operation is in flight";
        }

        if (!_blockStatusFresh || _blockStatus is null)
        {
            return "the boot block status is not known";
        }

        if (!_blockStatus.BlockAtBoot)
        {
            return "Block at boot is off";
        }

        if (!CoordinatorRules.NodesEnabled(_blockStatus))
        {
            return "the nodes are not enabled";
        }

        return CoordinatorRules.RenderOf(_snapshot, WatchedContainer()) switch
        {
            RenderState.Unknown => "the device state is not known",
            RenderState.Active => "the AirPods are in use",
            _ => "",
        };
    }

    private void EvaluateIdle()
    {
        string blocker = IdleBlocker();
        if (blocker.Length > 0)
        {
            CancelIdleWait(blocker);
            return;
        }

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

            string blocker = IdleBlocker();
            if (blocker.Length > 0)
            {
                _log.Write(LogLevel.Debug, "Idle block not issued: " + blocker + ".");
                return;
            }

            _log.Info("Blocking the nodes: the AirPods were not in use for " + Seconds(delay) + " with nothing in flight (" + Utc(_time.GetUtcNow()) + ").");
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
        if (run.Skipped is null && !run.BlockTook)
        {
            ClosingNotice = run.NodeChange?.UserMessage ?? CouldNotBlockMessage;
        }

        return run.BlockTook;
    }

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

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Describe(BootBlockStatus status) =>
        status.State + ", Block at boot " + (status.BlockAtBoot ? "on" : "off");

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private readonly record struct BlockAttempt(bool Issued, bool Took, string Message)
    {
        public static BlockAttempt NotIssued => new(Issued: false, Took: false, "");
    }

    // Why a Block sequence runs and which device its render check watches.
    private readonly record struct BlockCheck(BlockReason Reason, Guid Container);

    // What a connect has changed so far and must put back if it does not reach ACTIVE.
    private sealed class ConnectCleanup
    {
        public bool AllowIssued { get; set; }

        // Block at boot as read before the allow, for a clean-up whose own read fails.
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

        // Why the block was not sent after all (Block at boot now off, the AirPods in use, a read that failed), or
        // null when it was sent or none was needed.
        public string? Skipped { get; set; }

        // The nodes ended blocked: the block succeeded, or none was needed because they already were.
        public bool BlockTook => NodeChange is null ? Skipped is null && Status?.State == BlockState.Blocked : NodeChange.IsSuccess;

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
