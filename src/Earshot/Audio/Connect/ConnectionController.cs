using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio.Connect;

// What the controller decided from the endpoints it read, before anything was sent.
internal enum ConnectDecision
{
    InvalidContainer,  // Guid.Empty or the PC container: never a target
    ReadFailed,        // the endpoints could not be enumerated, or a read failure could have hidden one
    NotFound,          // no endpoint in the container, or no render endpoint among them
    NodesBlocked,      // connect, and every render endpoint is NOTPRESENT: the block coordinator allows first
    OutputDisabled,    // connect: no render endpoint can become ACTIVE because one is DISABLED in Sound settings;
                       // disconnect: every render endpoint is DISABLED, so no disconnect could be observed
    AlreadyInState,    // the endpoints are already in the wanted state
    Send,              // the request was sent
}

// Everything one connect or disconnect saw, for the log and for diag evidence.
//   StartedUtc     when the operation began
//   RequestedUtc   when the worker item that read the endpoints (and sent the request) began, or null when the
//                  container was refused before any read
internal sealed record ConnectReport(
    ConnectAction Action,
    Guid Container,
    ConnectDecision Decision,
    ConnectResult Result,
    EndpointRead? Before,
    KsSendResult? Send,
    ConfirmationResult? Confirmation,
    DateTimeOffset StartedUtc,
    DateTimeOffset? RequestedUtc,
    DateTimeOffset FinishedUtc);

// A connect or disconnect cancelled once the walk to the filters had begun, so a request may have gone out.
// Steps holds every step up to then: the per-filter ks-reconnect or ks-disconnect steps (KsConnectPath.RoleOfStep
// reads their role), accepted, refused or not sent because of the cancel, or only the ks-pass step when the
// driver call had not returned. A plain OperationCanceledException means nothing was sent.
internal sealed class ConnectCancelledException : OperationCanceledException
{
    public ConnectCancelledException()
        : this([], null, CancellationToken.None)
    {
    }

    public ConnectCancelledException(string message)
        : base(message)
    {
        Steps = [];
    }

    public ConnectCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
        Steps = [];
    }

    public ConnectCancelledException(IReadOnlyList<StepOutcome> steps, Exception? inner, CancellationToken token)
        : base("The connect or disconnect was cancelled after the walk to the filters began.", inner, token)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Steps = steps.ToArray();
    }

    public IReadOnlyList<StepOutcome> Steps { get; }
}

// Connect and disconnect over Core Audio and IKsControl (design section F). No elevation.
//
//   1. On the audio worker, in one work item: enumerate the endpoints and keep the container's, decide, and when
//      the decision is to send, walk to the filters and send KSPROPERTY_ONESHOT_RECONNECT (connect) or
//      KSPROPERTY_ONESHOT_DISCONNECT (disconnect) to each filter that passes the guard: the A2DP filter first,
//      then Hands-Free. Every COM object is made and released inside that item.
//   2. Off the worker: wait for the endpoints to reach the wanted state (ConfirmationWaiter). The worker is never
//      blocked on a notification, because notifications are processed by later worker items.
//
// Outcomes (messages from ConnectMessages):
//   container never a target, no endpoints, or no      NotFound
//   render endpoint
//   endpoints could not be read, or a failed read      Failed, could not read the audio devices
//   could have hidden the device's own endpoint
//   connect with every render endpoint NOTPRESENT      NodesBlocked, nothing sent
//   connect with every render endpoint DISABLED or     Failed, output turned off, nothing sent
//   NOTPRESENT, one DISABLED; disconnect with every
//   render endpoint DISABLED
//   already in the wanted state                        Confirmed, nothing sent
//   no filter returned S_OK                            NoFiltersResponded, every HRESULT in the steps; or
//                                                      Failed, went away, when AUDCLNT_E_DEVICE_INVALIDATED
//                                                      came from a render endpoint or the A2DP filter
//   the walk threw and no filter returned S_OK         the same, with the exception logged and its HResult on
//                                                      the filter's visit-filter step, so the caller sees what
//                                                      each filter did rather than an exception
//   S_OK and the state was observed in time            Confirmed
//   S_OK, then a newer snapshot shows the state can    Failed: output turned off when a render endpoint is
//   no longer come or be seen                          DISABLED, went away otherwise
//   S_OK, no state change in time                      AttemptedTimedOut
// Connected and Disconnected are only ever reported from an observed endpoint state, never from an HRESULT.
//
// Cancellation. A token cancelled before the work item starts, or while it reads the endpoints, stops the
// operation before anything is sent, and a plain OperationCanceledException is thrown. Cancelled between two
// filters, it stops the walk before the next one. Once the walk has begun, a request sent is not recalled: the
// steps are logged and ConnectCancelledException (an OperationCanceledException) is thrown carrying every step,
// whether a filter accepted, refused or was sent nothing, so the caller sees what went out before it decides
// what to undo, and never mistakes a cancelled walk for "no filter responded".
//
// A stalled driver. KsProperty is a synchronous call on the single audio worker and Microsoft documents no
// latency for it, so the pass is awaited with a budget (PassBudget). The work item runs on a token the budget
// cancels too, so once the budget has run out it sends nothing to a later filter, and an item still queued
// behind an earlier stalled call never starts. If the item had not begun, nothing was sent: Failed, the driver
// is busy. If it had begun and not returned: AttemptedTimedOut, or ConnectCancelledException with the ks-pass
// step when the caller's token was cancelled too. The item itself is not torn down (the call is inside a driver)
// and the worker stays busy until it returns; the owner's live tests time the real call.
internal sealed class ConnectionController : IConnectionController
{
    internal const string GuardStep = "connect-target";
    internal const string StalledStep = "ks-pass";

    // How long the work item that reads the endpoints and sends the request is awaited. A waiting budget, not a
    // measured figure: it is longer than a connect confirmation wait, so a driver that returns at all is never
    // cut short by it.
    internal static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(20);

    private readonly IAudioWorker _worker;
    private readonly IKsConnectPath _path;
    private readonly ConfirmationWaiter _waiter;
    private readonly ILog _log;
    private readonly TimeProvider _time;

    public ConnectionController(AudioWorker worker, IDeviceMonitor monitor, ILog log)
        : this(worker, new KsConnectPath(worker), new ConfirmationWaiter(monitor, log), log, TimeProvider.System)
    {
    }

    internal ConnectionController(IAudioWorker worker, IKsConnectPath path, ConfirmationWaiter waiter, ILog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _worker = worker;
        _path = path;
        _waiter = waiter;
        _log = log;
        _time = time;
    }

    public async Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default) =>
        (await RunAsync(ConnectAction.Connect, containerId, ct).ConfigureAwait(false)).Result;

    public async Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default) =>
        (await RunAsync(ConnectAction.Disconnect, containerId, ct).ConfigureAwait(false)).Result;

    internal async Task<ConnectReport> RunAsync(ConnectAction action, Guid container, CancellationToken ct)
    {
        DateTimeOffset started = _time.GetUtcNow();
        string name = Name(action) + " " + TopologyWalk.Format(container);

        if (!NodeMatch.IsValidTargetContainer(container))
        {
            StepOutcome refused = StepOutcomes.NotAttempted(GuardStep, "The container " + TopologyWalk.Format(container) + " is never a target.");
            return Finish(action, container, ConnectDecision.InvalidContainer, ConnectOutcome.NotFound, ConnectMessages.NotFound,
                new[] { refused }, null, null, null, started, null);
        }

        WorkerPass? pass = null;
        ConnectReport? stopped = null;
        var progress = new PassProgress();
        using (var budget = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time))
        using (var passCancel = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token))
        {
            Task<WorkerPass> passing = _worker.RunAsync(
                token => progress.TryStart() ? PassOnWorker(action, container, token) : throw new OperationCanceledException(token),
                passCancel.Token);
            budget.CancelAfter(PassBudget);
            try
            {
                // Only the budget ends this wait: an item that has begun is awaited after a cancel, so its steps come back.
                pass = await passing.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (budget.IsCancellationRequested || ct.IsCancellationRequested)
            {
                // From here the pass is given up. The item's token is cancelled before anything is decided, and an
                // item that has not begun by then never does, so "began" cannot change after it is read.
                await passCancel.CancelAsync().ConfigureAwait(false);
                bool began = progress.Abandon();
                string seconds = PassBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
                if (passing.IsCompletedSuccessfully)
                {
                    pass = await passing.ConfigureAwait(false);
                }
                else if (!began || passing.IsCanceled)
                {
                    // Nothing was sent: the item never ran, or it stopped at the token check before the walk.
                    ct.ThrowIfCancellationRequested();
                    StepOutcome busy = StepOutcomes.NotAttempted(StalledStep, began
                        ? "The audio devices were not read within " + seconds + " s, so no request was sent."
                        : "The audio worker was still busy with an earlier request after " + seconds + " s, so no request was sent.");
                    stopped = Finish(action, container, ConnectDecision.ReadFailed, ConnectOutcome.Failed, ConnectMessages.DriverBusy,
                        new[] { busy }, null, null, null, started, null);
                }
                else
                {
                    // The item is still inside the driver; it is left to finish, and every later item queues behind
                    // it. Nothing here can say whether the request went out.
                    StepOutcome stalled = StepOutcomes.NotAttempted(StalledStep,
                        "The audio driver did not answer within " + seconds + " s, so whether the request went out is not known.");
                    if (ct.IsCancellationRequested)
                    {
                        _log.Info(name + ": cancelled while the driver call was still running. " + Describe(new[] { stalled }));
                        throw new ConnectCancelledException(new[] { stalled }, ex, ct);
                    }

                    stopped = Finish(action, container, ConnectDecision.Send, ConnectOutcome.AttemptedTimedOut,
                        action == ConnectAction.Connect ? ConnectMessages.StillConnecting : ConnectMessages.DidNotDisconnect,
                        new[] { stalled }, null, null, null, started, null);
                }
            }
        }

        if (stopped is not null)
        {
            return stopped;
        }

        EndpointRead read = pass!.Read;

        switch (pass.Decision)
        {
            case ConnectDecision.ReadFailed:
                return Finish(action, container, pass.Decision, ConnectOutcome.Failed, ConnectMessages.CouldNotReadDevices,
                    read.FailedSteps, read, null, null, started, pass.StartedUtc);

            case ConnectDecision.NotFound:
                return Finish(action, container, pass.Decision, ConnectOutcome.NotFound, ConnectMessages.NotFound,
                    read.FailedSteps, read, null, null, started, pass.StartedUtc);

            case ConnectDecision.NodesBlocked:
                return Finish(action, container, pass.Decision, ConnectOutcome.NodesBlocked, ConnectMessages.AllowingFirst,
                    read.ContainerSteps, read, null, null, started, pass.StartedUtc);

            case ConnectDecision.OutputDisabled:
                return Finish(action, container, pass.Decision, ConnectOutcome.Failed, ConnectMessages.OutputTurnedOff,
                    read.ContainerSteps, read, null, null, started, pass.StartedUtc);

            case ConnectDecision.AlreadyInState:
                return Finish(action, container, pass.Decision, ConnectOutcome.Confirmed, ConfirmedMessage(action),
                    read.ContainerSteps, read, null, null, started, pass.StartedUtc);
        }

        KsSendResult send = pass.Send!;
        var steps = new List<StepOutcome>(read.ContainerSteps);
        steps.AddRange(send.Steps);

        if (send.Fault is Exception fault)
        {
            // Logged and surfaced, never rethrown: the caller needs the per-filter steps (which filter was sent
            // what, and what it answered) to tell "the A2DP filter refused" from "nothing responded".
            _log.Error(name + ": the walk to the filters stopped with an exception. Steps: " + Describe(steps), fault);
        }

        if (ct.IsCancellationRequested)
        {
            // A newer intent cancelled this one during the walk. A filter refusing before the cancel is not "no filter
            // responded": the filters after it were sent nothing only because of the cancel.
            _log.Info(name + ": cancelled during the walk to the filters. Requests already sent stay sent: " + Describe(steps));
            throw new ConnectCancelledException(steps, null, ct);
        }

        if (!send.AnyAccepted)
        {
            return RenderSideInvalidated(read, send)
                ? Finish(action, container, pass.Decision, ConnectOutcome.Failed, ConnectMessages.WentAway, steps, read, send, null, started, pass.StartedUtc)
                : Finish(action, container, pass.Decision, ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver, steps, read, send, null, started, pass.StartedUtc);
        }

        _log.Info(name + ": request accepted by " +
                  string.Join(", ", send.Filters.Where(f => f.Accepted).Select(f => f.Name)) + "; waiting for the endpoint state.");

        ConfirmationResult confirmation;
        try
        {
            confirmation = await _waiter.WaitAsync(container, action, ConfirmationWaiter.TimeoutFor(action), pass.Sequence, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _log.Info(name + ": the wait was cancelled. Requests already sent stay sent: " + Describe(steps));
            throw new ConnectCancelledException(steps, ex, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(name + ": the wait for the endpoint state failed after " + Describe(steps), ex);
            throw;
        }

        if (confirmation.Reached)
        {
            return Finish(action, container, pass.Decision, ConnectOutcome.Confirmed, ConfirmedMessage(action),
                steps, read, send, confirmation, started, pass.StartedUtc);
        }

        if (confirmation.Unreachable)
        {
            return Finish(action, container, pass.Decision, ConnectOutcome.Failed, UnreachableMessage(action, confirmation, container),
                steps, read, send, confirmation, started, pass.StartedUtc);
        }

        return Finish(action, container, pass.Decision, ConnectOutcome.AttemptedTimedOut,
            action == ConnectAction.Connect ? ConnectMessages.StillConnecting : ConnectMessages.DidNotDisconnect,
            steps, read, send, confirmation, started, pass.StartedUtc);
    }

    // The steps of an enumeration that worked but could have left an endpoint out of its device: an item, id or
    // flow that could not be read drops the endpoint, and a property store or container id that could not be read
    // moves it into the Guid.Empty group. Any of those could have been the device's own render endpoint.
    private static readonly string[] EndpointHidingSteps =
    [
        CoreAudioEndpointReader.ItemStep,
        CoreAudioEndpointReader.IdStep,
        CoreAudioEndpointReader.FlowStep,
        CoreAudioEndpointReader.StoreStep,
        CoreAudioEndpointReader.ContainerStep,
    ];

    // The decision from one read, before anything is sent.
    internal static ConnectDecision Decide(ConnectAction action, EndpointRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!read.Ok)
        {
            return ConnectDecision.ReadFailed;
        }

        // Only a render endpoint's state can confirm a request (ConfirmationWaiter), so a container without one is
        // sent nothing. That also keeps a container holding only a Hands-Free capture endpoint, which is what a
        // paired phone would show, from ever being sent a request.
        List<AudioEndpoint> render = read.Endpoints.Where(e => e.Flow == EndpointFlow.Render).ToList();
        if (render.Count == 0)
        {
            // "Not found" is a claim about the device; an unreadable endpoint is a claim about the read.
            return HidesAnEndpoint(read) ? ConnectDecision.ReadFailed : ConnectDecision.NotFound;
        }

        if (ConfirmationWaiter.IsReached(read.Endpoints, action))
        {
            return ConnectDecision.AlreadyInState;
        }

        if (ConfirmationWaiter.IsUnreachable(read.Endpoints, action))
        {
            // NOTPRESENT covers an adapter devnode that is disabled, which is what the boot block does. When every
            // render endpoint is NOTPRESENT the A2DP filter is not there, whatever the Hands-Free side shows, and a
            // request sent to the Hands-Free filter alone could page the AirPods over Hands-Free with no render
            // endpoint to confirm it. Nothing is sent and the coordinator allows first. A render endpoint DISABLED
            // in Sound settings cannot become ACTIVE, and while every one is DISABLED a disconnect cannot be seen
            // either, so nothing is sent for it and the message says why rather than claim a state.
            // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
            return action == ConnectAction.Connect && render.All(e => e.State == EndpointState.NotPresent)
                ? ConnectDecision.NodesBlocked
                : ConnectDecision.OutputDisabled;
        }

        return ConnectDecision.Send;
    }

    // True when AUDCLNT_E_DEVICE_INVALIDATED came from the A2DP side: a filter reached from a render endpoint, or
    // a step naming one of the container's render endpoints. The Hands-Free filter can go (Protect audio quality
    // removes it) without the device going, so an invalidated Hands-Free filter alone is not "went away".
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-activate
    internal static bool RenderSideInvalidated(EndpointRead read, KsSendResult send)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(send);
        const int Invalidated = CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED;
        List<string> renderIds = read.Endpoints.Where(e => e.Flow == EndpointFlow.Render).Select(e => e.EndpointId).ToList();

        bool NamesRender(StepOutcome step) => renderIds.Any(id => step.Step.EndsWith(":" + id, StringComparison.Ordinal));

        return send.Filters.Any(f => f.Role == FilterRole.A2dp && (f.Hr == Invalidated || f.Visit.Steps.Any(s => s.Code == Invalidated))) ||
               send.Steps.Any(s => s.Code == Invalidated && NamesRender(s)) ||
               read.ContainerSteps.Any(s => s.Code == Invalidated && NamesRender(s));
    }

    // True when a failed read could have kept an endpoint out of the container's list.
    internal static bool HidesAnEndpoint(EndpointRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.FailedSteps.Any(s => EndpointHidingSteps.Any(prefix =>
            s.Step.StartsWith(prefix + ":", StringComparison.Ordinal) || string.Equals(s.Step, prefix, StringComparison.Ordinal)));
    }

    internal static string Describe(IEnumerable<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        List<string> parts = steps.Select(CoreAudioDeviceMonitor.Describe).ToList();
        return parts.Count == 0 ? "no steps" : string.Join("; ", parts);
    }

    private static string Name(ConnectAction action) => action == ConnectAction.Connect ? "Connect" : "Disconnect";

    private static string ConfirmedMessage(ConnectAction action) =>
        action == ConnectAction.Connect ? ConnectMessages.Connected : ConnectMessages.Disconnected;

    private static string UnreachableMessage(ConnectAction action, ConfirmationResult confirmation, Guid container)
    {
        IReadOnlyList<AudioEndpoint> last = confirmation.LastObserved is DeviceSnapshot snapshot
            ? ConfirmationWaiter.EndpointsOf(snapshot, container)
            : Array.Empty<AudioEndpoint>();
        bool outputOff = last.Any(e => e.Flow == EndpointFlow.Render && e.State == EndpointState.Disabled);
        return outputOff ? ConnectMessages.OutputTurnedOff : ConnectMessages.WentAway;
    }

    // Audio worker thread only.
    private WorkerPass PassOnWorker(ConnectAction action, Guid container, CancellationToken token)
    {
        DateTimeOffset startedUtc = _time.GetUtcNow();

        // The monitor numbers its enumerations on this same worker, so every enumeration after this item carries
        // a higher number: that is what tells evidence of this request from evidence of what came before it.
        long sequence = _waiter.CurrentSequence;
        EndpointRead read = _path.ReadEndpoints(container);
        ConnectDecision decision = Decide(action, read);
        if (decision != ConnectDecision.Send)
        {
            return new WorkerPass(read, decision, null, startedUtc, sequence);
        }

        // A newer click may have cancelled this one while the endpoints were read. Nothing has been sent yet.
        token.ThrowIfCancellationRequested();
        KsSendResult send = _path.Send(container, read.Endpoints, action, FilterChoice.All, token);
        return new WorkerPass(read, decision, send, startedUtc, sequence);
    }

    private ConnectReport Finish(
        ConnectAction action,
        Guid container,
        ConnectDecision decision,
        ConnectOutcome outcome,
        string message,
        IReadOnlyList<StepOutcome> steps,
        EndpointRead? read,
        KsSendResult? send,
        ConfirmationResult? confirmation,
        DateTimeOffset started,
        DateTimeOffset? requested)
    {
        var result = new ConnectResult(outcome, message, steps);
        var report = new ConnectReport(action, container, decision, result, read, send, confirmation, started, requested, _time.GetUtcNow());

        string line = Name(action) + " " + TopologyWalk.Format(container) + ": " + outcome + " (" + decision + ")";
        if (confirmation is not null)
        {
            string how = confirmation.Reached ? "state seen via " + confirmation.Source
                : confirmation.Unreachable ? "state can no longer come, seen via " + confirmation.Source
                : "no state change";
            line += ", " + how + " after " + confirmation.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        line += ". \"" + message + "\" Steps: " + Describe(steps);
        _log.Write(outcome is ConnectOutcome.Confirmed or ConnectOutcome.NodesBlocked ? LogLevel.Info : LogLevel.Warn, line);
        return report;
    }

    // Plain data only: this leaves the audio worker.
    private sealed record WorkerPass(EndpointRead Read, ConnectDecision Decision, KsSendResult? Send, DateTimeOffset StartedUtc, long Sequence);

    // Whether the work item began, decided under one lock with the caller giving up on it, so an item the caller
    // has already reported on never starts afterwards.
    private sealed class PassProgress
    {
        private readonly Lock _gate = new();
        private bool _started;
        private bool _abandoned;

        // Worker side: false when the caller has given up, and the item must not run.
        public bool TryStart()
        {
            lock (_gate)
            {
                if (_abandoned)
                {
                    return false;
                }

                _started = true;
                return true;
            }
        }

        // Caller side: gives up on the item; true when it had already begun.
        public bool Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                return _started;
            }
        }
    }
}
