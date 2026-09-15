using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;
using static Earshot.Tests.Phase3.ConnectFixtures;

namespace Earshot.Tests.Phase3;

// The controller over a real audio worker, a fake connect path, a fake monitor and a manual clock. Covers every
// outcome in the failure table, that nothing is sent when the decision is not to send, that Connected and
// Disconnected come only from an observed state, and cancellation on both sides of the send.
[TestClass]
public sealed class ConnectionControllerTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly string[] ReconnectSteps = ["ks-reconnect:src", "ks-reconnect:wave"];
    private static readonly string[] DisconnectSteps = ["ks-disconnect:src", "ks-disconnect:wave"];
    private static readonly int[] FailThenNotFound = [EFail, CoreAudio.E_NOTFOUND];
    private static readonly string[] KeptReadSteps =
        [CoreAudioEndpointReader.InterfaceNameStep + ":" + AirPodsCaptureId, CoreAudioEndpointReader.ItemStep + ":7"];

    private ManualTimeProvider _time = null!;
    private FakeDeviceMonitor _monitor = null!;
    private FakeConnectPath _path = null!;
    private CapturingLog _log = null!;
    private AudioWorker _worker = null!;
    private ConnectionController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _time = new ManualTimeProvider();
        _monitor = new FakeDeviceMonitor(_time);
        _path = new FakeConnectPath();
        _log = new CapturingLog();
        _worker = new AudioWorker(_log);
        _controller = new ConnectionController(_worker, _path, new ConfirmationWaiter(_monitor, _log, _time), _log, _time);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Assert.AreEqual(0, _monitor.Handlers, "The SnapshotChanged handler is removed on every path.");
        Assert.AreEqual(0, _time.LiveTimers, "The timeout timer is disposed on every path.");
    }

    public async ValueTask DisposeAsync()
    {
        _monitor.Dispose();
        await _worker.DisposeAsync();
    }

    private void Device(params AudioEndpoint[] endpoints)
    {
        _path.Endpoints = endpoints.ToList();
        _monitor.SetEndpoints(endpoints);
    }

    private void Filters(int? srcHr, int? waveHr, int? waveActivateHr = null) =>
        _path.Filters =
        [
            new FakeFilter(SrcAdapter, Render(EndpointState.Unplugged), srcHr),
            new FakeFilter(WaveAdapter, Capture(EndpointState.Unplugged), waveHr, waveActivateHr),
        ];

    private async Task Armed() =>
        await Eventually.True(() => _time.ArmedTimers == 1 && _monitor.RefreshCalls == 1, "the confirmation wait to arm");

    private Task<int> WorkerThread() => _worker.RunAsync(_ => Environment.CurrentManagedThreadId);

    // Refused before anything runs

    [TestMethod]
    public async Task AContainerThatIsNeverATargetIsNotFoundAndNothingRuns()
    {
        foreach (Guid container in new[] { Guid.Empty, PcContainer })
        {
            ConnectResult connect = await _controller.ConnectAsync(container).WaitAsync(Guard);
            ConnectResult disconnect = await _controller.DisconnectAsync(container).WaitAsync(Guard);

            foreach (ConnectResult result in new[] { connect, disconnect })
            {
                Assert.AreEqual(ConnectOutcome.NotFound, result.Outcome);
                Assert.AreEqual(ConnectMessages.NotFound, result.UserMessage);
                StepOutcome step = result.Steps.Single();
                Assert.AreEqual(ConnectionController.GuardStep, step.Step);
                Assert.AreEqual(NativeCodes.NotAttempted, step.Code, "A refusal never reads as S_OK.");
            }
        }

        Assert.IsEmpty(_path.ReadThreads);
        Assert.IsFalse(_worker.HasStarted);
    }

    [TestMethod]
    public async Task AContainerWithoutARenderEndpointIsNotFoundAndNothingIsSent()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Active), Phone());
        Filters(SOk, SOk);

        // The phone's container holds only an active Hands-Free capture endpoint; a new container holds nothing.
        ConnectResult phone = await _controller.ConnectAsync(IPhoneContainer).WaitAsync(Guard);
        ConnectResult unknown = await _controller.DisconnectAsync(Guid.NewGuid()).WaitAsync(Guard);

        foreach (ConnectResult result in new[] { phone, unknown })
        {
            Assert.AreEqual(ConnectOutcome.NotFound, result.Outcome);
            Assert.AreEqual(ConnectMessages.NotFound, result.UserMessage);
        }

        Assert.HasCount(2, _path.ReadThreads);
        Assert.IsEmpty(_path.Sends, "Nothing is ever sent towards the phone's filter.");
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task EndpointsThatCannotBeReadAreAFailureWithTheirSteps()
    {
        _path.ReadOk = false;
        _path.ReadSteps = [StepOutcomes.FromHResult(CoreAudioEndpointReader.EnumerateStep, FakeHr.EOutOfMemory)];

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.CouldNotReachDriver, result.UserMessage);
        Assert.AreEqual("E_OUTOFMEMORY", result.Steps.Single().CodeName);
        Assert.IsEmpty(_path.Sends);
    }

    [TestMethod]
    public async Task ConnectWithEveryEndpointNotPresentIsNodesBlockedAndSendsNothing()
    {
        Device(Render(EndpointState.NotPresent), Capture(EndpointState.NotPresent));
        Filters(SOk, SOk);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NodesBlocked, result.Outcome);
        Assert.AreEqual(ConnectMessages.AllowingFirst, result.UserMessage);
        Assert.IsEmpty(_path.Sends, "A blocked device is never sent a request.");
        Assert.AreEqual(0, _monitor.RefreshCalls, "Nothing is waited for.");
        Assert.IsFalse(result.Confirmed);
    }

    [TestMethod]
    public async Task ConnectWhenAlreadyActiveConfirmsWithoutSending()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Unplugged));

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        Assert.AreEqual(ConnectMessages.Connected, result.UserMessage);
        Assert.IsEmpty(_path.Sends);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    [DataRow(EndpointState.Unplugged)]
    [DataRow(EndpointState.NotPresent)]
    public async Task DisconnectWhenAlreadyDisconnectedConfirmsWithoutSending(EndpointState state)
    {
        Device(Render(state), Capture(state));

        ConnectResult result = await _controller.DisconnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        Assert.AreEqual(ConnectMessages.Disconnected, result.UserMessage);
        Assert.IsEmpty(_path.Sends);
    }

    // Sent and confirmed

    [TestMethod]
    public async Task ConnectSendsOnTheWorkerThenConfirmsFromTheEndpointState()
    {
        int worker = await WorkerThread();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        Assert.IsFalse(connecting.IsCompleted, "S_OK alone confirms nothing.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active), Capture(EndpointState.Unplugged)));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        Assert.AreEqual(ConnectMessages.Connected, result.UserMessage);
        var send = _path.Sends.Single();
        Assert.AreEqual((AirPodsContainer, ConnectAction.Connect, FilterChoice.All), (send.Container, send.Action, send.Choice));
        Assert.AreEqual(worker, send.ThreadId, "The request is sent on the audio worker.");
        Assert.AreEqual(worker, _path.ReadThreads.Single(), "The endpoints are read on the audio worker.");
        CollectionAssert.AreEqual(ReconnectSteps, result.Steps.Select(s => s.Step).ToArray());
        Assert.IsTrue(_log.Has(LogLevel.Info, "Confirmed"));
    }

    [TestMethod]
    public async Task DisconnectSendsThenConfirmsFromTheEndpointState()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Active));
        Filters(SOk, SOk);

        Task<ConnectResult> disconnecting = _controller.DisconnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged)));
        ConnectResult result = await disconnecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        Assert.AreEqual(ConnectMessages.Disconnected, result.UserMessage);
        Assert.AreEqual(ConnectAction.Disconnect, _path.Sends.Single().Action);
        CollectionAssert.AreEqual(DisconnectSteps, result.Steps.Select(s => s.Step).ToArray());
    }

    [TestMethod]
    public async Task AStateReachedBeforeTheWaitArmsIsStillConfirmed()
    {
        Device(Render(EndpointState.Unplugged));
        Filters(SOk, SOk);

        // The device connects while the request is sent, before the wait is armed.
        _path.DuringSend = () => _monitor.SetEndpoints(Render(EndpointState.Active));
        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
    }

    [TestMethod]
    public async Task OneFilterAcceptingIsEnoughToWaitForTheState()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(EFail, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        CollectionAssert.AreEqual(new[] { ("ks-reconnect:src", "E_FAIL"), ("ks-reconnect:wave", "S_OK") },
            result.Steps.Select(s => (s.Step, s.CodeName)).ToArray(), "A partial success keeps the failed filter's code.");
    }

    // Sent and not confirmed

    [TestMethod]
    public async Task EveryFilterFailingIsNoFiltersRespondedWithEveryHResult()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(EFail, CoreAudio.E_NOTFOUND);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NoFiltersResponded, result.Outcome);
        Assert.AreEqual(ConnectMessages.CouldNotReachDriver, result.UserMessage);
        CollectionAssert.AreEqual(FailThenNotFound, result.Steps.Select(s => s.Code).ToArray());
        Assert.AreEqual(0, _monitor.RefreshCalls, "Nothing was attempted, so nothing is waited for.");
        Assert.IsTrue(_log.Has(LogLevel.Warn, "NoFiltersResponded"));
    }

    [TestMethod]
    public async Task NoFilterPassingTheGuardIsNoFiltersResponded()
    {
        Device(Render(EndpointState.Active));
        _path.Filters = [new FakeFilter(SrcAdapter, Render(EndpointState.Active), Hr: null)];

        ConnectResult result = await _controller.DisconnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NoFiltersResponded, result.Outcome);
        Assert.AreEqual(NativeCodes.NotAttempted, result.Steps.Single().Code);
    }

    [TestMethod]
    public async Task SOkWithNoStateChangeTimesOut()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _time.Advance(ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome);
        Assert.AreEqual(ConnectMessages.StillConnecting, result.UserMessage);
        Assert.IsFalse(result.Confirmed);
    }

    [TestMethod]
    public async Task ADisconnectThatTimesOutSaysSo()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Active));
        Filters(SOk, SOk);

        Task<ConnectResult> disconnecting = _controller.DisconnectAsync(AirPodsContainer);
        await Armed();
        _time.Advance(ConfirmationWaiter.TimeoutFor(ConnectAction.Disconnect));
        ConnectResult result = await disconnecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome);
        Assert.AreEqual(ConnectMessages.DidNotDisconnect, result.UserMessage);
    }

    [TestMethod]
    public async Task AnActiveStateSeenBeforeTheRequestNeverConfirmsIt()
    {
        DeviceSnapshot earlier = _monitor.Snapshot(Render(EndpointState.Active));
        Device(Render(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(earlier);
        _time.Advance(ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome);
    }

    [TestMethod]
    public async Task ADeviceInvalidatedWithNothingAcceptedWentAway()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED, null, waveActivateHr: CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.WentAway, result.UserMessage);
        Assert.IsTrue(result.Steps.Any(s => s.CodeName == "AUDCLNT_E_DEVICE_INVALIDATED"));
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task ADeviceInvalidatedOnOneFilterStillWaitsWhenTheOtherAccepted()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, null, waveActivateHr: CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _time.Advance(ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome, "The Hands-Free filter can go without the device going.");
    }

    [TestMethod]
    public async Task ARenderEndpointThatGoesDuringTheWaitWentAway()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _path.DuringSend = () => _monitor.SetEndpoints(Capture(EndpointState.Unplugged));

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _time.Advance(ConfirmationWaiter.TimeoutFor(ConnectAction.Connect));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.WentAway, result.UserMessage);
    }

    // Cancellation

    [TestMethod]
    public async Task CancellingDuringTheWaitThrowsAndTheRequestStaysSent()
    {
        using var cts = new CancellationTokenSource();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer, cts.Token);
        await Armed();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => connecting.WaitAsync(Guard));
        Assert.HasCount(1, _path.Sends, "The request was sent once and not recalled.");
        Assert.IsTrue(_log.Has(LogLevel.Info, "the wait was cancelled"));
        Assert.IsTrue(_log.Has(LogLevel.Info, "ks-reconnect:src S_OK"), "The requests already sent are logged.");
    }

    [TestMethod]
    public async Task CancellingWhileTheEndpointsAreReadSendsNothing()
    {
        using var cts = new CancellationTokenSource();
        Device(Render(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _path.DuringRead = cts.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => _controller.ConnectAsync(AirPodsContainer, cts.Token).WaitAsync(Guard));

        Assert.HasCount(1, _path.ReadThreads);
        Assert.IsEmpty(_path.Sends);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task ATokenCancelledBeforeTheStartReadsAndSendsNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Device(Render(EndpointState.Unplugged));
        Filters(SOk, SOk);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _controller.DisconnectAsync(AirPodsContainer, cts.Token).WaitAsync(Guard));

        Assert.IsEmpty(_path.ReadThreads);
        Assert.IsEmpty(_path.Sends);
    }

    [TestMethod]
    public async Task AWaitThatFailsIsLoggedWithTheSentStepsAndRethrown()
    {
        Device(Render(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _monitor.OnRefresh = _ => Task.FromException<DeviceSnapshot>(new ObjectDisposedException("monitor"));

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard));

        Assert.IsTrue(_log.Has(LogLevel.Error, "ks-reconnect:src S_OK"));
    }

    // Decision and steps

    [TestMethod]
    public void TheDecisionComesFromTheEndpointsRead()
    {
        static EndpointRead Read(params AudioEndpoint[] endpoints) =>
            EndpointRead.From(new EndpointEnumeration(true, endpoints.Select(EndpointReading.Of).ToList(), []), AirPodsContainer);

        Assert.AreEqual(ConnectDecision.ReadFailed, ConnectionController.Decide(ConnectAction.Connect, EndpointRead.Failed([])));
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Connect, Read(Phone())));
        Assert.AreEqual(ConnectDecision.NodesBlocked, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.NotPresent))));
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Connect,
            Read(Render(EndpointState.NotPresent), Capture(EndpointState.Unplugged))), "Not every endpoint is NOTPRESENT.");
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Disabled))));
        Assert.AreEqual(ConnectDecision.AlreadyInState, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Active))));

        Assert.AreEqual(ConnectDecision.AlreadyInState, ConnectionController.Decide(ConnectAction.Disconnect, Read(Render(EndpointState.NotPresent))));
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Disconnect, Read(Render(EndpointState.Active))));
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Disconnect, Read(Capture(EndpointState.Active))),
            "Without a render endpoint no state could confirm a request, so none is sent.");
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Connect, Read(Capture(EndpointState.Unplugged))));
    }

    [TestMethod]
    public async Task FailedReadsOfOtherDevicesStayOutOfTheResult()
    {
        AudioEndpoint hdmi = AmdHdmiUnreadable(1).Endpoint;
        Device(Render(EndpointState.Active), Capture(EndpointState.Active), hdmi);
        _path.ReadSteps =
        [
            StepOutcomes.FromHResult(CoreAudioEndpointReader.FriendlyNameStep + ":" + hdmi.EndpointId, CoreAudio.ERROR_NO_SUCH_DEVINST),
            StepOutcomes.FromHResult(CoreAudioEndpointReader.InterfaceNameStep + ":" + AirPodsCaptureId, EFail),
            StepOutcomes.FromHResult(CoreAudioEndpointReader.ItemStep + ":7", EFail),
        ];

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        CollectionAssert.AreEqual(
            KeptReadSteps,
            result.Steps.Select(s => s.Step).ToArray(),
            "The capture endpoint's failure and a failure that names no endpoint are kept; the HDMI endpoint's is not.");
    }
}
