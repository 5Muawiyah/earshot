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

    // Subscribes the coordinator and lets the first status read and start-up check run.
    public void Start()
    {
        Coordinator.Start();
        Pump();
    }

    // Runs everything posted to the UI thread, including what that work posts in turn.
    public void Pump() => Ui.RunAll();

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
        Pump();
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
    public const string Address = "5A6B7C8D9EAF";
    public const string Name = "Owner’s AirPods Pro";

    public static readonly Guid Container = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");

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

    // Nothing was sent to the A2DP filter, so the Hands-Free assisted way does not apply.
    public static ConnectResult NothingSent() =>
        new(ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.NotAttempted("ks-reconnect:src", "a2dp: adapter")]);
}

internal sealed class FakeMonitor : IDeviceMonitor
{
    public DeviceSnapshot Current { get; private set; } = Devices.NotRead();

    public int Refreshes { get; private set; }

    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged;

    public void Start()
    {
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        Refreshes++;
        return Task.FromResult(Current);
    }

    // The state a later read would show, with no notification.
    public void Set(DeviceSnapshot snapshot) => Current = snapshot;

    // A change, as the monitor raises it on the UI thread.
    public void Publish(DeviceSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));
    }

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

internal sealed class FakeBlock : IBlockController
{
    private readonly FakeMonitor _monitor;
    private long _sequence = 200;

    public FakeBlock(FakeMonitor monitor) => _monitor = monitor;

    public BootBlockStatus Status { get; set; } = Statuses.Allowed();

    public Exception? StatusFailure { get; set; }

    public List<string> Calls { get; } = new();

    public List<string>? Trace { get; init; }

    public int StatusReads { get; private set; }

    public Func<CancellationToken, Task<ControllerResult>>? OnBlock { get; set; }

    public Func<CancellationToken, Task<ControllerResult>>? OnAllow { get; set; }

    public bool IsSetUp => Status.State != BlockState.NotSetUp;

    public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default)
    {
        StatusReads++;
        return StatusFailure is null ? Task.FromResult(Status) : Task.FromException<BootBlockStatus>(StatusFailure);
    }

    public Task<ControllerResult> BlockAsync(CancellationToken ct = default)
    {
        Calls.Add("block");
        Trace?.Add("block");
        if (OnBlock is { } block)
        {
            return block(ct);
        }

        Status = Status with { State = BlockState.Blocked };
        _monitor.Publish(Devices.NotPresent(++_sequence));
        return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
    }

    public Task<ControllerResult> AllowAsync(CancellationToken ct = default)
    {
        Calls.Add("allow");
        Trace?.Add("allow");
        if (OnAllow is { } allow)
        {
            return allow(ct);
        }

        Status = Status with { State = BlockState.Allowed };
        _monitor.Publish(Devices.Idle(++_sequence));
        return Task.FromResult(ControllerResult.Ok("Allowed"));
    }

    public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default)
    {
        Calls.Add(blockAtBoot ? "setboot-on" : "setboot-off");
        Status = Status with { BlockAtBoot = blockAtBoot };
        return Task.FromResult(ControllerResult.Ok("Block at boot is " + (blockAtBoot ? "on" : "off")));
    }

    public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default)
    {
        Calls.Add("set-device:" + address12);
        Trace?.Add("set-device");
        return Task.FromResult(ControllerResult.Ok("Device chosen"));
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
}

internal sealed class FakeProtection : IAudioProtectionController
{
    public AudioProtectionState State { get; set; } = AudioProtectionState.NotProtected;

    public bool? Pending { get; set; }

    public Exception? StatusFailure { get; set; }

    public List<bool> Applies { get; } = new();

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

        return Task.FromResult(new AudioProtectionSnapshot(
            State,
            HandsfreeInstalled: State is AudioProtectionState.NotProtected or AudioProtectionState.Partial,
            HeadsetInstalled: false));
    }

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default)
    {
        Applies.Add(protect);
        Trace?.Add(protect ? "protect-on" : "protect-off");
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

    public int Hides { get; private set; }

    public List<string> Statuses => Shown.Select(s => s.Content.Status).ToList();

    public void Show(CardContent content, CardAnchor anchor) => Shown.Add(new CardShown(content, anchor, null));

    public void Show(CardContent content, CardAnchor anchor, Point clickPoint) =>
        Shown.Add(new CardShown(content, anchor, clickPoint));

    public void Hide() => Hides++;
}
