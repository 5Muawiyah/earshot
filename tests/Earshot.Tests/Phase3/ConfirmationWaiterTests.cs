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

    private Task<ConfirmationResult> Wait(ConnectAction action, long requested, CancellationToken ct = default) =>
        _waiter.WaitAsync(AirPodsContainer, action, ConfirmationWaiter.TimeoutFor(action), requested, ct);

    private async Task Armed() => await Eventually.True(() => _time.ArmedTimers == 1 && _monitor.RefreshCalls == 1, "the wait to arm and refresh");

    [TestMethod]
    public async Task AStateReachedBeforeArmingCompletesFromTheRefresh()
    {
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _time.Advance(ConnectTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.IsFalse(waiting.IsCompleted, "Not before the budget is spent.");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsFalse(result.Unreachable);
        Assert.AreEqual(ConfirmationSource.None, result.Source);
        Assert.IsNotNull(result.LastObserved);
        Assert.IsTrue(result.RenderEndpointObserved);
        Assert.IsGreaterThanOrEqualTo(ConnectTimeout, result.Elapsed);
        Assert.IsNull(result.ThreadId);
    }

    [TestMethod]
    [DataRow((int)ConnectAction.Connect)]
    [DataRow((int)ConnectAction.Disconnect)]
    public async Task ARenderEndpointThatHasGoneEndsTheWaitWithoutTheTimeout(int action)
    {
        long requested = _monitor.Sequence;
        _monitor.SetEndpoints(Capture(EndpointState.Unplugged));

        ConfirmationResult result = await Wait((ConnectAction)action, requested).WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsTrue(result.Unreachable);
        Assert.AreEqual(ConfirmationSource.Refresh, result.Source);
        Assert.IsNotNull(result.LastObserved);
        Assert.IsFalse(result.RenderEndpointObserved);
        Assert.IsNotNull(result.ThreadId);
    }

    [TestMethod]
    [DataRow(EndpointState.NotPresent)]
    [DataRow(EndpointState.Disabled)]
    public async Task AConnectEndsWhenNoRenderEndpointCanBecomeActive(EndpointState state)
    {
        long requested = _monitor.Sequence;
        _monitor.SetEndpoints(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();
        Assert.IsFalse(waiting.IsCompleted);

        _monitor.Raise(_monitor.Snapshot(Render(state), Capture(EndpointState.Unplugged)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsTrue(result.Unreachable);
        Assert.AreEqual(ConfirmationSource.Notification, result.Source);
        Assert.AreEqual(state, ConfirmationWaiter.EndpointsOf(result.LastObserved!, AirPodsContainer).Single(e => e.Flow == EndpointFlow.Render).State);
    }

    // A render endpoint turned off in Sound settings during a disconnect hides the link state for the rest of the
    // wait, so the wait ends then rather than run to its timeout and blame the AirPods.
    [TestMethod]
    public async Task ADisconnectEndsWhenEveryRenderEndpointIsTurnedOff()
    {
        long requested = _monitor.Sequence;
        _monitor.SetEndpoints(Render(EndpointState.Active), Capture(EndpointState.Active));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Disconnect, requested);
        await Armed();
        Assert.IsFalse(waiting.IsCompleted);

        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Disabled), Capture(EndpointState.Unplugged)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached, "Nothing shows the link went.");
        Assert.IsTrue(result.Unreachable);
        Assert.AreEqual(ConfirmationSource.Notification, result.Source);
    }

    [TestMethod]
    public async Task AnOlderSnapshotWithoutARenderEndpointDoesNotEndTheWait()
    {
        long requested = _monitor.Sequence;
        DeviceSnapshot older = _monitor.Snapshot(Capture(EndpointState.Unplugged));
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Unplugged)));

        // Taken after the request but before the newest snapshot seen, and delivered after it.
        _monitor.Raise(older);
        Assert.IsFalse(waiting.IsCompleted, "Only the newest snapshot seen can end the wait early.");
        _time.Advance(ConnectTimeout);
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsFalse(result.Reached);
        Assert.IsFalse(result.Unreachable);
        Assert.IsTrue(result.RenderEndpointObserved);
    }

    [TestMethod]
    public async Task AnotherDevicesStateNeverCounts()
    {
        var phoneRender = new AudioEndpoint("{0.0.0.00000000}.{44444444-5555-4666-8777-888888888802}", EndpointFlow.Render,
            EndpointState.Active, "Headphones (iPhone)", IPhoneContainer);
        long requested = _monitor.Sequence;
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
        long requested = _monitor.Sequence;
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

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Wait(ConnectAction.Connect, _monitor.Sequence, cts.Token));

        Assert.AreEqual(0, _monitor.Subscribed);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task AFailedRefreshFailsTheWait()
    {
        _monitor.OnRefresh = _ => Task.FromException<DeviceSnapshot>(new ObjectDisposedException("monitor"));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => Wait(ConnectAction.Disconnect, _monitor.Sequence).WaitAsync(Guard));
    }

    [TestMethod]
    public async Task ARefreshThatFailsAfterTheStateWasSeenIsLogged()
    {
        var refresh = new TaskCompletionSource<DeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        long requested = _monitor.Sequence;
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

    // A rebuild after a settings change carries the enumeration's own number, so a snapshot stamped later than
    // the request but built from readings taken before it is not evidence.
    [TestMethod]
    public async Task ASnapshotOfAnEarlierEnumerationIsIgnoredHoweverLateItsTimeIs()
    {
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        DeviceSnapshot earlier = _monitor.Snapshot(Render(EndpointState.Active));
        long requested = _monitor.Sequence;
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        _time.Advance(TimeSpan.FromSeconds(1));
        _monitor.Raise(earlier with { TakenUtc = _time.GetUtcNow() });

        Assert.IsFalse(waiting.IsCompleted, "The number, not the time, says what came after the request.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(1, result.StaleIgnored);
    }

    [TestMethod]
    public async Task AFailedReadIsNeverEvidence()
    {
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        long requested = _monitor.Sequence;
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        // What the monitor publishes when an enumeration fails: the last devices, marked as not read.
        DeviceSnapshot failed = _monitor.Snapshot(Render(EndpointState.Active)) with
        {
            ReadStatus = SnapshotReadStatus.Failed,
            Resolution = TargetResolution.ReadFailed,
        };
        _monitor.Raise(failed);

        Assert.IsFalse(waiting.IsCompleted, "A read that failed says nothing about the device.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
        Assert.AreEqual(1, result.StaleIgnored);
    }

    // An endpoint whose container could not be read is grouped under Guid.Empty, so an empty-looking container
    // is not proof the device went.
    [TestMethod]
    public async Task ARenderEndpointWithAnUnreadableContainerKeepsTheWaitGoing()
    {
        _monitor.SetEndpoints(Render(EndpointState.Unplugged));
        long requested = _monitor.Sequence;
        Task<ConfirmationResult> waiting = Wait(ConnectAction.Connect, requested);
        await Armed();

        AudioEndpoint unreadable = Render(EndpointState.Unplugged) with { ContainerId = Guid.Empty, EndpointId = "{0.0.0.00000000}.{unreadable}" };
        _monitor.Raise(_monitor.Snapshot(unreadable));

        Assert.IsFalse(waiting.IsCompleted, "The device may be the endpoint whose container could not be read.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
        ConfirmationResult result = await waiting.WaitAsync(Guard);

        Assert.IsTrue(result.Reached);
    }

    [TestMethod]
    public void AGroupWithoutAContainerCanHideTheDevice()
    {
        DeviceSnapshot clean = _monitor.Snapshot(Render(EndpointState.Unplugged));
        DeviceSnapshot hidden = _monitor.Snapshot(Render(EndpointState.Unplugged) with { ContainerId = Guid.Empty });
        DeviceSnapshot captureOnly = _monitor.Snapshot(Capture(EndpointState.Unplugged) with { ContainerId = Guid.Empty });

        Assert.IsFalse(ConfirmationWaiter.MayHideTheDevice(clean));
        Assert.IsTrue(ConfirmationWaiter.MayHideTheDevice(hidden));
        Assert.IsFalse(ConfirmationWaiter.MayHideTheDevice(captureOnly), "Only a render endpoint can confirm anything.");
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
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Unplugged), Capture(EndpointState.Active)], ConnectAction.Disconnect),
            "An active Hands-Free capture endpoint is a live link.");
        Assert.IsTrue(ConfirmationWaiter.IsReached([Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged)], ConnectAction.Disconnect));
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Active), secondRender], ConnectAction.Disconnect),
            "Any render endpoint still active is not a disconnect.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Disabled)], ConnectAction.Disconnect),
            "A render endpoint turned off in Sound settings hides the link state, so no disconnect is seen.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([Render(EndpointState.Disabled), Capture(EndpointState.Unplugged)], ConnectAction.Disconnect),
            "The capture side cannot stand in for it.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([Capture(EndpointState.Unplugged)], ConnectAction.Disconnect),
            "Without a render endpoint nothing was observed.");
        Assert.IsFalse(ConfirmationWaiter.IsReached([], ConnectAction.Disconnect));
    }

    [TestMethod]
    public void TheStateIsUnreachableWhenNoRenderEndpointCanGetThere()
    {
        AudioEndpoint secondRender = Render(EndpointState.Unplugged) with { EndpointId = "{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516971}" };

        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([], ConnectAction.Connect));
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Capture(EndpointState.Active)], ConnectAction.Connect));
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Capture(EndpointState.Active)], ConnectAction.Disconnect));
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Render(EndpointState.NotPresent), Capture(EndpointState.Unplugged)], ConnectAction.Connect));
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Render(EndpointState.Disabled)], ConnectAction.Connect));
        Assert.IsFalse(ConfirmationWaiter.IsUnreachable([Render(EndpointState.NotPresent), secondRender], ConnectAction.Connect));
        Assert.IsFalse(ConfirmationWaiter.IsUnreachable([Render(EndpointState.Unplugged)], ConnectAction.Connect));
        Assert.IsFalse(ConfirmationWaiter.IsUnreachable([Render(EndpointState.NotPresent)], ConnectAction.Disconnect),
            "NOTPRESENT is where a disconnect is going.");
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Render(EndpointState.Disabled)], ConnectAction.Disconnect),
            "Every render endpoint turned off in Sound settings: a disconnect can no longer be seen.");
        Assert.IsTrue(ConfirmationWaiter.IsUnreachable([Render(EndpointState.Disabled), Capture(EndpointState.Active)], ConnectAction.Disconnect));
        Assert.IsFalse(ConfirmationWaiter.IsUnreachable([Render(EndpointState.Disabled), secondRender], ConnectAction.Disconnect),
            "One render endpoint can still show the link state.");
    }

    [TestMethod]
    public void TheTimeoutsAreNamedBudgets()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(15), ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        Assert.AreEqual(TimeSpan.FromSeconds(12), ConfirmationWaiter.TimeoutFor(ConnectAction.Disconnect));
    }
}
