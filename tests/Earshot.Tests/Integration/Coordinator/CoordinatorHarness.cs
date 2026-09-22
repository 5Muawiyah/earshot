using System.Diagnostics;
using System.Drawing;
using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The block coordinator against fakes, on one thread, with a clock the test moves. Nothing here reaches a
// device, a device node, Bluetooth, Task Scheduler or a window.
//
// Everything the coordinator awaits is completed by the test thread, and every continuation is posted to the
// manual synchronisation context, so Pump runs the whole flow in order with no waiting and no sleeping.
//
// Every Pump checks the at-rest invariant against the fakes (AssertAtRest), so a flow that leaves the nodes
// enabled with nothing in hand fails where it happens, not only in the test that looks for it.
internal sealed class CoordinatorHarness : IDisposable
{
    private readonly SynchronizationContext? _previous;

    public CoordinatorHarness(bool safeMode = false, bool startedAtLogon = false, bool protectAudio = false)
    {
        _previous = SynchronizationContext.Current;
        Ui = new ManualUiContext();
        SynchronizationContext.SetSynchronizationContext(Ui);

        Connection = new FakeConnection(Monitor) { Trace = Trace };
        Block = new FakeBlock(Monitor) { Trace = Trace };
        Protection.Trace = Trace;
        Settings.Update(s =>
        {
            s.PinnedContainerId = Devices.Container;
            s.PinnedAddress = Devices.Address;
            s.ProtectAudioQuality = protectAudio;

            // Off here even though the production default is on (EarshotSettings.HandBackOnShutdownAndSleep):
            // every existing test in this folder was written and passes against today's session-end behaviour,
            // so the harness keeps that byte for byte unless a hand-back test opts in explicitly.
            s.HandBackOnShutdownAndSleep = false;
        });

        Coordinator = new BlockCoordinator(Monitor, Connection, Block, Protection, Settings, Cards, Log, Time,
            new CoordinatorOptions(safeMode, startedAtLogon));
    }

    public ManualUiContext Ui { get; }

    // Every call to a controller, in the order they were made, so a test can pin the order of a service change
    // and a node change.
    public List<string> Trace { get; } = new();

    public ManualTime Time { get; } = new();

    public CapturingLog Log { get; } = new();

    public FakeMonitor Monitor { get; } = new();

    public FakeConnection Connection { get; }

    public FakeBlock Block { get; }

    public FakeProtection Protection { get; } = new();

    public FakeSettings Settings { get; } = new();

    public RecordingCards Cards { get; } = new();

    public BlockCoordinator Coordinator { get; }

    // False for a test that builds a world Earshot has not been told about yet and checks the invariant itself.
    public bool CheckInvariantOnPump { get; set; } = true;

    // Subscribes the coordinator and lets the first status read and start-up check run.
    public void Start()
    {
        Coordinator.Start();
        Pump();
    }

    // Runs everything posted to the UI thread, including what that work posts in turn, then checks the invariant.
    public void Pump()
    {
        Ui.RunAll();
        Assert.IsEmpty(Block.UnchosenActiveBlocks, "A block was sent while render was ACTIVE; the test must choose Block.ActiveLink.");
        if (CheckInvariantOnPump)
        {
            AssertAtRest("after a pump");
        }
    }

    // Task.WaitAsync(CancellationToken) on a task this harness completed with SetResult only observes that
    // completion after one real thread-pool hop for its own continuation to run on, not merely a posted
    // continuation Pump's synchronous drain can catch in the same turn: a plain Pump() right after SetResult can
    // see the waiter as still pending. This gives that hop a short, bounded, real wait to actually happen
    // (ManualTime does not move it along; only wall-clock time does), then pumps again.
    public void PumpAfterRealHop(Func<bool> done, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan bound = limit ?? TimeSpan.FromSeconds(2);
        while (!done())
        {
            if (clock.Elapsed > bound)
            {
                Assert.Fail("PumpAfterRealHop: still not done after " + bound.TotalMilliseconds + " ms of real waiting.");
            }

            Thread.Sleep(2);
            Pump();
        }
    }

    // The at-rest invariant as the fakes stand now: with Block at boot on, the nodes are blocked, or the AirPods
    // are in use, or Earshot still has something in hand that ends in one of those (an operation in flight, an
    // idle wait running, or a read of the state scheduled). A state nothing can observe cannot be acted on, so a
    // read that fails now, unknown nodes and a boot block that is not set up are allowed too, but only while the
    // fault lasts: once it clears, the coordinator must still have something in hand.
    public void AssertAtRest(string when = "at the end")
    {
        BootBlockStatus status = Block.Status;
        if (!status.BlockAtBoot || status.State is BlockState.Blocked or BlockState.NotSetUp or BlockState.NotFound or BlockState.Unknown)
        {
            return;
        }

        DeviceSnapshot now = Monitor.Current;
        if (CoordinatorRules.RenderOf(now, Devices.Container) == RenderState.Active ||
            Coordinator.IsBusy ||
            Coordinator.IdleWaitRunning ||
            Coordinator.RecheckRunning ||
            Block.StatusFailure is not null ||
            Block.StatusStalls ||
            Protection.StatusStalls ||
            now.ReadStatus != SnapshotReadStatus.Ok)
        {
            return;
        }

        Assert.Fail("The at-rest invariant does not hold " + when + ": the nodes are " + status.State +
            " with Block at boot on, the AirPods are not in use, and Earshot has nothing in flight, no idle wait and no read scheduled.");
    }

    // Moves the clock, firing every timer due in that time, then runs what they posted.
    public void Advance(TimeSpan by)
    {
        Time.Advance(by);
        Pump();
    }

    public void Publish(DeviceSnapshot snapshot)
    {
        Monitor.Publish(snapshot);
        Pump();
    }

    public static ToggleRequest Request(bool connect, Point? click = null) =>
        new(connect, Devices.Container, Devices.Name, click is { } point ? CardPlace.AtClick(point) : CardPlace.NearCursor);

    // Starts a connect or disconnect and asserts it finished within this pump.
    public ToggleReport Toggle(bool connect, Point? click = null)
    {
        Task<ToggleReport> task = Coordinator.ToggleAsync(Request(connect, click));
        Pump();
        Assert.IsTrue(task.IsCompleted, (connect ? "The connect" : "The disconnect") + " did not finish.");
        return task.GetAwaiter().GetResult();
    }

    public ControllerResult SetProtection(bool protect, Point? click = null)
    {
        Task<ControllerResult> task = Coordinator.SetProtectionAsync(protect, click is { } point ? CardPlace.AtClick(point) : CardPlace.NearCursor);
        Pump();
        Assert.IsTrue(task.IsCompleted, "The protection change did not finish.");
        return task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        Ui.RunAll();
        SynchronizationContext.SetSynchronizationContext(_previous);
    }
}

// Runs posted work only when the test asks, on the test thread, in the order it was posted.
internal sealed class ManualUiContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    // Nothing in the coordinator sends synchronously; a Send would run work out of order.
    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("The coordinator must never post to the UI thread and wait for it.");

    public override SynchronizationContext CreateCopy() => this;

    public void RunAll()
    {
        for (int run = 0; _queue.Count > 0; run++)
        {
            Assert.IsLessThan(10_000, run, "The UI queue never emptied.");
            (SendOrPostCallback callback, object? state) = _queue.Dequeue();
            callback(state);
        }
    }
}

// A clock that only moves when the test moves it. Timers fire on the thread that calls Advance.
internal sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 9, 15, 20, 30, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ActiveTimers => _timers.Count(t => t.Due is not null);

    internal DateTimeOffset Now => _now;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target = _now + by;
        while (true)
        {
            ManualTimer? next = _timers
                .Where(t => t.Due is { } due && due <= target)
                .OrderBy(t => t.Due!.Value)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            _now = next.Due!.Value;
            next.Fire();
        }

        _now = target;
    }

    internal void Forget(ManualTimer timer) => _timers.Remove(timer);
}

internal sealed class ManualTimer : ITimer
{
    private readonly ManualTime _time;
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private TimeSpan _period = Timeout.InfiniteTimeSpan;

    public ManualTimer(ManualTime time, TimerCallback callback, object? state)
    {
        _time = time;
        _callback = callback;
        _state = state;
    }

    public DateTimeOffset? Due { get; private set; }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        _period = period;
        Due = dueTime == Timeout.InfiniteTimeSpan ? null : _time.Now + dueTime;
        return true;
    }

    public void Fire()
    {
        Due = _period == Timeout.InfiniteTimeSpan ? null : _time.Now + _period;
        _callback(_state);
    }

    public void Dispose()
    {
        Due = null;
        _time.Forget(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

// The device as the tests describe it: the owner's AirPods container, with a render endpoint and, unless
// protection has removed it, a Hands-Free capture endpoint.
internal static class Devices
{
    public const string Address = "0A1B2C3D4E8C";
    public const string Name = "Jonathan’s AirPods Pro";

    public static readonly Guid Container = new("5C3A9E21-4B7D-5F18-9A6C-2D8E0B4F7A13");

    // In use on this PC: the render endpoint is ACTIVE.
    public static DeviceSnapshot Active(long sequence, bool capture = true) =>
        Snapshot(EndpointState.Active, sequence, capture);

    // Paired and present, nothing playing.
    public static DeviceSnapshot Idle(long sequence, bool capture = true) =>
        Snapshot(EndpointState.Unplugged, sequence, capture);

    // The nodes are disabled, so the endpoints are NOTPRESENT.
    public static DeviceSnapshot NotPresent(long sequence) =>
        Snapshot(EndpointState.NotPresent, sequence, capture: false);

    // The enumeration failed: nothing in it is an observation.
    public static DeviceSnapshot Unreadable(long sequence) =>
        Snapshot(EndpointState.Active, sequence, capture: true) with
        {
            ReadStatus = SnapshotReadStatus.Failed,
            Resolution = TargetResolution.ReadFailed,
        };

    // Nothing has been enumerated yet.
    public static DeviceSnapshot NotRead() =>
        new(Target: null, AllGroups: Array.Empty<DeviceModel>(), TakenUtc: DateTimeOffset.UnixEpoch);

    private static DeviceSnapshot Snapshot(EndpointState render, long sequence, bool capture)
    {
        var endpoints = new List<AudioEndpoint>
        {
            new("{0.0.0.00000000}.render", EndpointFlow.Render, render, Name, Container),
        };
        if (capture)
        {
            endpoints.Add(new("{0.0.1.00000000}.capture", EndpointFlow.Capture, render, Name, Container));
        }

        var model = new DeviceModel(
            Container,
            Name,
            render == EndpointState.Active ? ConnectionState.Connected : ConnectionState.Disconnected,
            endpoints);
        return new DeviceSnapshot(model, [model], DateTimeOffset.UnixEpoch.AddSeconds(sequence))
        {
            Sequence = sequence,
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        };
    }
}

internal static class Statuses
{
    public static BootBlockStatus Allowed(bool blockAtBoot = true) => Status(BlockState.Allowed, blockAtBoot);

    public static BootBlockStatus Blocked(bool blockAtBoot = true) => Status(BlockState.Blocked, blockAtBoot);

    public static BootBlockStatus Mixed(bool blockAtBoot = true) => Status(BlockState.Mixed, blockAtBoot);

    public static BootBlockStatus Unknown(bool blockAtBoot = true) => Status(BlockState.Unknown, blockAtBoot);

    public static BootBlockStatus NotSetUp() =>
        new(BlockState.NotSetUp, Devices.Container, Array.Empty<BluetoothNode>(), TasksInstalled: false, BlockAtBoot: true);

    private static BootBlockStatus Status(BlockState state, bool blockAtBoot) =>
        new(state, Devices.Container, Array.Empty<BluetoothNode>(), TasksInstalled: true, blockAtBoot);
}

// Connect and disconnect results the tests hand back.
internal static class Results
{
    public static ConnectResult Connected() => new(ConnectOutcome.Confirmed, ConnectMessages.Connected, Array.Empty<StepOutcome>());

    public static ConnectResult Disconnected() => new(ConnectOutcome.Confirmed, ConnectMessages.Disconnected, Array.Empty<StepOutcome>());

    public static ConnectResult NodesBlocked() =>
        new(ConnectOutcome.NodesBlocked, ConnectMessages.AllowingFirst, Array.Empty<StepOutcome>());

    public static ConnectResult TimedOut() =>
        new(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting,
            [StepOutcomes.FromHResult("ks-reconnect:src", 0, "a2dp: adapter")]);

    public static ConnectResult DidNotDisconnect() =>
        new(ConnectOutcome.AttemptedTimedOut, ConnectMessages.DidNotDisconnect,
            [StepOutcomes.FromHResult("ks-disconnect:src", 0, "a2dp: adapter")]);

    // Every filter turned the request down, the A2DP filter included: what the Hands-Free assisted way keys on.
    public static ConnectResult A2dpRejected() =>
        new(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.FromHResult("ks-reconnect:src", unchecked((int)0x80004005), "a2dp: adapter")]);

    // A gate change the Task Scheduler accepted whose end was never seen, because the wait for it ran out.
    public static ControllerResult GateWaitRanOut(string task, string message) => ControllerResult.Fail(message,
    [
        StepOutcomes.FromHResult(CoordinatorRules.TaskRunStepPrefix + "\\Earshot\\" + task, 0, "0123456789abcdef0123456789abcdef"),
        StepOutcomes.NotAttempted("task-wait:" + task, "No result within 150 s."),
    ]);

    // Nothing was sent to the A2DP filter, so the Hands-Free assisted way does not apply.
    public static ConnectResult NothingSent() =>
        new(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.NotAttempted("ks-reconnect:src", "a2dp: adapter")]);
}

// The monitor numbers its enumerations, so each snapshot a test sets or publishes is renumbered as the newest one:
// the numbers the tests and the other fakes pass only name the state. PublishLate raises a snapshot with the
// number given and leaves Current alone, for an enumeration that finishes after a newer one.
internal sealed class FakeMonitor : IDeviceMonitor
{
    private long _sequence;

    public DeviceSnapshot Current { get; private set; } = Devices.NotRead();

    public int Refreshes { get; private set; }

    // While true, a refresh never completes and ignores its token: a refresh queued on the audio worker behind a
    // driver call that has not returned, the worst case, since a refresh still queued would at least be cancelled.
    public bool RefreshStalls { get; set; }

    // Watching for endpoint changes failed at start: no notification ever comes, and only a refresh reads the device.
    public bool WatchFailed { get; set; }

    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged;

    public void Start()
    {
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        Refreshes++;
        return RefreshStalls ? new TaskCompletionSource<DeviceSnapshot>().Task : Task.FromResult(Current);
    }

    // The state a later read would show, with no notification.
    public void Set(DeviceSnapshot snapshot) => Current = Numbered(snapshot);

    // A change, as the monitor raises it on the UI thread.
    public void Publish(DeviceSnapshot snapshot)
    {
        Current = Numbered(snapshot);
        SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(Current));
    }

    public void PublishLate(DeviceSnapshot snapshot) =>
        SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));

    private DeviceSnapshot Numbered(DeviceSnapshot snapshot) =>
        snapshot.Sequence == 0 ? snapshot : snapshot with { Sequence = ++_sequence };

    public void Dispose()
    {
    }
}

internal sealed class FakeConnection : IConnectionController
{
    private readonly FakeMonitor _monitor;
    private long _sequence = 100;

    public FakeConnection(FakeMonitor monitor) => _monitor = monitor;

    public List<(bool Connect, Guid Container)> Calls { get; } = new();

    public List<string>? Trace { get; init; }

    // The next connects, in order. When it is empty the default is used: the AirPods become ACTIVE.
    public Queue<Func<CancellationToken, Task<ConnectResult>>> Connects { get; } = new();

    public Func<CancellationToken, Task<ConnectResult>>? OnDisconnect { get; set; }

    public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default)
    {
        Calls.Add((true, containerId));
        Trace?.Add("connect");
        if (Connects.Count > 0)
        {
            return Connects.Dequeue()(ct);
        }

        _monitor.Publish(Devices.Active(++_sequence));
        return Task.FromResult(Results.Connected());
    }

    public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default)
    {
        Calls.Add((false, containerId));
        Trace?.Add("disconnect");
        if (OnDisconnect is { } disconnect)
        {
            return disconnect(ct);
        }

        _monitor.Publish(Devices.Idle(++_sequence));
        return Task.FromResult(Results.Disconnected());
    }
}

// What a block does to a link that is ACTIVE when it is sent. Whether disabling the nodes drops an active link
// is not verified on the device (a live-test item), so a test that blocks while render is ACTIVE says which it
// assumes rather than inheriting a default.
internal enum ActiveLinkOnBlock
{
    NotChosen,
    Drops,   // the nodes go and the endpoints with them: render NOTPRESENT
    Stays,   // the disable is kept for the next start, but the link stays up until then: render still ACTIVE
}

internal sealed class FakeBlock : IBlockController
{
    private readonly FakeMonitor _monitor;
    private long _sequence = 200;

    public FakeBlock(FakeMonitor monitor) => _monitor = monitor;

    public ActiveLinkOnBlock ActiveLink { get; set; }

    // Blocks sent while render was ACTIVE with ActiveLink not chosen; Pump fails the test on any.
    public List<string> UnchosenActiveBlocks { get; } = new();

    public BootBlockStatus Status { get; set; } = Statuses.Allowed();

    public Exception? StatusFailure { get; set; }

    // While true, a status read never completes and ignores its token: a Task Scheduler or CfgMgr32 call that never
    // returns.
    public bool StatusStalls { get; set; }

    // The next reads that fail, once each, before reads work again. The fault has cleared once they are used up.
    public int StatusFailuresLeft { get; set; }

    // The next reads that show this state, once each, while the real state is Status.
    public Queue<BlockState> NextReadStates { get; } = new();

    // Run once each, in order, as the next reads start, so a test can change the world at one exact read.
    public Queue<Action> BeforeReads { get; } = new();

    public List<string> Calls { get; } = new();

    public List<string>? Trace { get; init; }

    public int StatusReads { get; private set; }

    // Gate changes that were handed a token that can be cancelled. The coordinator waits for every gate change.
    public List<string> CancellableChanges { get; } = new();

    public Func<CancellationToken, Task<ControllerResult>>? OnBlock { get; set; }

    public Func<CancellationToken, Task<ControllerResult>>? OnAllow { get; set; }

    public bool IsSetUp => Status.State != BlockState.NotSetUp;

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default)
    {
        StatusReads++;
        if (BeforeReads.Count > 0)
        {
            BeforeReads.Dequeue()();
        }

        if (StatusFailure is not null)
        {
            return Task.FromException<BootBlockStatus>(StatusFailure);
        }

        if (StatusStalls)
        {
            return new TaskCompletionSource<BootBlockStatus>().Task;
        }

        if (StatusFailuresLeft > 0)
        {
            StatusFailuresLeft--;
            return Task.FromException<BootBlockStatus>(new IOException("The process cannot access the file.", unchecked((int)0x80070020)));
        }

        return Task.FromResult(NextReadStates.Count > 0 ? Status with { State = NextReadStates.Dequeue() } : Status);
    }

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default)
    {
        Calls.Add("block");
        Trace?.Add("block");
        NoteToken("block", ct);
        if (OnBlock is { } block)
        {
            return block(ct);
        }

        bool active = CoordinatorRules.RenderOf(_monitor.Current, Devices.Container) == RenderState.Active;
        if (active && ActiveLink == ActiveLinkOnBlock.NotChosen)
        {
            UnchosenActiveBlocks.Add("block");
        }

        Status = Status with { State = BlockState.Blocked };
        if (!active || ActiveLink != ActiveLinkOnBlock.Stays)
        {
            _monitor.Publish(Devices.NotPresent(++_sequence));
        }

        return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
    }

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default)
    {
        Calls.Add("allow");
        Trace?.Add("allow");
        NoteToken("allow", ct);
        if (OnAllow is { } allow)
        {
            return allow(ct);
        }

        Status = Status with { State = BlockState.Allowed };
        _monitor.Publish(Devices.Idle(++_sequence));
        return Task.FromResult(ControllerResult.Ok("Allowed"));
    }

    // A Block at boot change that does something else than write the setting at once.
    public Func<bool, CancellationToken, Task<ControllerResult>>? OnSetBlockAtBoot { get; set; }

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default)
    {
        Calls.Add(blockAtBoot ? "setboot-on" : "setboot-off");
        NoteToken(blockAtBoot ? "setboot-on" : "setboot-off", ct);
        if (OnSetBlockAtBoot is { } set)
        {
            return set(blockAtBoot, ct);
        }

        Status = Status with { BlockAtBoot = blockAtBoot };
        return Task.FromResult(ControllerResult.Ok("Block at boot is " + (blockAtBoot ? "on" : "off")));
    }

    public Func<CancellationToken, Task<ControllerResult>>? OnSetDevice { get; set; }

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default)
    {
        Calls.Add("set-device:" + address12);
        Trace?.Add("set-device");
        NoteToken("set-device", ct);
        return OnSetDevice is { } setDevice ? setDevice(ct) : Task.FromResult(ControllerResult.Ok("Device chosen"));
    }

    public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default)
    {
        Calls.Add("setup");
        return Task.FromResult(ControllerResult.Ok("Earshot is set up"));
    }

    public Task<ControllerResult> UninstallAsync(CancellationToken ct = default)
    {
        Calls.Add("uninstall");
        return Task.FromResult(ControllerResult.Ok("Earshot is removed"));
    }

    private void NoteToken(string call, CancellationToken ct)
    {
        if (ct.CanBeCanceled)
        {
            CancellableChanges.Add(call);
        }
    }
}

internal sealed class FakeProtection : IAudioProtectionController
{
    public AudioProtectionState State { get; set; } = AudioProtectionState.NotProtected;

    public bool? Pending { get; set; }

    public Exception? StatusFailure { get; set; }

    // While true, a status read never completes and ignores its token: a Bluetooth API call that never returns.
    public bool StatusStalls { get; set; }

    public List<bool> Applies { get; } = new();

    // Protect verbs that were handed a token that can be cancelled. The coordinator waits for every gate change.
    public int CancellableApplies { get; private set; }

    public List<string>? Trace { get; set; }

    public Func<bool, CancellationToken, Task<ControllerResult>>? OnApply { get; set; }

    // What the endpoints do when protection changes, for the tests that need the Hands-Free endpoint back.
    public Action<bool>? Effect { get; set; }

    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        if (StatusFailure is not null)
        {
            return Task.FromException<AudioProtectionSnapshot>(StatusFailure);
        }

        if (StatusStalls)
        {
            return new TaskCompletionSource<AudioProtectionSnapshot>().Task;
        }

        return Task.FromResult(new AudioProtectionSnapshot(
            State,
            HandsfreeInstalled: State is AudioProtectionState.NotProtected or AudioProtectionState.Partial,
            HeadsetInstalled: false));
    }

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default)
    {
        Applies.Add(protect);
        Trace?.Add(protect ? "protect-on" : "protect-off");
        if (ct.CanBeCanceled)
        {
            CancellableApplies++;
        }

        Effect?.Invoke(protect);
        if (OnApply is { } apply)
        {
            return apply(protect, ct);
        }

        State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
        return Task.FromResult(ControllerResult.Ok(protect ? "Audio quality protected" : "Audio quality protection is off"));
    }

    public Task<bool?> GetPendingProtectAsync(CancellationToken ct = default) => Task.FromResult(Pending);
}

internal sealed class FakeSettings : ISettingsStore
{
    public EarshotSettings Current { get; } = new();

    public Exception? SaveFailure { get; set; }

    public event EventHandler<EarshotSettings>? Changed;

    public void Update(Action<EarshotSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        mutate(Current);
        Changed?.Invoke(this, Current);
    }

    public void Reload()
    {
    }
}

internal sealed record CardShown(CardContent Content, CardAnchor Anchor, Point? ClickPoint);

internal sealed class RecordingCards : ICardPresenter
{
    public List<CardShown> Shown { get; } = new();

    // Cards that were asked for but held back.
    public List<CardShown> HeldBack { get; } = new();

    public int Hides { get; private set; }

    // True while Windows is not taking notifications (a full-screen app, quiet time): a card nobody clicked for
    // is held back, as the real presenter does.
    public bool HoldBackNearTray { get; set; }

    public List<string> Statuses => Shown.Select(s => s.Content.Status).ToList();

    public void Show(CardContent content, CardAnchor anchor) => Add(new CardShown(content, anchor, null));

    public void Show(CardContent content, CardAnchor anchor, Point clickPoint) => Add(new CardShown(content, anchor, clickPoint));

    public Task<bool> ShowAsync(CardContent content, CardAnchor anchor, Point? clickPoint) =>
        Task.FromResult(Add(new CardShown(content, anchor, clickPoint)));

    public void Hide() => Hides++;

    private bool Add(CardShown card)
    {
        if (HoldBackNearTray && card.Anchor == CardAnchor.NearTray)
        {
            HeldBack.Add(card);
            return false;
        }

        Shown.Add(card);
        return true;
    }
}
