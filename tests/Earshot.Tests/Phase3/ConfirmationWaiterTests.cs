using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tests.Phase2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;
using static Earshot.Tests.Phase3.ConnectFixtures;

namespace Earshot.Tests.Phase3;

// The wait for an observed state change: armed before the refresh, so a change on either side of arming is seen;
// evidence older than the request ignored; a timeout on the injected clock through CancelAfter; cancellation that
// throws; and the SnapshotChanged handler and timer released on every path.
[TestClass]
public sealed class ConfirmationWaiterTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = ConfirmationWaiter.TimeoutFor(ConnectAction.Connect);

    private ManualTimeProvider _time = null!;
    private FakeDeviceMonitor _monitor = null!;
    private CapturingLog _log = null!;
    private ConfirmationWaiter _waiter = null!;

    [TestInitialize]
    public void Setup()
    {
        _time = new ManualTimeProvider();
        _monitor = new FakeDeviceMonitor(_time);
        _log = new CapturingLog();
        _waiter = new ConfirmationWaiter(_monitor, _log, _time);
    }

    public void Dispose() => _monitor?.Dispose();

    [TestCleanup]
    public void Cleanup()
    {
        Assert.AreEqual(0, _monitor.Handlers, "The SnapshotChanged handler is removed on every path.");
        Assert.AreEqual(_monitor.Subscribed, _monitor.Unsubscribed);
        Assert.AreEqual(0, _time.LiveTimers, "The timeout timer is disposed on every path.");
    }

    private Task<ConfirmationResult> Wait(ConnectAction action, DateTimeOffset requested, CancellationToken ct = default) =>
        _waiter.WaitAsync(AirPodsContainer, action, ConfirmationWaiter.TimeoutFor(action), requested, ct);

    private async Task Armed() => await Eventually.True(() => _time.ArmedTimers == 1 && _monitor.RefreshCalls == 1, "the wait to arm and refresh");

    [TestMethod]
    public async Task AStateReachedBeforeArmingCompletesFromTheRefresh()
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Active), Capture(EndpointState.Active));

        ConfirmationResult result = await Wait(ConnectAction.Connect, requested).WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(ConfirmationSource.Refresh, result.Source);
        Assert.AreEqual(1, _monitor.HandlersAtFirstRefresh, "The handler is subscribed before the refresh runs.");
        Assert.AreEqual(1, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task ANotificationBetweenArmingAndTheRefreshResultIsNotLost()
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        _monitor.OnRefresh = _ =>
        {
            // The enumeration ran just before the device connected; the notification arrives before the refresh
            // hands its (already out of date) snapshot back.
            DeviceSnapshot enumerated = _monitor.Snapshot();
            _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
            return Task.FromResult(enumerated);
        };

        ConfirmationResult result = await Wait(ConnectAction.Connect, requested).WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(ConfirmationSource.Notification, result.Source);
        Assert.AreEqual(1, result.Notifications);
    }

    [TestMethod]
    public async Task ANotificationAfterTheRefreshCompletesTheWait()
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();
        Assert.IsFalse(waiting.IsCompleted);

        int raisingThread = Environment.CurrentManagedThreadId;
        ApartmentState raisingApartment = Thread.CurrentThread.GetApartmentState();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active), Capture(EndpointState.Unplugged)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(ConfirmationSource.Notification, result.Source);
        Assert.AreEqual(raisingThread, result.ThreadId, "The thread that delivered the event is recorded.");
        Assert.AreEqual(raisingApartment, result.Apartment);
    }

    [TestMethod]
    [DataRow(EndpointState.Unplugged)]
    [DataRow(EndpointState.NotPresent)]
    public async Task ADisconnectIsReachedWhenTheRenderEndpointLeavesActive(EndpointState state)
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Active));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Disconnect, requested);
        await Armed();

        _monitor.Raise(_monitor.Snapshot(Render(state)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
    }

    [TestMethod]
    public async Task ASnapshotTakenBeforeTheRequestIsIgnored()
    {
        DeviceSnapshot stale = _monitor.Snapshot(Render(EndpointState.Active));
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        // Published before the request, delivered late through the UI queue.
        _monitor.Raise(stale);
        Assert.IsFalse(waiting.IsCompleted, "Old evidence never confirms.");
        _time.Advance(ConnectTimeout);
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.AreEqual(1, result.StaleIgnored);
        Assert.AreEqual(EndpointState.Unplugged, ConfirmationWaiter.EndpointsOf(result.LastObserved!, AirPodsContainer).Single().State);
    }

    [TestMethod]
    public async Task ARefreshThatHandsBackAnOlderSnapshotIsIgnored()
    {
        DeviceSnapshot previous = _monitor.Snapshot(Render(EndpointState.Active));
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.OnRefresh = _ => Task.FromResult(previous);   // an enumeration that failed keeps the old snapshot
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _time.Advance(ConnectTimeout);
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsNull(result.LastObserved, "Nothing taken after the request was seen.");
        Assert.AreEqual(1, result.StaleIgnored);
    }

    [TestMethod]
    public async Task NoChangeTimesOutOnTheInjectedClock()
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _time.Advance(ConnectTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.IsFalse(waiting.IsCompleted, "Not before the budget is spent.");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.AreEqual(ConfirmationSource.None, result.Source);
        Assert.IsNotNull(result.LastObserved);
        Assert.IsTrue(result.RenderEndpointObserved);
        Assert.IsGreaterThanOrEqualTo(ConnectTimeout, result.Elapsed);
        Assert.IsNull(result.ThreadId);
    }

    [TestMethod]
    public async Task ARenderEndpointThatHasGoneIsReportedAtTheTimeout()
    {
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Capture(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _time.Advance(ConnectTimeout);
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsNotNull(result.LastObserved);
        Assert.IsFalse(result.RenderEndpointObserved);
    }

    [TestMethod]
    public async Task AnotherDevicesStateNeverCounts()
    {
        var phoneRender = new AudioEndpoint("{0.0.0.00000000}.{44444444-5555-4666-8777-888888888802}", EndpointFlow.Render,
            EndpointState.Active, "Headphones (iPhone)", IPhoneContainer);
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged), phoneRender);
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Unplugged), phoneRender));
        _time.Advance(ConnectTimeout);
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
    }

    [TestMethod]
    public async Task CancellationEndsTheWaitAndThrows()
    {
        using var cts = new CancellationTokenSource();
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested, cts.Token);
        await Armed();

        await cts.CancelAsync();
        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiting.WaitAsync(Guard));

        Assert.AreEqual(cts.Token, error.CancellationToken);
    }

    [TestMethod]
    public async Task ATokenAlreadyCancelledNeverArms()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Wait(ConnectAction.Connect, _time.GetUtcNow(), cts.Token));

        Assert.AreEqual(0, _monitor.Subscribed);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task AFailedRefreshFailsTheWait()
    {
        _monitor.OnRefresh = _ => Task.FromException<DeviceSnapshot>(new ObjectDisposedException("monitor"));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => Wait(ConnectAction.Disconnect, _time.GetUtcNow()).WaitAsync(Guard));
    }

    [TestMethod]
    public async Task ARefreshThatFailsAfterTheStateWasSeenIsLogged()
    {
        var refresh = new TaskCompletionSource<DeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset requested = _time.GetUtcNow();
        _monitor.OnRefresh = _ =>
        {
            _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
            return refresh.Task;
        };

        ConfirmationResult result = await Wait(ConnectAction.Connect, requested).WaitAsync(Guard);
        refresh.SetException(new InvalidOperationException("late failure"));

        Assert.IsTrue(result.Reached);
        await Eventually.True(() => _log.Has(LogLevel.Error, "after the wait for the device state had ended"), "the late refresh failure to be logged");
    }

    [TestMethod]
    public void TheWantedStatesFollowTheRenderEndpoint()
    {
        AudioEndpoint secondRender = Render(EndpointState.Unplugged) with { EndpointId = "{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516971}" };

        Assert.IsTrue(ConfirmationWaiter.IsReached([Render(EndpointState.Active)], ConnectAction.Connect));
        Assert.IsTrue(ConfirmationWaiter.IsReached([Render(EndpointState.Active), secondRender], ConnectAction.Connect));
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Unplugged), Capture(EndpointState.Active)], ConnectAction.Connect),
            "An active capture endpoint alone is not a connect.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Disabled)], ConnectAction.Connect));
        Assert.IsFalse(ConfirmationWaiter.IsReached([], ConnectAction.Connect));

        Assert.IsTrue(ConfirmationWaiter.IsReached([Render(EndpointState.Unplugged)], ConnectAction.Disconnect));
        Assert.IsTrue(ConfirmationWaiter.IsReached([Render(EndpointState.NotPresent), Capture(EndpointState.NotPresent)], ConnectAction.Disconnect));
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Active), secondRender], ConnectAction.Disconnect),
            "Any render endpoint still active is not a disconnect.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Disabled)], ConnectAction.Disconnect));
        Assert.IsFalse(ConfirmationWaiter.IsReached([Capture(EndpointState.Unplugged)], ConnectAction.Disconnect),
            "Without a render endpoint nothing was observed.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([], ConnectAction.Disconnect));
    }

    [TestMethod]
    public void TheTimeoutsAreNamedBudgets()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(15), ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        Assert.AreEqual(TimeSpan.FromSeconds(12), ConfirmationWaiter.TimeoutFor(ConnectAction.Disconnect));
    }
}
