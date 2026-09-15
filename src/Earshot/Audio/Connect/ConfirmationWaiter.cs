using Earshot.Contracts;

namespace Earshot.Audio.Connect;

internal enum ConfirmationSource
{
    None,          // nothing reached the wanted state
    Refresh,       // the refresh forced right after arming
    Notification,  // a SnapshotChanged event
}

// How one wait for a state change ended.
//   Reached                 the container's render endpoint was seen in the wanted state
//   Source                  what showed it
//   Elapsed                 from arming to the observation, or to the end of the wait
//   LastObserved            the newest snapshot taken after the request (the one that reached the state, when
//                           reached), or null when nothing taken after the request was seen
//   RenderEndpointObserved  LastObserved had at least one render endpoint in the container
//   Notifications           SnapshotChanged events received while armed
//   StaleIgnored            snapshots ignored because they were taken before the request
//   ThreadId, Apartment     the thread that delivered the observation that reached the state
internal sealed record ConfirmationResult(
    bool Reached,
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
// Wanted state, from the container's render endpoints:
//   connect      any render endpoint ACTIVE (only ACTIVE endpoints can stream)
//   disconnect   at least one render endpoint, none ACTIVE, and one UNPLUGGED or NOTPRESENT
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
//
// Race safety. The wait is armed first (a TaskCompletionSource plus the SnapshotChanged subscription), and
// only then is the monitor refreshed. A state reached before arming shows in that refresh and completes the
// wait at once; one reached after arming shows in the refresh or in an event. Either way no change can fall
// between the two and turn into a false timeout.
//
// Stale evidence. SnapshotChanged is delivered through the UI post, so an event published before the request
// can still arrive after arming. Only snapshots taken after requestedUtc count. The caller stamps requestedUtc
// on the audio worker, in the same work item that reads the endpoints and sends; the monitor enumerates and
// stamps its snapshots on that worker too, one item at a time, so a snapshot taken before that item cannot
// carry a later time. A refresh whose enumeration failed returns the older snapshot and is ignored the same way.
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
        List<AudioEndpoint> render = endpoints.Where(e => e.Flow == EndpointFlow.Render).ToList();
        return action switch
        {
            ConnectAction.Connect => render.Any(e => e.State == EndpointState.Active),
            ConnectAction.Disconnect => render.Count > 0 &&
                                        render.All(e => e.State != EndpointState.Active) &&
                                        render.Any(e => e.State is EndpointState.Unplugged or EndpointState.NotPresent),
            _ => false,
        };
    }

    // The container's endpoints in a snapshot, or none when the container is not in it.
    public static IReadOnlyList<AudioEndpoint> EndpointsOf(DeviceSnapshot snapshot, Guid container)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DeviceModel? group = snapshot.AllGroups.FirstOrDefault(g => g.ContainerId == container) ??
                             (snapshot.Target?.ContainerId == container ? snapshot.Target : null);
        return group?.Endpoints ?? Array.Empty<AudioEndpoint>();
    }

    public async Task<ConfirmationResult> WaitAsync(
        Guid container, ConnectAction action, TimeSpan timeout, DateTimeOffset requestedUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var wait = new Wait(container, action, requestedUtc, _time, _log);
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

            Observation? reached = await wait.Completion.ConfigureAwait(false);
            if (reached is null && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The wait for the device state was cancelled.", ct);
            }

            return wait.Result(reached);
        }
        finally
        {
            _monitor.SnapshotChanged -= handler;
        }
    }

    private sealed record Observation(DeviceSnapshot Snapshot, ConfirmationSource Source, long Timestamp, int ThreadId, ApartmentState Apartment);

    private sealed class Wait
    {
        private readonly Guid _container;
        private readonly ConnectAction _action;
        private readonly DateTimeOffset _requestedUtc;
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

        public Wait(Guid container, ConnectAction action, DateTimeOffset requestedUtc, TimeProvider time, ILog log)
        {
            _container = container;
            _action = action;
            _requestedUtc = requestedUtc;
            _time = time;
            _log = log;
            _armed = time.GetTimestamp();
        }

        // Completes with the observation that reached the wanted state, or with null when the wait was stopped.
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

        public ConfirmationResult Result(Observation? reached)
        {
            DeviceSnapshot? last;
            bool lastHasRender;
            lock (_gate)
            {
                last = _last;
                lastHasRender = _lastHasRender;
            }

            long end = reached?.Timestamp ?? Interlocked.Read(ref _stopped);
            TimeSpan elapsed = _time.GetElapsedTime(_armed, end == 0 ? _time.GetTimestamp() : end);
            return reached is not null
                ? new ConfirmationResult(true, reached.Source, elapsed, reached.Snapshot, true,
                    Volatile.Read(ref _notifications), Volatile.Read(ref _stale), reached.ThreadId, reached.Apartment)
                : new ConfirmationResult(false, ConfirmationSource.None, elapsed, last, lastHasRender,
                    Volatile.Read(ref _notifications), Volatile.Read(ref _stale), null, null);
        }

        private void Observe(DeviceSnapshot? snapshot, ConfirmationSource source)
        {
            if (snapshot is null)
            {
                return;
            }

            if (snapshot.TakenUtc <= _requestedUtc)
            {
                Interlocked.Increment(ref _stale);
                return;
            }

            IReadOnlyList<AudioEndpoint> endpoints = EndpointsOf(snapshot, _container);
            lock (_gate)
            {
                if (_last is null || snapshot.TakenUtc >= _last.TakenUtc)
                {
                    _last = snapshot;
                    _lastHasRender = endpoints.Any(e => e.Flow == EndpointFlow.Render);
                }
            }

            if (IsReached(endpoints, _action))
            {
                _completion.TrySetResult(new Observation(snapshot, source, _time.GetTimestamp(),
                    Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
            }
        }

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
