using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase3;

// The adapter ids and endpoints of the owner's machine, as the read-only probe saw them, plus the paired phone's
// Hands-Free filter.
internal static class ConnectFixtures
{
    public const string SrcAdapter =
        "{2}.\\\\?\\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&2027#b&1a2b3c4d&0&0a1b2c3d4e8c_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\src";

    public const string WaveAdapter =
        "{2}.\\\\?\\bthhfenum#bthhfpaudio#c&2b3c4d5e&1&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";

    public const string PhoneAdapter =
        "{2}.\\\\?\\bthhfenum#bthhfpaudio#c&4d5e6f7&0&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int EFail = unchecked((int)0x80004005);

    public static AudioEndpoint Render(EndpointState state) => AirPodsRender(state).Endpoint;

    public static AudioEndpoint Capture(EndpointState state) => AirPodsCapture(state).Endpoint;

    public static AudioEndpoint Phone() => IPhoneHandsFree().Endpoint;
}

// A clock the test moves by hand. Each GetUtcNow moves it on by one tick, so two readings are never equal, as
// with the precise system clock. Timers fire only from Advance.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _ticks = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ArmedTimers
    {
        get { lock (_gate) { return _timers.Count(t => t.DueTicks >= 0); } }
    }

    public int LiveTimers
    {
        get { lock (_gate) { return _timers.Count; } }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            _ticks++;
            return new DateTimeOffset(_ticks, TimeSpan.Zero);
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _ticks += by.Ticks;
            due = _timers.Where(t => t.DueTicks >= 0 && t.DueTicks <= _ticks).ToList();
            foreach (ManualTimer timer in due)
            {
                timer.DueTicks = -1;
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueTicks { get; set; } = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period, "Only one-shot timers are used.");
            lock (owner._gate)
            {
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? -1 : owner._ticks + dueTime.Ticks;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// A device monitor whose state the test sets. Counts SnapshotChanged subscriptions, so a test can check the
// handler was added before the refresh and removed on every path.
internal sealed class FakeDeviceMonitor(TimeProvider time) : IDeviceMonitor
{
    private readonly Lock _gate = new();
    private EventHandler<DeviceSnapshotEventArgs>? _handlers;
    private List<AudioEndpoint> _endpoints = new();
    private int _refreshCalls;
    private int _handlersAtFirstRefresh = -1;
    private long _sequence;

    public event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged
    {
        add
        {
            lock (_gate)
            {
                _handlers += value;
                Subscribed++;
            }
        }

        remove
        {
            lock (_gate)
            {
                _handlers -= value;
                Unsubscribed++;
            }
        }
    }

    public int Subscribed { get; private set; }

    public int Unsubscribed { get; private set; }

    public int Handlers
    {
        get { lock (_gate) { return _handlers?.GetInvocationList().Length ?? 0; } }
    }

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    // How many handlers were subscribed when RefreshAsync was first called, or -1 when it never was.
    public int HandlersAtFirstRefresh => Volatile.Read(ref _handlersAtFirstRefresh);

    // Replaces the default refresh (a snapshot of the current endpoints taken now).
    public Func<CancellationToken, Task<DeviceSnapshot>>? OnRefresh { get; set; }

    public DeviceSnapshot Current => Snapshot();

    // The number of the last snapshot this monitor made, as the real monitor numbers its enumerations.
    public long Sequence => Interlocked.Read(ref _sequence);

    public void SetEndpoints(params AudioEndpoint[] endpoints)
    {
        lock (_gate)
        {
            _endpoints = endpoints.ToList();
        }
    }

    // A snapshot of the given endpoints (or the current ones) taken now.
    public DeviceSnapshot Snapshot(params AudioEndpoint[] endpoints)
    {
        List<AudioEndpoint> list;
        lock (_gate)
        {
            list = endpoints.Length > 0 ? endpoints.ToList() : _endpoints.ToList();
        }

        return EndpointModelBuilder.Build(list, "AirPods", AirPodsContainer, time.GetUtcNow()).Snapshot with
        {
            Sequence = Interlocked.Increment(ref _sequence),
        };
    }

    public void Raise(DeviceSnapshot snapshot)
    {
        EventHandler<DeviceSnapshotEventArgs>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        handlers?.Invoke(this, new DeviceSnapshotEventArgs(snapshot));
    }

    public void Start()
    {
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        Interlocked.CompareExchange(ref _handlersAtFirstRefresh, Handlers, -1);
        Interlocked.Increment(ref _refreshCalls);
        return OnRefresh is { } refresh ? refresh(ct) : Task.FromResult(Snapshot());
    }

    public bool WatchFailed => false;

    public void Dispose()
    {
    }
}

// One filter the fake path reports: its adapter, and the HRESULT of the request, or null when the guard kept
// the request from being sent.
internal sealed record FakeFilter(string AdapterId, AudioEndpoint From, int? Hr, int? ActivateHr = null);

// A connect path with no Core Audio behind it. Records the thread and arguments of every call.
internal sealed class FakeConnectPath : IKsConnectPath
{
    private readonly Lock _gate = new();
    private readonly List<(Guid Container, ConnectAction Action, FilterChoice Choice, int ThreadId)> _sends = new();
    private readonly List<int> _readThreads = new();

    public List<AudioEndpoint> Endpoints { get; set; } = new();

    public IReadOnlyList<StepOutcome> ReadSteps { get; set; } = Array.Empty<StepOutcome>();

    public bool ReadOk { get; set; } = true;

    public List<FakeFilter> Filters { get; set; } = new();

    // Runs inside ReadEndpoints, on the worker, before it returns.
    public Action? DuringRead { get; set; }

    // Runs inside Send, on the worker, after the requests are recorded.
    public Action? DuringSend { get; set; }

    // Runs inside Send after each filter, with its index: a test cancels here to stop the walk before the next.
    public Action<int>? AfterFilter { get; set; }

    // Reported as the send's Fault: the walk stopped with this exception after the filters above.
    public Exception? Fault { get; set; }

    public IReadOnlyList<(Guid Container, ConnectAction Action, FilterChoice Choice, int ThreadId)> Sends
    {
        get { lock (_gate) { return _sends.ToArray(); } }
    }

    public IReadOnlyList<int> ReadThreads
    {
        get { lock (_gate) { return _readThreads.ToArray(); } }
    }

    public EndpointRead ReadEndpoints(Guid container)
    {
        lock (_gate)
        {
            _readThreads.Add(Environment.CurrentManagedThreadId);
        }

        DuringRead?.Invoke();
        if (!ReadOk)
        {
            return EndpointRead.Failed(ReadSteps);
        }

        var enumeration = new EndpointEnumeration(true, Endpoints.Select(EndpointReading.Of).ToList(), ReadSteps);
        return EndpointRead.From(enumeration, container);
    }

    // Cancelled before a filter: it is recorded as sent nothing, as the real path does.
    public KsSendResult Send(Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _sends.Add((container, action, choice, Environment.CurrentManagedThreadId));
        }

        string prefix = KsConnectPath.StepPrefix(action);
        var steps = new List<StepOutcome>();
        var sends = new List<FilterSend>();
        foreach (FakeFilter filter in Filters)
        {
            var adapter = new AdapterPath(filter.AdapterId, new[] { filter.From });
            string name = KsConnectPath.FilterName(filter.AdapterId);
            FilterRole role = KsConnectPath.RoleOf(adapter);
            string label = KsConnectPath.RoleName(role) + ": " + filter.AdapterId;
            if (ct.IsCancellationRequested)
            {
                const string Cancelled = "the request was cancelled before this filter.";
                StepOutcome skipped = StepOutcomes.NotAttempted(prefix + ":" + name, label + ": no request was sent because " + Cancelled);
                steps.Add(skipped);
                sends.Add(new FilterSend(adapter, name, role, new FilterVisit(adapter, null, null, false, false, []), null, 0, null, null, Cancelled, skipped));
                continue;
            }

            var visitSteps = new List<StepOutcome>();
            if (filter.ActivateHr is int activateHr)
            {
                visitSteps.Add(StepOutcomes.FromHResult(TopologyWalk.ActivateControlStep + ":" + filter.AdapterId, activateHr));
            }

            bool sent = filter.Hr is not null;
            bool activated = filter.ActivateHr is null;
            var visit = new FilterVisit(adapter, EndpointState.Active, container, true, activated, visitSteps);
            string? reason = sent ? null : activated ? "the guard did not pass." : "IKsControl could not be activated.";
            StepOutcome step = filter.Hr is int hr
                ? StepOutcomes.FromHResult(prefix + ":" + name, hr, detail: label, ok: hr == 0)
                : StepOutcomes.NotAttempted(prefix + ":" + name, label + ": no request was sent because " + reason);
            steps.AddRange(visitSteps);
            steps.Add(step);
            sends.Add(new FilterSend(adapter, name, role, visit, filter.Hr, 0, sent ? DateTimeOffset.UtcNow : null,
                sent ? TimeSpan.FromMilliseconds(1) : null, reason, step));
            AfterFilter?.Invoke(sends.Count - 1);
        }

        DuringSend?.Invoke();
        var result = new KsSendResult(Filters.Select(f => new AdapterPath(f.AdapterId, new[] { f.From })).ToList(), sends, steps, Fault);
        lock (_gate)
        {
            _lastSend = result;
        }

        return result;
    }

    private KsSendResult? _lastSend;

    // The result of the latest Send, including one the controller stopped waiting for.
    public KsSendResult? LastSend
    {
        get { lock (_gate) { return _lastSend; } }
    }
}

// Answers each request with the HRESULT set for its control, or throws the exception set for it, and records the
// order the controls were reached in.
internal sealed class FakeKsSender : IKsPropertySender
{
    private readonly Dictionary<object, int> _hrByControl = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Exception> _throwByControl = new(ReferenceEqualityComparer.Instance);

    public List<(object Control, ConnectAction Action, KsPayload Payload)> Calls { get; } = new();

    public void Answer(object control, int hr) => _hrByControl[control] = hr;

    public void Throw(object control, Exception error) => _throwByControl[control] = error;

    // Runs inside each call, after it is recorded: a test can move a manual clock to time the call.
    public Action? OnSend { get; set; }

    public int Send(IKsControl control, ConnectAction action, KsPayload payload, out uint bytesReturned)
    {
        Calls.Add((control, action, payload));
        OnSend?.Invoke();
        if (_throwByControl.TryGetValue(control, out Exception? error))
        {
            throw error;
        }

        bytesReturned = 0;
        return _hrByControl.TryGetValue(control, out int hr) ? hr : 0;
    }
}

// An adapter whose GetState throws, as a released wrapper does during a device transition. Every other member is
// the ordinary fake's.
internal sealed class ThrowingStateDevice(ReleaseLedger ledger, string id, Exception error) : FakeDevice(ledger, id), IMMDevice
{
    int IMMDevice.GetState(out uint pdwState)
    {
        pdwState = 0;
        throw error;
    }
}

internal sealed record KsCall(Guid Set, uint Id, uint Flags, uint PropertyLength, nint PropertyData, uint DataLength, int? BufferValue);

// An IKsControl called directly (no COM wrapper), recording every argument of every call and, when a data buffer
// of at least four bytes is passed, the value it held.
internal sealed class RecordingKsControl : IKsControl
{
    public int Hr { get; set; }

    public List<KsCall> Properties { get; } = new();

    public int MethodsAndEvents { get; private set; }

    public int KsProperty(ref KSIDENTIFIER property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
    {
        int? value = propertyData != 0 && dataLength >= sizeof(int) ? System.Runtime.InteropServices.Marshal.ReadInt32(propertyData) : null;
        Properties.Add(new KsCall(property.Set, property.Id, property.Flags, propertyLength, propertyData, dataLength, value));
        bytesReturned = 0;
        return Hr;
    }

    public int KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
    {
        MethodsAndEvents++;
        bytesReturned = 0;
        return FakeHr.ENotImpl;
    }

    public int KsEvent(ref KSIDENTIFIER eventProperty, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
    {
        MethodsAndEvents++;
        bytesReturned = 0;
        return FakeHr.ENotImpl;
    }
}

// The owner's machine as Core Audio fakes: the AirPods render endpoint leads to the A2DP filter, the capture
// endpoint to the Hands-Free filter, and the phone's endpoint to its own Hands-Free filter in its own container.
// Every object is lent through the ledger, and the path releases through it.
internal sealed class FakeAudioMachine
{
    public FakeAudioMachine()
    {
        Ledger = new ReleaseLedger();
        Enumerator = new FakeEnumerator(Ledger);
        Collection = new FakeCollection(Ledger);
        Enumerator.Collection = Collection;
    }

    public ReleaseLedger Ledger { get; }

    public FakeEnumerator Enumerator { get; }

    public FakeCollection Collection { get; }

    public List<FakeConnector> Connectors { get; } = new();

    public int EnumeratorHr { get; set; }

    public FakeEndpointDevice AddEndpoint(AudioEndpoint endpoint, string? adapterId, string? name = null)
    {
        var connector = new FakeConnector { AdapterId = adapterId };
        Connectors.Add(connector);
        var store = new FakePropertyStore().Set(CoreAudio.PKEY_Device_ContainerId, endpoint.ContainerId);
        if (name is not null)
        {
            store.Set(CoreAudio.PKEY_Device_FriendlyName, name);
        }

        var device = new FakeEndpointDevice(Ledger, endpoint.EndpointId)
        {
            State = (uint)endpoint.State,
            DataFlow = endpoint.Flow == EndpointFlow.Render ? CoreAudio.eRender : CoreAudio.eCapture,
            Store = store,
            Topology = new FakeTopology(Ledger) { Connector = connector },
        };
        Enumerator.Add(device);
        Collection.Items.Add((FakeHr.SOk, device));
        return device;
    }

    public FakeDevice AddAdapter(string adapterId, Guid container, uint state = CoreAudio.DEVICE_STATE_ACTIVE)
    {
        var device = new FakeDevice(Ledger, adapterId)
        {
            State = state,
            Store = new FakePropertyStore().Set(CoreAudio.PKEY_Device_ContainerId, container),
            Control = new FakeKsControl(),
        };
        Enumerator.Devices[adapterId] = device;
        return device;
    }

    public FakeDevice Adapter(string adapterId) => Enumerator.Devices[adapterId];

    // The owner's machine with the AirPods in the given render state.
    public static FakeAudioMachine Owner(EndpointState render = EndpointState.Unplugged, EndpointState capture = EndpointState.Unplugged)
    {
        var machine = new FakeAudioMachine();
        machine.AddEndpoint(ConnectFixtures.Phone(), ConnectFixtures.PhoneAdapter);
        machine.AddEndpoint(ConnectFixtures.Capture(capture), ConnectFixtures.WaveAdapter);
        machine.AddEndpoint(ConnectFixtures.Render(render), ConnectFixtures.SrcAdapter);
        machine.AddAdapter(ConnectFixtures.SrcAdapter, AirPodsContainer);
        machine.AddAdapter(ConnectFixtures.WaveAdapter, AirPodsContainer);
        machine.AddAdapter(ConnectFixtures.PhoneAdapter, IPhoneContainer);
        return machine;
    }

    public KsConnectPath Path(IKsPropertySender sender, TimeProvider? time = null) =>
        new(EnumeratorOf, sender, Ledger.Release, time ?? TimeProvider.System);

    // requestsReachControls: the test used the real sender, whose requests land on the fake controls.
    public void AssertNothingChanged(bool requestsReachControls = false)
    {
        Ledger.AssertBalanced();
        Assert.IsTrue(Connectors.All(c => c.TopologyEdits == 0), "IConnector.ConnectTo or Disconnect was called.");
        foreach (FakeDevice device in Enumerator.Devices.Values)
        {
            Assert.IsTrue(device.Store is null || device.Store.Writes == 0, "A property store was written.");
            Assert.IsTrue(device.Control is null || device.Control.MethodsAndEvents == 0, "A KS method or event was sent.");
            Assert.IsTrue(requestsReachControls || device.Control is null || device.Control.Properties.Count == 0,
                "A request reached a fake control directly; every request must go through the sender.");
        }
    }

    private int EnumeratorOf(out IMMDeviceEnumerator? enumerator)
    {
        enumerator = EnumeratorHr < 0 ? null : Enumerator;
        return EnumeratorHr;
    }
}
