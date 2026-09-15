using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase2;

// The monitor over a fake endpoint source, a real audio worker (no COM work reaches it) and a delay the test
// releases by hand.
[TestClass]
public sealed class CoreAudioDeviceMonitorTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] SubscribeThenEnumerate = ["subscribe", "enumerate"];

    private CapturingLog _log = null!;
    private AudioWorker _worker = null!;
    private FakeEndpointSource _source = null!;
    private FakeSettingsStore _settings = null!;
    private ManualDelay _delay = null!;
    private List<DeviceSnapshot> _raised = null!;
    private int _posts;
    private CoreAudioDeviceMonitor _monitor = null!;

    [TestInitialize]
    public void Setup()
    {
        _log = new CapturingLog();
        _worker = new AudioWorker(_log);
        _source = new FakeEndpointSource();
        _source.SetReadings(Machine());
        _settings = new FakeSettingsStore();
        _delay = new ManualDelay();
        _raised = new List<DeviceSnapshot>();
        _posts = 0;
        _monitor = NewMonitor(action =>
        {
            Interlocked.Increment(ref _posts);
            action();
        });
    }

    public async ValueTask DisposeAsync()
    {
        _monitor.Dispose();
        await _worker.DisposeAsync();
    }

    private CoreAudioDeviceMonitor NewMonitor(Action<Action> uiPost)
    {
        var monitor = new CoreAudioDeviceMonitor(
            _worker, _source, _settings, _log, uiPost, CoreAudioDeviceMonitor.DefaultCoalesceWindow, _delay.Delay,
            () => DateTimeOffset.UtcNow);
        monitor.SnapshotChanged += (sender, e) =>
        {
            Assert.AreSame(monitor, sender);
            lock (_raised)
            {
                _raised.Add(e.Snapshot);
            }
        };
        return monitor;
    }

    private int Raised
    {
        get { lock (_raised) { return _raised.Count; } }
    }

    private DeviceSnapshot LastRaised
    {
        get { lock (_raised) { return _raised[^1]; } }
    }

    private static EndpointNotification StateChanged(string id, uint state) =>
        new(EndpointNotificationKind.StateChanged, id, state, 0, 0, Guid.Empty, 0);

    private async Task StartAndSettle()
    {
        _monitor.Start();
        await Eventually.True(() => _source.EnumerateCalls == 1 && Raised == 1, "the first enumeration after Start");
        await Drain();
    }

    // Waits until everything queued on the worker so far has run.
    private Task Drain() => _worker.RunAsync(_ => { }).WaitAsync(Timeout);

    [TestMethod]
    public async Task StartSubscribesBeforeItEnumerates()
    {
        await StartAndSettle();

        CollectionAssert.AreEqual(SubscribeThenEnumerate, _source.Calls.ToArray());
        Assert.IsTrue(_log.Has(LogLevel.Info, "Watching audio devices for changes."));
    }

    [TestMethod]
    public async Task StartPublishesTheFirstSnapshotThroughTheUiPost()
    {
        Assert.IsNull(_monitor.Current.Target);

        await StartAndSettle();

        Assert.AreEqual(1, _posts);
        Assert.AreEqual(AirPodsContainer, LastRaised.Target?.ContainerId);
        Assert.AreEqual(ConnectionState.Connected, LastRaised.Target?.Connection);
        Assert.AreSame(LastRaised, _monitor.Current);
    }

    [TestMethod]
    public async Task StartTwiceSubscribesOnce()
    {
        await StartAndSettle();
        _monitor.Start();
        await Drain();

        Assert.AreEqual(1, _source.Calls.Count(c => c == "subscribe"));
    }

    [TestMethod]
    public async Task EverySourceCallRunsOnTheWorkerThread()
    {
        await StartAndSettle();
        await _monitor.RefreshAsync().WaitAsync(Timeout);
        int workerThread = await _worker.RunAsync(_ => Environment.CurrentManagedThreadId).WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { workerThread }, _source.Threads.ToArray());
    }

    [TestMethod]
    public async Task TheSnapshotIsPostedNotRaisedDirectly()
    {
        var posted = new List<Action>();
        CoreAudioDeviceMonitor deferred = NewMonitor(action =>
        {
            lock (posted)
            {
                posted.Add(action);
            }
        });
        try
        {
            DeviceSnapshot snapshot = await deferred.RefreshAsync().WaitAsync(Timeout);

            Assert.AreEqual(0, Raised);
            Assert.HasCount(1, posted);
            posted[0]();
            Assert.AreEqual(1, Raised);
            Assert.AreSame(snapshot, LastRaised);
        }
        finally
        {
            deferred.Dispose();
        }
    }

    [TestMethod]
    public async Task ABurstOfNotificationsBecomesOneEnumeration()
    {
        await StartAndSettle();
        _source.SetReadings(Machine(EndpointState.Unplugged, EndpointState.Unplugged));

        _source.Notify(StateChanged(AirPodsRenderId, CoreAudio.DEVICE_STATE_UNPLUGGED));
        _source.Notify(StateChanged(AirPodsCaptureId, CoreAudio.DEVICE_STATE_UNPLUGGED));
        for (int i = 0; i < 8; i++)
        {
            _source.Notify(new EndpointNotification(EndpointNotificationKind.Removed, AirPodsCaptureId, 0, 0, 0, Guid.Empty, 0));
        }

        await Eventually.True(() => _delay.Requests == 1, "the coalescing delay");
        await Task.Delay(50);
        Assert.AreEqual(1, _delay.Requests, "One delay for the whole burst.");
        Assert.AreEqual(1, _source.EnumerateCalls, "Nothing is enumerated before the window ends.");

        _delay.ReleaseAll();
        await Eventually.True(() => _source.EnumerateCalls == 2 && Raised == 2, "one enumeration for the burst");
        await Task.Delay(50);

        Assert.AreEqual(2, _source.EnumerateCalls);
        Assert.AreEqual(10, _monitor.NotificationCount);
        Assert.AreEqual(ConnectionState.Disconnected, LastRaised.Target?.Connection);
    }

    [TestMethod]
    public async Task ANotificationDuringAnEnumerationGetsAPassOfItsOwn()
    {
        await StartAndSettle();
        using var gate = new ManualResetEventSlim(false);
        _source.EnumerateGate = gate;

        _source.Notify(StateChanged(AirPodsRenderId, CoreAudio.DEVICE_STATE_UNPLUGGED));
        await Eventually.True(() => _delay.Requests == 1, "the first delay");
        _delay.ReleaseAll();
        await Eventually.True(() => _source.EnumerateCalls == 2, "the notification enumeration to start");

        // The enumeration is running (held by the gate). A new notification must schedule another pass.
        _source.Notify(StateChanged(AirPodsRenderId, CoreAudio.DEVICE_STATE_ACTIVE));
        await Eventually.True(() => _delay.Requests == 2, "a second delay");

        gate.Set();
        _delay.ReleaseAll();
        await Eventually.True(() => _source.EnumerateCalls == 3, "the second pass");
    }

    [TestMethod]
    public async Task NotificationsThatCannotChangeTheModelAreIgnored()
    {
        await StartAndSettle();

        _source.Notify(new EndpointNotification(EndpointNotificationKind.DefaultChanged, AirPodsRenderId, 0, CoreAudio.eRender, CoreAudio.eConsole, Guid.Empty, 0));
        _source.Notify(new EndpointNotification(EndpointNotificationKind.PropertyChanged, AirPodsRenderId, 0, 0, 0,
            CoreAudio.PKEY_AudioEndpoint_FormFactor.fmtid, CoreAudio.PKEY_AudioEndpoint_FormFactor.pid));
        await Task.Delay(50);

        Assert.AreEqual(0, _delay.Requests);
        Assert.AreEqual(0, _monitor.NotificationCount);
    }

    [TestMethod]
    public async Task ANameChangeNotificationRefreshes()
    {
        await StartAndSettle();

        _source.Notify(new EndpointNotification(EndpointNotificationKind.PropertyChanged, AirPodsRenderId, 0, 0, 0,
            CoreAudio.PKEY_Device_FriendlyName.fmtid, CoreAudio.PKEY_Device_FriendlyName.pid));

        await Eventually.True(() => _delay.Requests == 1, "the coalescing delay");
    }

    [TestMethod]
    public async Task NoEventWhenNothingMaterialChanged()
    {
        DeviceSnapshot first = await _monitor.RefreshAsync().WaitAsync(Timeout);
        DeviceSnapshot second = await _monitor.RefreshAsync().WaitAsync(Timeout);

        Assert.AreEqual(1, Raised);
        Assert.IsTrue(EndpointModelBuilder.AreEquivalent(first, second));
        Assert.AreSame(second, _monitor.Current, "Current follows the latest successful read.");
    }

    [TestMethod]
    public async Task RefreshWorksWithoutStartAndDoesNotSubscribe()
    {
        DeviceSnapshot snapshot = await _monitor.RefreshAsync().WaitAsync(Timeout);

        Assert.AreEqual(AirPodsContainer, snapshot.Target?.ContainerId);
        Assert.IsFalse(_source.Calls.Contains("subscribe"));
    }

    [TestMethod]
    public async Task AStateChangeRaisesTheNewState()
    {
        await _monitor.RefreshAsync().WaitAsync(Timeout);
        _source.SetReadings(Machine(EndpointState.Unplugged, EndpointState.Unplugged));

        DeviceSnapshot snapshot = await _monitor.RefreshAsync().WaitAsync(Timeout);

        Assert.AreEqual(2, Raised);
        Assert.AreEqual(ConnectionState.Disconnected, snapshot.Target?.Connection);
        Assert.IsTrue(_log.Has(LogLevel.Info, "Audio devices changed (refresh)"));
    }

    [TestMethod]
    public async Task BlockedEndpointsDisappearingLeaveNoTarget()
    {
        await _monitor.RefreshAsync().WaitAsync(Timeout);
        _source.SetReadings(Machine().Where(r => r.Endpoint.ContainerId != AirPodsContainer));

        DeviceSnapshot snapshot = await _monitor.RefreshAsync().WaitAsync(Timeout);

        Assert.IsNull(snapshot.Target);
        Assert.AreEqual(2, Raised);
    }

    [TestMethod]
    public async Task AFailedEnumerationKeepsTheLastSnapshotAndIsReported()
    {
        DeviceSnapshot good = await _monitor.RefreshAsync().WaitAsync(Timeout);
        _source.EnumerationFailure = StepOutcomes.FromHResult(CoreAudioEndpointReader.EnumerateStep, CoreAudio.E_NOTFOUND);

        MonitorRefresh failed = await _monitor.RefreshDetailedAsync().WaitAsync(Timeout);

        Assert.IsFalse(failed.EnumerationOk);
        Assert.AreSame(good, failed.Snapshot);
        Assert.AreSame(good, _monitor.Current);
        Assert.AreEqual(1, Raised);
        StepOutcome step = failed.Steps.Single();
        Assert.AreEqual("E_NOTFOUND", step.CodeName);
        Assert.IsTrue(_log.Has(LogLevel.Error, "Could not read the audio devices (refresh): enumerate-endpoints E_NOTFOUND"));
    }

    [TestMethod]
    public async Task PropertyReadFailuresAreLoggedAtDebugOnceEach()
    {
        StepOutcome unreadable = StepOutcomes.FromHResult(CoreAudioEndpointReader.FriendlyNameStep + ":" + "{0.0.0.00000000}.{hdmi}", CoreAudio.ERROR_NO_SUCH_DEVINST);
        _source.SetReadings(Machine(), new[] { unreadable });

        MonitorRefresh first = await _monitor.RefreshDetailedAsync().WaitAsync(Timeout);
        await _monitor.RefreshDetailedAsync().WaitAsync(Timeout);

        Assert.IsTrue(first.EnumerationOk);
        Assert.AreEqual("ERROR_NO_SUCH_DEVINST", first.Steps.Single().CodeName);
        Assert.AreEqual(1, _log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("ERROR_NO_SUCH_DEVINST", StringComparison.Ordinal)));
        Assert.IsFalse(_log.Entries.Any(e => e.Level >= LogLevel.Warn), "An expected property failure is not a warning.");

        // Gone, then back: logged again.
        _source.SetReadings(Machine());
        await _monitor.RefreshDetailedAsync().WaitAsync(Timeout);
        _source.SetReadings(Machine(), new[] { unreadable });
        await _monitor.RefreshDetailedAsync().WaitAsync(Timeout);
        Assert.AreEqual(2, _log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("ERROR_NO_SUCH_DEVINST", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ADeviceMatchChangeChoosesTheTargetAgainWithoutEnumerating()
    {
        await StartAndSettle();

        _settings.Update(s => s.DeviceMatch = "Seiren");
        await Eventually.True(() => Raised == 2, "the new target");
        await Drain();

        Assert.AreEqual(MicrophoneContainer, LastRaised.Target?.ContainerId);
        Assert.AreEqual(1, _source.EnumerateCalls);
    }

    [TestMethod]
    public async Task APinnedContainerChangeChoosesTheTargetAgain()
    {
        await StartAndSettle();

        _settings.Update(s => s.PinnedContainerId = MicrophoneContainer);
        await Eventually.True(() => Raised == 2, "the pinned target");

        Assert.AreEqual(MicrophoneContainer, LastRaised.Target?.ContainerId);
    }

    [TestMethod]
    public async Task OtherSettingChangesAreIgnored()
    {
        await StartAndSettle();

        _settings.Update(s => s.OpenOnStartup = false);
        _settings.Reload();
        await Drain();

        Assert.AreEqual(1, Raised);
        Assert.AreEqual(1, _source.EnumerateCalls);
    }

    [TestMethod]
    public async Task DisposeUnsubscribesOnTheWorkerAndIgnoresEverythingAfter()
    {
        await StartAndSettle();
        Action<EndpointNotification> sink = _source.Sink!;

        _monitor.Dispose();
        await Eventually.True(() => _source.Calls.Contains("unsubscribe"), "unsubscribe");

        sink(StateChanged(AirPodsRenderId, CoreAudio.DEVICE_STATE_UNPLUGGED));
        _settings.Update(s => s.DeviceMatch = "Seiren");
        await Drain();
        await Task.Delay(50);

        Assert.AreEqual(0, _delay.Requests);
        Assert.AreEqual(1, Raised);
        Assert.IsTrue(_log.Has(LogLevel.Info, "Stopped watching audio devices."));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _monitor.Start());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => _monitor.RefreshAsync());
    }

    [TestMethod]
    public async Task ANotificationPendingAtDisposeDoesNotEnumerate()
    {
        await StartAndSettle();
        _source.Notify(StateChanged(AirPodsRenderId, CoreAudio.DEVICE_STATE_UNPLUGGED));
        await Eventually.True(() => _delay.Requests == 1, "the coalescing delay");

        _monitor.Dispose();
        _delay.ReleaseAll();
        await Task.Delay(50);
        await Drain();

        Assert.AreEqual(1, _source.EnumerateCalls);
    }

    [TestMethod]
    public async Task DisposeBeforeTheStartItemRunsNeverSubscribes()
    {
        using var gate = new ManualResetEventSlim(false);
        Task blocker = _worker.RunAsync(token => gate.Wait(Timeout, token));

        _monitor.Start();
        _monitor.Dispose();
        gate.Set();
        await blocker.WaitAsync(Timeout);
        await Drain();

        Assert.IsFalse(_source.Calls.Contains("subscribe"));
        Assert.AreEqual(0, Raised);
    }

    [TestMethod]
    public async Task ASubscribeFailureIsLoggedAndDiscoveryStillWorks()
    {
        _source.SubscribeResult = StepOutcomes.FromHResult(AudioWorker.Steps.RegisterClient, unchecked((int)0x80004005));

        _monitor.Start();
        await Eventually.True(() => Raised == 1, "the first snapshot");

        Assert.IsTrue(_log.Has(LogLevel.Error, "Could not watch audio devices for changes: register-notification-client E_FAIL"));
        Assert.AreEqual(AirPodsContainer, _monitor.Current.Target?.ContainerId);
    }

    [TestMethod]
    public async Task CallbackFailuresAreLoggedAsErrors()
    {
        _source.SetCallbackFailures(3, new InvalidOperationException("sink failed"));

        await _monitor.RefreshAsync().WaitAsync(Timeout);

        LogEntry entry = _log.Entries.Single(e => e.Level == LogLevel.Error);
        StringAssert.Contains(entry.Message, "3 audio device notifications could not be handled");
        Assert.IsInstanceOfType<InvalidOperationException>(entry.Exception);
    }

    [TestMethod]
    public async Task ARefreshAfterTheWorkerStoppedFailsLoudly()
    {
        await _worker.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => _monitor.RefreshAsync());
    }

    [TestMethod]
    public async Task AStartThatCannotReachTheWorkerIsLogged()
    {
        await _worker.DisposeAsync();

        _monitor.Start();

        await Eventually.True(() => _log.Has(LogLevel.Error, "Could not start watching audio devices."), "the logged failure");
    }
}
