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
        Assert.AreEqual(ConnectMessages.CouldNotReadDevices, result.UserMessage, "A read failure promises no other way.");
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
    [DataRow(EndpointState.Unplugged)]
    [DataRow(EndpointState.Active)]
    public async Task ConnectWithOnlyTheRenderEndpointNotPresentIsNodesBlockedAndSendsNothing(EndpointState capture)
    {
        // A partial block: the A2DP node is disabled but the Hands-Free node is not. A request to the Hands-Free
        // filter alone could page the AirPods over Hands-Free, and no render endpoint could confirm it.
        Device(Render(EndpointState.NotPresent), Capture(capture));
        Filters(SOk, SOk);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NodesBlocked, result.Outcome);
        Assert.IsEmpty(_path.Sends);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task ConnectWithTheOutputTurnedOffSendsNothing()
    {
        Device(Render(EndpointState.Disabled), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.OutputTurnedOff, result.UserMessage);
        Assert.IsEmpty(_path.Sends, "A DISABLED render endpoint can never confirm a connect.");
        Assert.AreEqual(0, _monitor.RefreshCalls);
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

    // A render endpoint turned off in Sound settings hides the link state: no disconnect could be seen, so
    // nothing is sent and the message says why rather than claim the AirPods disconnected.
    [TestMethod]
    [DataRow(EndpointState.Unplugged)]
    [DataRow(EndpointState.Active)]
    public async Task DisconnectWithTheOutputTurnedOffSendsNothingAndSaysWhy(EndpointState capture)
    {
        Device(Render(EndpointState.Disabled), Capture(capture));
        Filters(SOk, SOk);

        ConnectResult result = await _controller.DisconnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.OutputTurnedOff, result.UserMessage);
        Assert.IsEmpty(_path.Sends);
        Assert.AreEqual(0, _monitor.RefreshCalls);
    }

    [TestMethod]
    public async Task DisconnectEndsWithTheOutputMessageWhenTheOutputIsTurnedOffDuringTheWait()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Active));
        Filters(SOk, SOk);

        Task<ConnectResult> disconnecting = _controller.DisconnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Disabled), Capture(EndpointState.Unplugged)));
        ConnectResult result = await disconnecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.OutputTurnedOff, result.UserMessage);
    }

    // A driver that never returns must not leave the click in flight with no message.
    [TestMethod]
    public async Task ADriverThatDoesNotAnswerEndsTheOperationWithAMessage()
    {
        using var stuck = new ManualResetEventSlim();
        using var inDriver = new ManualResetEventSlim();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _path.DuringSend = () =>
        {
            inDriver.Set();
            stuck.Wait(TimeSpan.FromSeconds(30));
        };

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Eventually.True(() => inDriver.IsSet && _time.ArmedTimers == 1, "the driver call to start and its budget to arm");
        _time.Advance(ConnectionController.PassBudget);
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome);
        Assert.AreEqual(ConnectMessages.StillConnecting, result.UserMessage);
        StepOutcome stalled = result.Steps.Single();
        Assert.AreEqual(ConnectionController.StalledStep, stalled.Step);
        Assert.AreEqual(NativeCodes.NotAttempted, stalled.Code);

        stuck.Set();
        await _worker.RunAsync(_ => 0).WaitAsync(Guard);
    }

    // Once the budget has run out and the result is reported, a driver call that finally returns does not lead to
    // a request to the next filter.
    [TestMethod]
    public async Task ADriverThatAnswersAfterTheBudgetIsSentNothingMore()
    {
        using var stuck = new ManualResetEventSlim();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        using var inDriver = new ManualResetEventSlim();
        _path.AfterFilter = index =>
        {
            if (index == 0)
            {
                inDriver.Set();
                stuck.Wait(TimeSpan.FromSeconds(30));
            }
        };

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Eventually.True(() => inDriver.IsSet && _time.ArmedTimers == 1, "the driver call to start and its budget to arm");
        _time.Advance(ConnectionController.PassBudget);
        ConnectResult result = await connecting.WaitAsync(Guard);
        stuck.Set();
        await _worker.RunAsync(_ => 0).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.AttemptedTimedOut, result.Outcome);
        KsSendResult late = _path.LastSend!;
        Assert.AreEqual(0, late.Steps.Single(s => s.Step == "ks-reconnect:src").Code, "The call that was in the driver stays sent.");
        Assert.AreEqual(NativeCodes.NotAttempted, late.Steps.Single(s => s.Step == "ks-reconnect:wave").Code, "The Hands-Free filter is sent nothing.");
        Assert.AreEqual(0, _monitor.RefreshCalls, "Nothing is waited for after the result was reported.");
    }

    // A request still queued behind an earlier call that never returned is never sent, and the message does not
    // claim an attempt.
    [TestMethod]
    public async Task ARequestTheWorkerNeverStartedIsNotSentAfterTheBudget()
    {
        using var stuck = new ManualResetEventSlim();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        Task earlier = _worker.RunAsync(token => stuck.Wait(TimeSpan.FromSeconds(30), token));

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Eventually.True(() => _time.ArmedTimers == 1, "the budget for the driver call to arm");
        _time.Advance(ConnectionController.PassBudget);
        ConnectResult result = await connecting.WaitAsync(Guard);
        stuck.Set();
        await earlier.WaitAsync(Guard);
        await _worker.RunAsync(_ => 0).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.DriverBusy, result.UserMessage);
        StringAssert.Contains(result.Steps.Single(s => s.Step == ConnectionController.StalledStep).Detail, "no request was sent");
        Assert.IsEmpty(_path.ReadThreads, "The queued pass never ran.");
        Assert.IsEmpty(_path.Sends);
    }

    [TestMethod]
    public async Task DisconnectWithAnActiveHandsFreeLinkSendsAndWaitsForItToEnd()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Active));
        Filters(SOk, SOk);

        Task<ConnectResult> disconnecting = _controller.DisconnectAsync(AirPodsContainer);
        await Armed();
        Assert.HasCount(1, _path.Sends, "An active capture endpoint is a live link, so the request is sent.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Unplugged), Capture(EndpointState.Active)));
        Assert.IsFalse(disconnecting.IsCompleted, "Not disconnected while the capture endpoint is still active.");
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged)));
        ConnectResult result = await disconnecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome);
        Assert.AreEqual(ConnectMessages.Disconnected, result.UserMessage);
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
    public async Task AnInvalidatedHandsFreeFilterWhileTheA2dpFilterRejectsIsNoFiltersResponded()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(EFail, null, waveActivateHr: CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED);

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NoFiltersResponded, result.Outcome, "The Hands-Free filter can go without the device going.");
        Assert.AreEqual(ConnectMessages.CouldNotReachDriver, result.UserMessage);
        CollectionAssert.AreEqual(
            new[] { ("ks-reconnect:src", "E_FAIL"), (TopologyWalk.ActivateControlStep + ":" + WaveAdapter, "AUDCLNT_E_DEVICE_INVALIDATED"), ("ks-reconnect:wave", "NOT_ATTEMPTED") },
            result.Steps.Select(s => (s.Step, s.CodeName)).ToArray(),
            "Every step is kept for the coordinator.");
    }

    [TestMethod]
    public void AnInvalidatedRenderEndpointReadIsTheA2dpSide()
    {
        EndpointRead read = EndpointRead.From(new EndpointEnumeration(true,
            new[] { Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged) }.Select(EndpointReading.Of).ToList(), []), AirPodsContainer);
        var empty = new KsSendResult([], [], []);
        var renderWalk = new KsSendResult([], [], [StepOutcomes.FromHResult(TopologyWalk.ActivateTopologyStep + ":" + AirPodsRenderId, CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED)]);
        var captureWalk = new KsSendResult([], [], [StepOutcomes.FromHResult(TopologyWalk.ActivateTopologyStep + ":" + AirPodsCaptureId, CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED)]);

        Assert.IsFalse(ConnectionController.RenderSideInvalidated(read, empty));
        Assert.IsTrue(ConnectionController.RenderSideInvalidated(read, renderWalk));
        Assert.IsFalse(ConnectionController.RenderSideInvalidated(read, captureWalk));
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task ARenderEndpointThatGoesDuringAConnectWentAwayWithoutWaitingForTheTimeout(bool notPresent)
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _path.DuringSend = () =>
        {
            if (notPresent)
            {
                _monitor.SetEndpoints(Render(EndpointState.NotPresent), Capture(EndpointState.Unplugged));
            }
            else
            {
                _monitor.SetEndpoints(Capture(EndpointState.Unplugged));
            }
        };

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.WentAway, result.UserMessage);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "state can no longer come"));
    }

    [TestMethod]
    public async Task ARenderEndpointTurnedOffDuringAConnectSaysSo()
    {
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Disabled), Capture(EndpointState.Unplugged)));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.OutputTurnedOff, result.UserMessage);
    }

    [TestMethod]
    public async Task ADisconnectWhoseRenderEndpointGoesWentAway()
    {
        Device(Render(EndpointState.Active), Capture(EndpointState.Active));
        Filters(SOk, SOk);
        _path.DuringSend = () => _monitor.SetEndpoints(Capture(EndpointState.Unplugged));

        ConnectResult result = await _controller.DisconnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.WentAway, result.UserMessage);
    }

    // An exception in the walk

    [TestMethod]
    public async Task AWalkThatThrowsAfterAnAcceptedRequestIsLoggedAndStillConfirmsFromTheState()
    {
        var error = new InvalidOperationException("walk failed");
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, null);
        _path.Fault = error;

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer);
        await Armed();
        _monitor.Raise(_monitor.Snapshot(Render(EndpointState.Active)));
        ConnectResult result = await connecting.WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Confirmed, result.Outcome, "The A2DP filter accepted, so the state decides.");
        Assert.AreEqual("ks-reconnect:src", result.Steps[0].Step);
        LogEntry logged = _log.Entries.Single(e => e.Level == LogLevel.Error);
        Assert.AreSame(error, logged.Exception);
        Assert.IsTrue(logged.Message.Contains("ks-reconnect:src S_OK", StringComparison.Ordinal), "The request already sent is in the log.");
    }

    // The walk stopping is not a reason to lose what each filter did: the caller needs those steps to tell
    // "the A2DP filter refused" from "nothing responded" (the Hands-Free fallback rests on that).
    [TestMethod]
    public async Task AWalkThatThrowsWithNothingAcceptedIsLoggedAndReportedWithEveryStep()
    {
        var error = new InvalidOperationException("walk failed");
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(EFail, null);
        _path.Fault = error;

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.NoFiltersResponded, result.Outcome);
        Assert.AreEqual(ConnectMessages.CouldNotReachDriver, result.UserMessage);
        StepOutcome rejected = result.Steps.Single(s => s.Step == "ks-reconnect:src");
        Assert.AreEqual(EFail, rejected.Code);
        Assert.AreEqual(FilterRole.A2dp, KsConnectPath.RoleOfStep(rejected));
        Assert.IsTrue(_log.Has(LogLevel.Error, "the walk to the filters stopped with an exception"));
        Assert.AreEqual(0, _monitor.RefreshCalls, "Nothing was accepted, so nothing is waited for.");
    }

    // Cancellation

    // A newer click cancels this one while the A2DP filter is still in the driver: what was sent stays sent,
    // and the Hands-Free filter is not sent anything afterwards.
    [TestMethod]
    public async Task CancellingBetweenTwoFiltersStopsTheWalkBeforeTheSecond()
    {
        using var cts = new CancellationTokenSource();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        _path.AfterFilter = index =>
        {
            if (index == 0)
            {
                cts.Cancel();
            }
        };

        ConnectCancelledException cancelled = await Assert.ThrowsExactlyAsync<ConnectCancelledException>(
            () => _controller.ConnectAsync(AirPodsContainer, cts.Token).WaitAsync(Guard));

        Assert.AreEqual(NativeCodes.NotAttempted, cancelled.Steps.Single(s => s.Step == "ks-reconnect:wave").Code, "The Hands-Free filter was sent nothing.");
        Assert.IsTrue(_log.Entries.Any(e => e.Message.Contains("cancelled before this filter", StringComparison.Ordinal)),
            "The Hands-Free filter was sent nothing, and the log says why.");
        Assert.IsTrue(_log.Entries.Any(e => e.Message.Contains("ks-reconnect:src S_OK", StringComparison.Ordinal)),
            "The request already sent stays sent.");
    }

    // The A2DP filter refused, then a newer click cancelled: that is a cancel with the refusal on record, not "no
    // filter responded", which the block coordinator would take as a reason to try the Hands-Free way.
    [TestMethod]
    public async Task CancellingAfterTheA2dpFilterRefusedThrowsWithTheRefusalOnRecord()
    {
        using var cts = new CancellationTokenSource();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(EFail, SOk);
        _path.AfterFilter = index =>
        {
            if (index == 0)
            {
                cts.Cancel();
            }
        };

        ConnectCancelledException cancelled = await Assert.ThrowsExactlyAsync<ConnectCancelledException>(
            () => _controller.ConnectAsync(AirPodsContainer, cts.Token).WaitAsync(Guard));

        Assert.AreEqual(cts.Token, cancelled.CancellationToken);
        StepOutcome refused = cancelled.Steps.Single(s => s.Step == "ks-reconnect:src");
        Assert.AreEqual(EFail, refused.Code);
        Assert.AreEqual(FilterRole.A2dp, KsConnectPath.RoleOfStep(refused));
        Assert.AreEqual(NativeCodes.NotAttempted, cancelled.Steps.Single(s => s.Step == "ks-reconnect:wave").Code);
        Assert.AreEqual(0, _monitor.RefreshCalls);
        Assert.IsTrue(_log.Has(LogLevel.Info, "cancelled during the walk to the filters"));
    }

    // Cancelled while the driver call has not returned and the budget runs out too: a request may have gone out,
    // so the caller gets the steps rather than a bare cancel.
    [TestMethod]
    public async Task CancellingWhileTheDriverCallHasNotReturnedThrowsWithThePassStep()
    {
        using var cts = new CancellationTokenSource();
        using var stuck = new ManualResetEventSlim();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);
        using var inDriver = new ManualResetEventSlim();
        _path.AfterFilter = index =>
        {
            if (index == 0)
            {
                inDriver.Set();
                stuck.Wait(TimeSpan.FromSeconds(30));
            }
        };

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer, cts.Token);
        await Eventually.True(() => inDriver.IsSet && _time.ArmedTimers == 1, "the driver call to start and its budget to arm");
        await cts.CancelAsync();
        _time.Advance(ConnectionController.PassBudget);

        ConnectCancelledException cancelled = await Assert.ThrowsExactlyAsync<ConnectCancelledException>(() => connecting.WaitAsync(Guard));
        stuck.Set();
        await _worker.RunAsync(_ => 0).WaitAsync(Guard);

        Assert.AreEqual(cts.Token, cancelled.CancellationToken);
        Assert.AreEqual(ConnectionController.StalledStep, cancelled.Steps.Single().Step);
        Assert.AreEqual(NativeCodes.NotAttempted, _path.LastSend!.Steps.Single(s => s.Step == "ks-reconnect:wave").Code);
    }

    [TestMethod]
    public async Task CancellingDuringTheWaitThrowsAndTheRequestStaysSent()
    {
        using var cts = new CancellationTokenSource();
        Device(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        Filters(SOk, SOk);

        Task<ConnectResult> connecting = _controller.ConnectAsync(AirPodsContainer, cts.Token);
        await Armed();
        await cts.CancelAsync();

        ConnectCancelledException cancelled = await Assert.ThrowsExactlyAsync<ConnectCancelledException>(() => connecting.WaitAsync(Guard));
        Assert.IsInstanceOfType<OperationCanceledException>(cancelled, "A caller that catches OperationCanceledException still does.");
        Assert.AreEqual(cts.Token, cancelled.CancellationToken);
        StepOutcome sent = cancelled.Steps.Single(s => s.Step == "ks-reconnect:src");
        Assert.AreEqual(0, sent.Code, "The steps say what was sent before the cancel.");
        Assert.AreEqual(FilterRole.A2dp, KsConnectPath.RoleOfStep(sent));
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
        AudioEndpoint secondRender = Render(EndpointState.NotPresent) with { EndpointId = "{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516971}" };

        Assert.AreEqual(ConnectDecision.NodesBlocked, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.NotPresent))));
        Assert.AreEqual(ConnectDecision.NodesBlocked, ConnectionController.Decide(ConnectAction.Connect,
            Read(Render(EndpointState.NotPresent), Capture(EndpointState.Unplugged))), "Every render endpoint is NOTPRESENT, whatever capture shows.");
        Assert.AreEqual(ConnectDecision.NodesBlocked, ConnectionController.Decide(ConnectAction.Connect,
            Read(Render(EndpointState.NotPresent), Capture(EndpointState.Active))));
        Assert.AreEqual(ConnectDecision.OutputDisabled, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Disabled))));
        Assert.AreEqual(ConnectDecision.OutputDisabled, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Disabled), secondRender)));
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Unplugged), secondRender)),
            "One render endpoint that can become ACTIVE is enough.");
        Assert.AreEqual(ConnectDecision.AlreadyInState, ConnectionController.Decide(ConnectAction.Connect, Read(Render(EndpointState.Active))));

        Assert.AreEqual(ConnectDecision.AlreadyInState, ConnectionController.Decide(ConnectAction.Disconnect, Read(Render(EndpointState.NotPresent))));
        Assert.AreEqual(ConnectDecision.AlreadyInState, ConnectionController.Decide(ConnectAction.Disconnect,
            Read(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged))));
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Disconnect,
            Read(Render(EndpointState.Unplugged), Capture(EndpointState.Active))), "An active Hands-Free link is not disconnected.");
        Assert.AreEqual(ConnectDecision.Send, ConnectionController.Decide(ConnectAction.Disconnect, Read(Render(EndpointState.Active))));
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Disconnect, Read(Capture(EndpointState.Active))),
            "Without a render endpoint no state could confirm a request, so none is sent.");
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Connect, Read(Capture(EndpointState.Unplugged))));
        Assert.AreEqual(ConnectDecision.OutputDisabled, ConnectionController.Decide(ConnectAction.Disconnect, Read(Render(EndpointState.Disabled))),
            "Every render endpoint turned off: no disconnect could be seen, so nothing is sent.");
        Assert.AreEqual(ConnectDecision.OutputDisabled, ConnectionController.Decide(ConnectAction.Disconnect,
            Read(Render(EndpointState.Disabled), Capture(EndpointState.Active))));
    }

    // "Not found" is a claim about the device. When a read failure could have kept the device's own render
    // endpoint out of its container, the decision is a failed read instead.
    [TestMethod]
    [DataRow(CoreAudioEndpointReader.ContainerStep)]
    [DataRow(CoreAudioEndpointReader.StoreStep)]
    [DataRow(CoreAudioEndpointReader.IdStep)]
    [DataRow(CoreAudioEndpointReader.FlowStep)]
    [DataRow(CoreAudioEndpointReader.ItemStep)]
    public void AReadFailureThatCouldHideTheRenderEndpointIsNotNotFound(string failedStep)
    {
        AudioEndpoint unreadable = Render(EndpointState.Active) with { ContainerId = Guid.Empty, EndpointId = "{0.0.0.00000000}.{unreadable}" };
        var enumeration = new EndpointEnumeration(true,
            [EndpointReading.Of(Capture(EndpointState.Active)), EndpointReading.Of(unreadable)],
            [StepOutcomes.FromHResult(failedStep + ":" + unreadable.EndpointId, EFail)]);

        EndpointRead read = EndpointRead.From(enumeration, AirPodsContainer);

        Assert.IsTrue(ConnectionController.HidesAnEndpoint(read));
        Assert.AreEqual(ConnectDecision.ReadFailed, ConnectionController.Decide(ConnectAction.Connect, read));
        Assert.AreEqual(ConnectDecision.ReadFailed, ConnectionController.Decide(ConnectAction.Disconnect, read));
    }

    [TestMethod]
    public void AFailedNameReadHidesNothing()
    {
        var enumeration = new EndpointEnumeration(true,
            [EndpointReading.Of(Capture(EndpointState.Active))],
            [StepOutcomes.FromHResult(CoreAudioEndpointReader.FriendlyNameStep + ":" + AirPodsCaptureId, EFail)]);

        EndpointRead read = EndpointRead.From(enumeration, AirPodsContainer);

        Assert.IsFalse(ConnectionController.HidesAnEndpoint(read));
        Assert.AreEqual(ConnectDecision.NotFound, ConnectionController.Decide(ConnectAction.Connect, read));
    }

    [TestMethod]
    public async Task AConnectWhoseReadCouldHideTheDeviceSaysTheReadFailed()
    {
        AudioEndpoint unreadable = Render(EndpointState.Unplugged) with { ContainerId = Guid.Empty, EndpointId = "{0.0.0.00000000}.{unreadable}" };
        Device(Capture(EndpointState.Unplugged), unreadable);
        _path.ReadSteps = [StepOutcomes.FromHResult(CoreAudioEndpointReader.ContainerStep + ":" + unreadable.EndpointId, EFail)];

        ConnectResult result = await _controller.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

        Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
        Assert.AreEqual(ConnectMessages.CouldNotReadDevices, result.UserMessage);
        Assert.IsEmpty(_path.Sends);
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
