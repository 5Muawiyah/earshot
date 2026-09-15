using System.Globalization;
using System.Runtime.ExceptionServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio.Connect;

// What the controller decided from the endpoints it read, before anything was sent.
internal enum ConnectDecision
{
    InvalidContainer,  // Guid.Empty or the PC container: never a target
    ReadFailed,        // the endpoints could not be enumerated
    NotFound,          // no endpoint in the container, or no render endpoint among them
    NodesBlocked,      // connect, and every render endpoint is NOTPRESENT: the block coordinator allows first
    OutputDisabled,    // connect, and no render endpoint can become ACTIVE because one is DISABLED in Sound settings
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
//   endpoints could not be read                        Failed, could not read the audio devices
//   connect with every render endpoint NOTPRESENT      NodesBlocked, nothing sent
//   connect with every render endpoint DISABLED or     Failed, output turned off, nothing sent
//   NOTPRESENT, one DISABLED
//   already in the wanted state                        Confirmed, nothing sent
//   no filter returned S_OK                            NoFiltersResponded, every HRESULT in the steps; or
//                                                      Failed, went away, when AUDCLNT_E_DEVICE_INVALIDATED
//                                                      came from a render endpoint or the A2DP filter
//   the walk threw and no filter returned S_OK         the exception is logged with every step and rethrown
//   S_OK and the state was observed in time            Confirmed
//   S_OK, then a newer snapshot shows the state can    Failed: output turned off when a render endpoint is
//   no longer come                                     DISABLED during a connect, went away otherwise
//   S_OK, no state change in time                      AttemptedTimedOut
// Connected and Disconnected are only ever reported from an observed endpoint state, never from an HRESULT.
//
// Cancellation. A token cancelled before the work item starts, or while it reads the endpoints, stops the
// operation before anything is sent. Once a request is sent it is not recalled: cancellation then ends only the
// wait, the requests already sent are logged, and OperationCanceledException is thrown.
internal sealed class ConnectionController : IConnectionController
{
    internal const string GuardStep = "connect-target";

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

        WorkerPass pass = await _worker.RunAsync(token => PassOnWorker(action, container, token), ct).ConfigureAwait(false);
        EndpointRead read = pass.Read;

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
            _log.Error(name + ": the walk to the filters stopped with an exception. Steps so far: " + Describe(steps), fault);
            if (!send.AnyAccepted)
            {
                // Nothing reached a driver as an accepted request, so the exception is surfaced as it is.
                ExceptionDispatchInfo.Throw(fault);
            }
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
            confirmation = await _waiter.WaitAsync(container, action, ConfirmationWaiter.TimeoutFor(action), pass.StartedUtc, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Info(name + ": the wait was cancelled. Requests already sent stay sent: " + Describe(steps));
            throw;
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
            return ConnectDecision.NotFound;
        }

        if (ConfirmationWaiter.IsReached(read.Endpoints, action))
        {
            return ConnectDecision.AlreadyInState;
        }

        if (action == ConnectAction.Connect && ConfirmationWaiter.IsUnreachable(read.Endpoints, action))
        {
            // NOTPRESENT covers an adapter devnode that is disabled, which is what the boot block does. When every
            // render endpoint is NOTPRESENT the A2DP filter is not there, whatever the Hands-Free side shows, and a
            // request sent to the Hands-Free filter alone could page the AirPods over Hands-Free with no render
            // endpoint to confirm it. Nothing is sent and the coordinator allows first. A render endpoint DISABLED
            // in Sound settings cannot become ACTIVE either, so nothing is sent for it.
            // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
            return render.All(e => e.State == EndpointState.NotPresent) ? ConnectDecision.NodesBlocked : ConnectDecision.OutputDisabled;
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
        bool outputOff = action == ConnectAction.Connect &&
                         last.Any(e => e.Flow == EndpointFlow.Render && e.State == EndpointState.Disabled);
        return outputOff ? ConnectMessages.OutputTurnedOff : ConnectMessages.WentAway;
    }

    // Audio worker thread only.
    private WorkerPass PassOnWorker(ConnectAction action, Guid container, CancellationToken token)
    {
        DateTimeOffset startedUtc = _time.GetUtcNow();
        EndpointRead read = _path.ReadEndpoints(container);
        ConnectDecision decision = Decide(action, read);
        if (decision != ConnectDecision.Send)
        {
            return new WorkerPass(read, decision, null, startedUtc);
        }

        // A newer click may have cancelled this one while the endpoints were read. Nothing has been sent yet.
        token.ThrowIfCancellationRequested();
        KsSendResult send = _path.Send(container, read.Endpoints, action, FilterChoice.All);
        return new WorkerPass(read, decision, send, startedUtc);
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
    private sealed record WorkerPass(EndpointRead Read, ConnectDecision Decision, KsSendResult? Send, DateTimeOffset StartedUtc);
}
