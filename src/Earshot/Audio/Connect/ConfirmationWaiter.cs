using Earshot.Contracts;

namespace Earshot.Audio.Connect;

internal enum ConfirmationSource
{
    None,          // the wait ran to its timeout or was stopped
    Refresh,       // the refresh forced right after arming
    Notification,  // a SnapshotChanged event
}

// How one wait for a state change ended.
//   Reached                 the container's endpoints were seen in the wanted state
//   Unreachable             the wait ended early: the newest snapshot taken after the request showed that the
//                           wanted state could no longer come (IsUnreachable)
//   Source                  what showed the state, or what showed it could not come
//   Elapsed                 from arming to that observation, or to the end of the wait
//   LastObserved            the newest snapshot taken after the request (the one the wait ended on, when it ended
//                           on one), or null when nothing taken after the request was seen
//   RenderEndpointObserved  LastObserved had at least one render endpoint in the container
//   Notifications           SnapshotChanged events received while armed
//   StaleIgnored            snapshots ignored because they were taken before the request
//   ThreadId, Apartment     the thread that delivered the observation the wait ended on, or null at the timeout
internal sealed record ConfirmationResult(
    bool Reached,
    bool Unreachable,
    ConfirmationSource Source,
    TimeSpan Elapsed,
    DeviceSnapshot? LastObserved,
    bool RenderEndpointObserved,
    int Notifications,
    int StaleIgnored,
    int? ThreadId,
    ApartmentState? Apartment);

// Waits for the device's render endpoint to reach the state a request asked for. A KSPROPERTY_ONESHOT_*
// S_OK means only that the driver attempted the change, and the Hands-Free driver completes it
// asynchronously, so success is an observed endpoint state, never an HRESULT.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect
// https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kernel-streaming-considerations
//
// Wanted state, from the container's endpoints:
//   connect      any render endpoint ACTIVE (only ACTIVE endpoints can stream)
//   disconnect   at least one render endpoint, none ACTIVE, one UNPLUGGED or NOTPRESENT, and no capture endpoint
//                ACTIVE (an ACTIVE Hands-Free capture endpoint is a live link)
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
//
// Unreachable. A snapshot taken after the request can show that the wanted state cannot come: the container has
// no render endpoint any more, or, for a connect, no render endpoint that could become ACTIVE (every one
// NOTPRESENT, as when its adapter is removed or disabled, or DISABLED in Sound settings), or, for a disconnect,
// every render endpoint DISABLED, which hides the link state. When the newest snapshot seen shows that, the wait
// ends at once rather than at the timeout. A snapshot holding a render endpoint whose container could not be
// read never ends a wait this way (MayHideTheDevice).
//
// Race safety. The wait is armed first (a TaskCompletionSource plus the SnapshotChanged subscription), and
// only then is the monitor refreshed. A state reached before arming shows in that refresh and completes the
// wait at once; one reached after arming shows in the refresh or in an event. Either way no change can fall
// between the two and turn into a false timeout.
//
// Stale evidence. SnapshotChanged is delivered through the UI post, so an event published before the request
// can still arrive after arming. Only snapshots the monitor numbered above requestedSequence count, and only
// snapshots of a read that worked (SnapshotReadStatus.Ok): a failed read keeps the devices and the number of
// the last enumeration that worked, so it is never taken as evidence either way. The caller reads the number on
// the audio worker at the start of the work item that reads the endpoints and sends; the monitor numbers its
// enumerations on that same worker, one item at a time, so every snapshot above that number was enumerated
// after the request. A rebuild after a settings change keeps the number of the enumeration it was built from,
// so it can never look newer than it is, and a clock step during the operation changes nothing.
//
// Timeout and cancellation. The timeout is a CancellationTokenSource with CancelAfter; the caller's token is
// linked to it. Either ends the wait only: a request already sent is never recalled. The timeout returns a
// result; cancellation by the caller's token throws OperationCanceledException. The SnapshotChanged handler
// is removed on every path, including a failed refresh.
internal sealed class ConfirmationWaiter
{
    // Waiting budgets, not figures shown to anyone. Microsoft documents no latency for these requests; the
    // owner's live tests time the real change and these are revisited then.
    internal const int ConnectTimeoutMilliseconds = 15_000;
    internal const int DisconnectTimeoutMilliseconds = 12_000;

    private readonly IDeviceMonitor _monitor;
    private readonly ILog _log;
    private readonly TimeProvider _time;

    public ConfirmationWaiter(IDeviceMonitor monitor, ILog log)
        : this(monitor, log, TimeProvider.System)
    {
    }

    internal ConfirmationWaiter(IDeviceMonitor monitor, ILog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _monitor = monitor;
        _log = log;
        _time = time;
    }

    public static TimeSpan TimeoutFor(ConnectAction action) => action switch
    {
        ConnectAction.Connect => TimeSpan.FromMilliseconds(ConnectTimeoutMilliseconds),
        ConnectAction.Disconnect => TimeSpan.FromMilliseconds(DisconnectTimeoutMilliseconds),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static bool IsReached(IEnumerable<AudioEndpoint> endpoints, ConnectAction action)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        List<AudioEndpoint> all = endpoints.ToList();
        List<AudioEndpoint> render = all.Where(e => e.Flow == EndpointFlow.Render).ToList();
        return action switch
        {
            ConnectAction.Connect => render.Any(e => e.State == EndpointState.Active),
            ConnectAction.Disconnect => render.Count > 0 &&
                                        render.All(e => e.State != EndpointState.Active) &&
                                        render.Any(e => e.State is EndpointState.Unplugged or EndpointState.NotPresent) &&
                                        !all.Any(e => e.Flow == EndpointFlow.Capture && e.State == EndpointState.Active),
            _ => false,
        };
    }

    // True when the endpoints show that the wanted state can no longer come, or can no longer be seen: no render
    // endpoint; for a connect, every render endpoint NOTPRESENT or DISABLED (only a present, enabled endpoint can
    // become ACTIVE); for a disconnect, every render endpoint DISABLED. A render endpoint turned off in Sound
    // settings reports DISABLED whether or not the link is up, so it can never show UNPLUGGED, and the capture
    // side alone cannot stand in for it: with Protect audio quality on there is no Hands-Free capture endpoint at
    // all. Nothing is then claimed either way.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
    public static bool IsUnreachable(IEnumerable<AudioEndpoint> endpoints, ConnectAction action)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        List<AudioEndpoint> render = endpoints.Where(e => e.Flow == EndpointFlow.Render).ToList();
        return render.Count == 0 || action switch
        {
            ConnectAction.Connect => render.All(e => e.State is EndpointState.NotPresent or EndpointState.Disabled),
            ConnectAction.Disconnect => render.All(e => e.State == EndpointState.Disabled),
            _ => false,
        };
    }

    // True when the snapshot holds a render endpoint whose container could not be read (the builder groups
    // those under Guid.Empty). One of them could be the device's, so an empty-looking container is not proof
    // that the device went: the wait then runs to its timeout rather than report it gone.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids
    internal static bool MayHideTheDevice(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.AllGroups.Any(g => g.ContainerId == Guid.Empty && g.Endpoints.Any(e => e.Flow == EndpointFlow.Render));
    }

    // The container's endpoints in a snapshot, or none when the container is not in it.
    public static IReadOnlyList<AudioEndpoint> EndpointsOf(DeviceSnapshot snapshot, Guid container)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DeviceModel? group = snapshot.AllGroups.FirstOrDefault(g => g.ContainerId == container) ??
                             (snapshot.Target?.ContainerId == container ? snapshot.Target : null);
        return group?.Endpoints ?? Array.Empty<AudioEndpoint>();
    }

    // The monitor's enumeration number now. Read on the audio worker at the start of a send, so every later
    // enumeration carries a higher number.
    public long CurrentSequence => _monitor.Current.Sequence;

    public async Task<ConfirmationResult> WaitAsync(
        Guid container, ConnectAction action, TimeSpan timeout, long requestedSequence, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var wait = new Wait(container, action, requestedSequence, _time, _log);
        EventHandler<DeviceSnapshotEventArgs> handler = wait.OnSnapshotChanged;

        // 1. Arm.
        _monitor.SnapshotChanged += handler;
        try
        {
            using var timeoutSource = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
            using CancellationTokenRegistration stop = linked.Token.Register(static state => ((Wait)state!).Stop(), wait);
            timeoutSource.CancelAfter(timeout);

            // 2. Refresh. A state reached before arming completes the wait here.
            wait.Follow(_monitor.RefreshAsync(linked.Token));

            Observation? ending = await wait.Completion.ConfigureAwait(false);
            if (ending is null && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The wait for the device state was cancelled.", ct);
            }

            return wait.Result(ending);
        }
        finally
        {
            _monitor.SnapshotChanged -= handler;
        }
    }

    // The snapshot a wait ended on: Reached when it showed the wanted state, otherwise it showed the state unreachable.
    private sealed record Observation(DeviceSnapshot Snapshot, bool Reached, ConfirmationSource Source, long Timestamp, int ThreadId, ApartmentState Apartment);

    private sealed class Wait
    {
        private readonly Guid _container;
        private readonly ConnectAction _action;
        private readonly long _requestedSequence;
        private readonly TimeProvider _time;
        private readonly ILog _log;
        private readonly long _armed;
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource<Observation?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DeviceSnapshot? _last;
        private bool _lastHasRender;
        private int _notifications;
        private int _stale;
        private long _stopped;

        public Wait(Guid container, ConnectAction action, long requestedSequence, TimeProvider time, ILog log)
        {
            _container = container;
            _action = action;
            _requestedSequence = requestedSequence;
            _time = time;
            _log = log;
            _armed = time.GetTimestamp();
        }

        // Completes with the observation the wait ended on, or with null when the wait was stopped.
        public Task<Observation?> Completion => _completion.Task;

        // Raised on the UI thread in the tray and on the audio worker in the console modes. Never blocks.
        public void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
        {
            Interlocked.Increment(ref _notifications);
            Observe(e.Snapshot, ConfirmationSource.Notification);
        }

        public void Follow(Task<DeviceSnapshot> refresh) =>
            refresh.ContinueWith(
                static (t, state) => ((Wait)state!).OnRefreshDone(t),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        public void Stop()
        {
            Interlocked.CompareExchange(ref _stopped, _time.GetTimestamp(), 0);
            _completion.TrySetResult(null);
        }

        public ConfirmationResult Result(Observation? ending)
        {
            DeviceSnapshot? last;
            bool lastHasRender;
            lock (_gate)
            {
                last = _last;
                lastHasRender = _lastHasRender;
            }

            long end = ending?.Timestamp ?? Interlocked.Read(ref _stopped);
            TimeSpan elapsed = _time.GetElapsedTime(_armed, end == 0 ? _time.GetTimestamp() : end);
            if (ending is null)
            {
                return new ConfirmationResult(false, false, ConfirmationSource.None, elapsed, last, lastHasRender,
                    Volatile.Read(ref _notifications), Volatile.Read(ref _stale), null, null);
            }

            bool hasRender = EndpointsOf(ending.Snapshot, _container).Any(e => e.Flow == EndpointFlow.Render);
            return new ConfirmationResult(ending.Reached, !ending.Reached, ending.Source, elapsed, ending.Snapshot, hasRender,
                Volatile.Read(ref _notifications), Volatile.Read(ref _stale), ending.ThreadId, ending.Apartment);
        }

        private void Observe(DeviceSnapshot? snapshot, ConfirmationSource source)
        {
            if (snapshot is null)
            {
                return;
            }

            if (snapshot.ReadStatus != SnapshotReadStatus.Ok || snapshot.Sequence <= _requestedSequence)
            {
                // Either the read failed (so the devices in it are the last known, not an observation) or it
                // was enumerated before the request.
                Interlocked.Increment(ref _stale);
                return;
            }

            IReadOnlyList<AudioEndpoint> endpoints = EndpointsOf(snapshot, _container);
            bool newest;
            lock (_gate)
            {
                newest = _last is null || snapshot.Sequence >= _last.Sequence;
                if (newest)
                {
                    _last = snapshot;
                    _lastHasRender = endpoints.Any(e => e.Flow == EndpointFlow.Render);
                }
            }

            if (IsReached(endpoints, _action))
            {
                End(snapshot, reached: true, source);
            }
            else if (newest && IsUnreachable(endpoints, _action) && !MayHideTheDevice(snapshot))
            {
                // Only the newest snapshot seen can end the wait this way; an older one is already out of date.
                End(snapshot, reached: false, source);
            }
        }

        private void End(DeviceSnapshot snapshot, bool reached, ConfirmationSource source) =>
            _completion.TrySetResult(new Observation(snapshot, reached, source, _time.GetTimestamp(),
                Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

        private void OnRefreshDone(Task<DeviceSnapshot> refresh)
        {
            if (refresh.IsCompletedSuccessfully)
            {
                Observe(refresh.Result, ConfirmationSource.Refresh);
                return;
            }

            if (refresh.Exception is AggregateException failure)
            {
                Exception error = failure.InnerExceptions.Count == 1 ? failure.InnerExceptions[0] : failure;
                if (!_completion.TrySetException(error))
                {
                    _log.Error("Refreshing the audio devices failed after the wait for the device state had ended.", error);
                }
            }

            // Cancelled: only this wait's tokens cancel the refresh, and cancelling them has already stopped it.
        }
    }
}
