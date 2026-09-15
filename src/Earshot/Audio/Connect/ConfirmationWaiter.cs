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
// NOTPRESENT, as when its adapter is removed or disabled, or DISABLED in Sound settings). When the newest
// snapshot seen shows that, the wait ends at once rather than at the timeout.
//
// Race safety. The wait is armed first (a TaskCompletionSource plus the SnapshotChanged subscription), and
// only then is the monitor refreshed. A state reached before arming shows in that refresh and completes the
// wait at once; one reached after arming shows in the refresh or in an event. Either way no change can fall
// between the two and turn into a false timeout.
//
// Stale evidence. SnapshotChanged is delivered through the UI post, so an event published before the request
// can still arrive after arming. Only snapshots whose TakenUtc is later than requestedUtc count. The caller
// stamps requestedUtc on the audio worker at the start of the work item that reads the endpoints and sends. The
// monitor builds and stamps its snapshots on that worker too, one item at a time, and after an enumeration it
// builds from that enumeration's readings, so the snapshot of an enumeration that ran before the request carries
// an earlier time. A refresh whose enumeration failed hands back the current snapshot, which is ignored the same
// way when it is older than the request. Two gaps remain, both outside this class:
//   - a settings change (DeviceMatch or PinnedContainerId) makes the monitor rebuild its snapshot from the last
//     readings it enumerated, stamped with the current time, so readings from before the request can arrive
//     with a later stamp;
//   - both times are wall-clock times, so a clock step while the operation runs can make a snapshot look newer
//     or older than it is.
// A monotonic enumeration sequence on the snapshot would close both.
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

    // True when the endpoints show that the wanted state can no longer come: no render endpoint, or, for a
    // connect, every render endpoint NOTPRESENT or DISABLED. Only an ACTIVE endpoint can stream, and only a present,
    // enabled endpoint can become ACTIVE.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
    public static bool IsUnreachable(IEnumerable<AudioEndpoint> endpoints, ConnectAction action)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        List<AudioEndpoint> render = endpoints.Where(e => e.Flow == EndpointFlow.Render).ToList();
        return render.Count == 0 ||
               (action == ConnectAction.Connect && render.All(e => e.State is EndpointState.NotPresent or EndpointState.Disabled));
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

            if (snapshot.TakenUtc <= _requestedUtc)
            {
                Interlocked.Increment(ref _stale);
                return;
            }

            IReadOnlyList<AudioEndpoint> endpoints = EndpointsOf(snapshot, _container);
            bool newest;
            lock (_gate)
            {
                newest = _last is null || snapshot.TakenUtc >= _last.TakenUtc;
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
            else if (newest && IsUnreachable(endpoints, _action))
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
