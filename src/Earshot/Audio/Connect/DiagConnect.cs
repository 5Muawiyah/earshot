using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot;

// diag connect | diag disconnect | diag ks <reconnect|disconnect> <src|wave|all> [buffer4]
//
// LIVE. These send KSPROPSETID_BtAudio requests to the owner's device and exist only for the owner's live tests
// (research unknowns: whether the A2DP filter honours the one-shot requests, the HRESULTs, whether a driver wants
// a data buffer, the latency to the endpoint state change, and the thread and apartment of the notification
// callbacks). Program.RunDiag refuses every target in safe mode before these run, and each target refuses again
// on its own when the services it is given are in safe mode, before any worker item is queued. They are never
// run by a build, and a test only ever calls them with safe-mode services.
//
//   connect, disconnect   the full controller path (ConnectionController), exactly as a left click runs it
//   ks                    the request only, to the chosen filters, through the same walk and guard; no decision
//                         about the current state is made, so a request can be sent in the "wrong" state on
//                         purpose. The endpoint state is then observed for the connect timeout. src is the
//                         filter reached from a render endpoint (A2DP), wave the one reached only from a capture
//                         endpoint (Hands-Free). buffer4 sends a 4-byte zeroed data buffer instead of none.
//
// Each writes one JSON evidence file (UTC timestamps, per-filter HRESULT, name, role and call duration, time to
// the state change, and every endpoint notification with the thread id and apartment it arrived on) and a short
// summary to Out.
// The notifications are recorded by a second notification client registered on the audio worker's enumerator
// for the length of the run; the monitor is started too, so the confirmation wait sees SnapshotChanged.
internal static partial class Program
{
    internal const string DiagBufferSwitch = "buffer4";

    internal sealed record DiagKsArguments(ConnectAction Action, FilterChoice Filters, KsPayload Payload = KsPayload.None);

    // Everything one diag connect, disconnect or ks run saw.
    internal sealed record DiagConnectEvidence(
        string Target,
        IReadOnlyList<string> Args,
        ConnectAction Action,
        FilterChoice Filters,
        DateTimeOffset StartedUtc,
        DateTimeOffset FinishedUtc,
        Guid Container,
        KsPayload Payload,
        DeviceSnapshot? Before,
        DeviceSnapshot? After,
        ConnectReport? Report,
        EndpointRead? Read,
        KsSendResult? Send,
        ConfirmationResult? Confirmation,
        DateTimeOffset? RequestedUtc,
        IReadOnlyList<RecordedNotification> Notifications,
        IReadOnlyList<StepOutcome> RecorderSteps,
        string? Error);

    static partial void DiagConnect(DiagContext ctx) => RunDiagController(ctx, ConnectAction.Connect);

    static partial void DiagDisconnect(DiagContext ctx) => RunDiagController(ctx, ConnectAction.Disconnect);

    static partial void DiagKs(DiagContext ctx) => RunDiagKs(ctx);

    internal static void RunDiagKs(DiagContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        ServiceRegistry services = ctx.Services;
        if (RefuseDiagInSafeMode(ctx, services))
        {
            return;
        }

        if (!TryParseDiagKs(ctx.Args, out DiagKsArguments? arguments, out string? error))
        {
            ctx.Out.WriteLine(error);
            ctx.ExitCode = ExitCodes.Usage;
            return;
        }

        if (services.Worker is not AudioWorker worker)
        {
            ReportDiagUnavailable(ctx, services.Log, "the audio worker is not part of this build");
            return;
        }

        ILog log = services.Log;
        IDeviceMonitor monitor = services.Monitor;
        var path = new KsConnectPath(worker);
        var waiter = new ConfirmationWaiter(monitor, log);
        var recorder = new NotificationRecorder(TimeProvider.System);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var recorderSteps = new List<StepOutcome>();
        DeviceSnapshot? before = null;
        DeviceSnapshot? after = null;
        EndpointRead? read = null;
        KsSendResult? send = null;
        ConfirmationResult? confirmation = null;
        DateTimeOffset? requested = null;
        Guid container = Guid.Empty;
        string? failure = null;

        recorderSteps.Add(worker.RunAsync(_ => recorder.Register(worker)).GetAwaiter().GetResult());
        try
        {
            monitor.Start();
            before = monitor.RefreshAsync().GetAwaiter().GetResult();
            container = before.Target?.ContainerId ?? Guid.Empty;
            if (!NodeMatch.IsValidTargetContainer(container))
            {
                failure = "No target device was found, so nothing was sent.";
            }
            else
            {
                (read, send, requested) = worker.RunAsync(_ =>
                {
                    DateTimeOffset at = DateTimeOffset.UtcNow;
                    EndpointRead endpoints = path.ReadEndpoints(container);
                    KsSendResult? sent = endpoints.Ok && endpoints.Endpoints.Count > 0
                        ? path.Send(container, endpoints.Endpoints, arguments.Action, arguments.Filters, arguments.Payload)
                        : null;
                    return (endpoints, sent, (DateTimeOffset?)at);
                }).GetAwaiter().GetResult();

                if (send?.Fault is Exception fault)
                {
                    log.Error("diag ks " + string.Join(' ', ctx.Args) + ": the walk to the filters stopped with an exception. Steps: " +
                              ConnectionController.Describe(send.Steps), fault);
                }

                if (send is not null && send.Filters.Any(f => f.Sent))
                {
                    // Watch for the connect timeout whatever the HRESULTs were, so a change after a refused request
                    // is recorded too.
                    TimeSpan window = ConfirmationWaiter.TimeoutFor(ConnectAction.Connect);
                    confirmation = waiter.WaitAsync(container, arguments.Action, window, requested!.Value).GetAwaiter().GetResult();
                    TimeSpan remaining = requested.Value + window - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        Task.Delay(remaining).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    failure = "No request was sent: " + NothingSentReason(read, send);
                }
            }

            after = monitor.RefreshAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failure = "The run stopped: " + ex.GetType().Name + ": " + ex.Message;
            log.Error("diag ks " + string.Join(' ', ctx.Args) + " stopped.", ex);
        }
        finally
        {
            recorderSteps.Add(worker.RunAsync(_ => recorder.Unregister(worker)).GetAwaiter().GetResult());
        }

        var evidence = new DiagConnectEvidence("ks", ctx.Args, arguments.Action, arguments.Filters, started, DateTimeOffset.UtcNow,
            container, arguments.Payload, before, after, null, read, send, confirmation, requested, recorder.Entries, recorderSteps, failure);
        string file = ctx.NewEvidenceFile("ks-" + string.Join('-', ctx.Args));
        File.WriteAllText(file, DiagConnectJson(evidence));

        foreach (FilterSend filter in send?.Filters ?? Array.Empty<FilterSend>())
        {
            ctx.Out.WriteLine(filter.Step.Step + " (" + KsConnectPath.RoleName(filter.Role) + "): " + filter.Step.CodeName);
        }

        if (send?.Fault is Exception stopped)
        {
            ctx.Out.WriteLine("The walk stopped: " + stopped.GetType().Name + ": " + stopped.Message);
        }

        ctx.Out.WriteLine(failure ?? (confirmation is { Reached: true }
            ? "State reached after " + Milliseconds(confirmation.Elapsed) + " ms."
            : confirmation is { Unreachable: true }
                ? "The state could no longer be reached after " + Milliseconds(confirmation.Elapsed) + " ms."
                : "No state change within the window."));
        ctx.Out.WriteLine("Evidence: " + file);
        log.Info("diag ks " + string.Join(' ', ctx.Args) + ": evidence " + file);
        ctx.ExitCode = failure is null && send is { Fault: null } && send.Filters.Any(f => f.Sent) ? ExitCodes.Ok : ExitCodes.Software;
    }

    // Refuses a diag target when the services are in safe mode. Program.RunDiag already refuses every target in
    // safe mode; this is the target's own check, so a live request never rests on one check alone.
    internal static bool RefuseDiagInSafeMode(DiagContext ctx, ServiceRegistry services)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(services);
        if (!services.SafeMode)
        {
            return false;
        }

        services.Log.Warn(SafeDecorators.Message + " Refused: diag " + ctx.Target + ".");
        ctx.Out.WriteLine(SafeDecorators.Message);
        ctx.ExitCode = ExitCodes.Refused;
        return true;
    }

    // Why diag ks sent nothing, from what it saw. Per chosen filter the reason the walk recorded, so a filter whose
    // IKsControl could not be activated is not reported as refused by the guard.
    internal static string NothingSentReason(EndpointRead? read, KsSendResult? send)
    {
        if (read is { Ok: false })
        {
            return "the endpoints could not be read.";
        }

        if (send is null)
        {
            return "the device has no endpoints.";
        }

        StepOutcome? enumerator = send.Steps.FirstOrDefault(s => s.Step == AudioWorker.Steps.CreateEnumerator);
        if (enumerator is not null)
        {
            return "the audio device enumerator could not be created (" + enumerator.CodeName + ").";
        }

        if (send.Filters.Count == 0)
        {
            return "no chosen filter was found.";
        }

        return string.Join("; ", send.Filters.Select(f => f.Name + ": " + (f.NotSentReason ?? "the request was not made.").TrimEnd('.'))) + ".";
    }

    // diag ks arguments: <reconnect|disconnect> <src|wave|all> [buffer4]. Program.TryParseDiagArgs checks the
    // grammar first; this turns the words into the request and refuses anything else again rather than guess.
    internal static bool TryParseDiagKs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out DiagKsArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        parsed = null;
        if (args.Count is not (2 or 3))
        {
            error = "diag ks needs <reconnect|disconnect> <src|wave|all> [buffer4].";
            return false;
        }

        if (args.Count == 3 && args[2] != DiagBufferSwitch)
        {
            error = "diag ks only takes buffer4 after the filter.";
            return false;
        }

        ConnectAction? action = args[0] switch
        {
            "reconnect" => ConnectAction.Connect,
            "disconnect" => ConnectAction.Disconnect,
            _ => null,
        };

        FilterChoice? filters = args[1] switch
        {
            "src" => FilterChoice.Src,
            "wave" => FilterChoice.Wave,
            "all" => FilterChoice.All,
            _ => null,
        };

        if (action is null)
        {
            error = "diag ks action must be reconnect or disconnect.";
            return false;
        }

        if (filters is null)
        {
            error = "diag ks filter must be src, wave or all.";
            return false;
        }

        parsed = new DiagKsArguments(action.Value, filters.Value, args.Count == 3 ? KsPayload.ZeroedFourBytes : KsPayload.None);
        error = null;
        return true;
    }

    // diag connect and diag disconnect take no arguments.
    internal static bool TryParseDiagController(IReadOnlyList<string> args, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = args.Count == 0 ? null : "diag connect and diag disconnect take no arguments.";
        return error is null;
    }

    internal static void RunDiagController(DiagContext ctx, ConnectAction action)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Handled = true;
        string target = action == ConnectAction.Connect ? "connect" : "disconnect";
        ServiceRegistry services = ctx.Services;
        if (RefuseDiagInSafeMode(ctx, services))
        {
            return;
        }

        if (!TryParseDiagController(ctx.Args, out string? error))
        {
            ctx.Out.WriteLine(error);
            ctx.ExitCode = ExitCodes.Usage;
            return;
        }

        if (services.Worker is not AudioWorker worker || services.Connection is not ConnectionController controller)
        {
            ReportDiagUnavailable(ctx, services.Log, "the connection controller is not part of this build");
            return;
        }

        ILog log = services.Log;
        IDeviceMonitor monitor = services.Monitor;
        var recorder = new NotificationRecorder(TimeProvider.System);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var recorderSteps = new List<StepOutcome>();
        DeviceSnapshot? before = null;
        DeviceSnapshot? after = null;
        ConnectReport? report = null;
        Guid container = Guid.Empty;
        string? failure = null;

        recorderSteps.Add(worker.RunAsync(_ => recorder.Register(worker)).GetAwaiter().GetResult());
        try
        {
            monitor.Start();
            before = monitor.RefreshAsync().GetAwaiter().GetResult();
            container = before.Target?.ContainerId ?? Guid.Empty;
            report = controller.RunAsync(action, container, CancellationToken.None).GetAwaiter().GetResult();
            after = monitor.RefreshAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failure = "The run stopped: " + ex.GetType().Name + ": " + ex.Message;
            log.Error("diag " + target + " stopped.", ex);
        }
        finally
        {
            recorderSteps.Add(worker.RunAsync(_ => recorder.Unregister(worker)).GetAwaiter().GetResult());
        }

        var evidence = new DiagConnectEvidence(target, ctx.Args, action, FilterChoice.All, started, DateTimeOffset.UtcNow,
            container, KsPayload.None, before, after, report, report?.Before, report?.Send, report?.Confirmation, report?.RequestedUtc,
            recorder.Entries, recorderSteps, failure);
        string file = ctx.NewEvidenceFile(target);
        File.WriteAllText(file, DiagConnectJson(evidence));

        ctx.Out.WriteLine(report is null
            ? failure
            : target + ": " + report.Result.Outcome + " (" + report.Decision + "), \"" + report.Result.UserMessage + "\"");
        foreach (StepOutcome step in report?.Result.Steps ?? Array.Empty<StepOutcome>())
        {
            ctx.Out.WriteLine("  " + CoreAudioDeviceMonitor.Describe(step));
        }

        ctx.Out.WriteLine("Evidence: " + file);
        log.Info("diag " + target + ": evidence " + file);
        ctx.ExitCode = report is { Result.Confirmed: true } ? ExitCodes.Ok : ExitCodes.Software;
    }

    private static void ReportDiagUnavailable(DiagContext ctx, ILog log, string why)
    {
        log.Warn("diag " + ctx.Target + ": " + why + ".");
        ctx.Out.WriteLine(NotAvailableMessage);
        ctx.ExitCode = ExitCodes.Unavailable;
    }

    // The first endpoint notification that put one of the container's render endpoints into the wanted state,
    // or null. Render endpoint ids come from the snapshots before and after the run.
    internal static RecordedNotification? FirstWantedNotification(
        IEnumerable<RecordedNotification> notifications, IEnumerable<string> renderEndpointIds, ConnectAction action)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(renderEndpointIds);
        var ids = new HashSet<string>(renderEndpointIds, StringComparer.OrdinalIgnoreCase);
        return notifications
            .OrderBy(n => n.Sequence)
            .FirstOrDefault(n => n.Kind == EndpointNotificationKind.StateChanged &&
                                 n.DeviceId is not null && ids.Contains(n.DeviceId) &&
                                 (action == ConnectAction.Connect
                                     ? n.NewState == CoreAudio.DEVICE_STATE_ACTIVE
                                     : n.NewState is CoreAudio.DEVICE_STATE_UNPLUGGED or CoreAudio.DEVICE_STATE_NOTPRESENT));
    }

    internal static string DiagConnectJson(DiagConnectEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        List<string> renderIds = new[] { e.Before, e.After }
            .Where(s => s is not null)
            .SelectMany(s => ConfirmationWaiter.EndpointsOf(s!, e.Container))
            .Where(x => x.Flow == EndpointFlow.Render)
            .Select(x => x.EndpointId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RecordedNotification? first = FirstWantedNotification(e.Notifications, renderIds, e.Action);

        return ProbeContext.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", e.Target);
            w.WriteStartArray("args");
            foreach (string arg in e.Args)
            {
                w.WriteStringValue(arg);
            }

            w.WriteEndArray();
            w.WriteString("action", e.Action == ConnectAction.Connect ? "reconnect" : "disconnect");
            w.WriteString("filterChoice", e.Filters switch
            {
                FilterChoice.Src => "src",
                FilterChoice.Wave => "wave",
                _ => "all",
            });
            w.WriteString("payload", e.Payload == KsPayload.ZeroedFourBytes ? DiagBufferSwitch : "none");
            w.WriteString("startedUtc", Utc(e.StartedUtc));
            w.WriteString("finishedUtc", Utc(e.FinishedUtc));
            w.WriteString("containerId", e.Container);
            WriteNullableUtc(w, "requestedUtc", e.RequestedUtc);
            w.WriteString("error", e.Error);

            if (e.Report is ConnectReport report)
            {
                w.WriteStartObject("controller");
                w.WriteString("decision", report.Decision.ToString());
                w.WriteString("outcome", report.Result.Outcome.ToString());
                w.WriteString("userMessage", report.Result.UserMessage);
                w.WriteBoolean("confirmed", report.Result.Confirmed);
                w.WriteNumber("totalMilliseconds", Milliseconds(report.FinishedUtc - report.StartedUtc));
                WriteSteps(w, "steps", report.Result.Steps);
                w.WriteEndObject();
            }

            WriteDiagEndpoints(w, "endpointsBefore", e.Before, e.Container);
            WriteDiagEndpoints(w, "endpointsAfter", e.After, e.Container);
            if (e.Read is EndpointRead read)
            {
                w.WriteBoolean("endpointReadOk", read.Ok);
                WriteSteps(w, "endpointReadSteps", read.ContainerSteps);
            }

            WriteDiagFilters(w, e.Send, e.RequestedUtc);
            w.WriteString("sendFault", e.Send?.Fault is Exception fault ? fault.GetType().Name + ": " + fault.Message : null);
            WriteDiagConfirmation(w, e.Confirmation);

            if (first is not null && e.RequestedUtc is DateTimeOffset requestedAt)
            {
                w.WriteNumber("millisecondsToFirstWantedStateNotification", Milliseconds(first.Utc - requestedAt));
            }
            else
            {
                w.WriteNull("millisecondsToFirstWantedStateNotification");
            }

            w.WriteStartArray("notifications");
            foreach (RecordedNotification n in e.Notifications.OrderBy(n => n.Sequence))
            {
                w.WriteStartObject();
                w.WriteNumber("sequence", n.Sequence);
                w.WriteString("utc", Utc(n.Utc));
                if (e.RequestedUtc is DateTimeOffset at)
                {
                    w.WriteNumber("millisecondsAfterRequest", Milliseconds(n.Utc - at));
                }

                w.WriteString("kind", n.Kind.ToString());
                w.WriteString("deviceId", n.DeviceId);
                w.WriteString("newState", "0x" + n.NewState.ToString("X", CultureInfo.InvariantCulture));
                w.WriteNumber("threadId", n.ThreadId);
                w.WriteString("apartment", n.Apartment.ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteSteps(w, "recorderSteps", e.RecorderSteps);
            WriteSteps(w, "sendSteps", e.Send?.Steps ?? Array.Empty<StepOutcome>());
            w.WriteEndObject();
        });
    }

    private static void WriteDiagEndpoints(Utf8JsonWriter w, string name, DeviceSnapshot? snapshot, Guid container)
    {
        if (snapshot is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("takenUtc", Utc(snapshot.TakenUtc));
        w.WriteStartArray("endpoints");
        foreach (AudioEndpoint endpoint in ConfirmationWaiter.EndpointsOf(snapshot, container))
        {
            w.WriteStartObject();
            w.WriteString("id", endpoint.EndpointId);
            w.WriteString("flow", endpoint.Flow.ToString());
            w.WriteString("state", StateText(endpoint.State));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDiagFilters(Utf8JsonWriter w, KsSendResult? send, DateTimeOffset? requested)
    {
        w.WriteStartArray("filtersFound");
        foreach (AdapterPath adapter in send?.Adapters ?? Array.Empty<AdapterPath>())
        {
            w.WriteStartObject();
            w.WriteString("adapterId", adapter.AdapterId);
            w.WriteString("name", KsConnectPath.FilterName(adapter.AdapterId));
            w.WriteString("role", KsConnectPath.RoleName(KsConnectPath.RoleOf(adapter)));
            w.WriteBoolean("fromRender", adapter.FromRender);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("filters");
        foreach (FilterSend filter in send?.Filters ?? Array.Empty<FilterSend>())
        {
            w.WriteStartObject();
            w.WriteString("adapterId", filter.Adapter.AdapterId);
            w.WriteString("name", filter.Name);
            w.WriteString("role", KsConnectPath.RoleName(filter.Role));
            w.WriteBoolean("fromRender", filter.Adapter.FromRender);
            w.WriteString("adapterState", filter.Visit.State is EndpointState state ? StateText(state) : null);
            if (filter.Visit.ContainerId is Guid adapterContainer)
            {
                w.WriteString("adapterContainerId", adapterContainer);
            }
            else
            {
                w.WriteNull("adapterContainerId");
            }

            w.WriteBoolean("guardPassed", filter.Visit.GuardPassed);
            w.WriteBoolean("ksControlActivated", filter.Visit.ControlActivated);
            w.WriteBoolean("requestSent", filter.Sent);
            WriteNullableUtc(w, "sentUtc", filter.SentUtc);
            if (filter.SentUtc is DateTimeOffset sentAt && requested is DateTimeOffset requestedAt)
            {
                w.WriteNumber("millisecondsAfterRequest", Milliseconds(sentAt - requestedAt));
            }

            // How long KsProperty took to return: the call runs on the single audio worker, so a driver that blocks
            // inside it stalls every refresh.
            WriteNullableUtc(w, "returnedUtc", filter.SentUtc is DateTimeOffset at && filter.CallDuration is TimeSpan took ? at + took : null);
            if (filter.CallDuration is TimeSpan duration)
            {
                w.WriteNumber("callMilliseconds", Math.Round(duration.TotalMilliseconds, 3));
            }
            else
            {
                w.WriteNull("callMilliseconds");
            }

            w.WriteString("notSentReason", filter.NotSentReason);

            w.WriteString("hr", filter.Sent ? "0x" + unchecked((uint)filter.Step.Code).ToString("X8", CultureInfo.InvariantCulture) : null);
            w.WriteString("hrName", filter.Step.CodeName);
            w.WriteBoolean("accepted", filter.Accepted);
            w.WriteNumber("bytesReturned", filter.BytesReturned);
            w.WriteString("step", filter.Step.Step);
            w.WriteString("detail", filter.Step.Detail);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteDiagConfirmation(Utf8JsonWriter w, ConfirmationResult? confirmation)
    {
        if (confirmation is null)
        {
            w.WriteNull("confirmation");
            return;
        }

        w.WriteStartObject("confirmation");
        w.WriteBoolean("reached", confirmation.Reached);
        w.WriteBoolean("unreachable", confirmation.Unreachable);
        w.WriteString("source", confirmation.Source.ToString());
        w.WriteNumber("elapsedMilliseconds", Milliseconds(confirmation.Elapsed));
        w.WriteNumber("snapshotChangedEvents", confirmation.Notifications);
        w.WriteNumber("staleSnapshotsIgnored", confirmation.StaleIgnored);
        w.WriteBoolean("renderEndpointObserved", confirmation.RenderEndpointObserved);
        WriteNullableUtc(w, "lastObservedUtc", confirmation.LastObserved?.TakenUtc);
        if (confirmation.ThreadId is int thread)
        {
            w.WriteNumber("observedOnThreadId", thread);
        }
        else
        {
            w.WriteNull("observedOnThreadId");
        }

        w.WriteString("observedOnApartment", confirmation.Apartment?.ToString());
        w.WriteEndObject();
    }

    private static void WriteNullableUtc(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is DateTimeOffset at)
        {
            w.WriteString(name, Utc(at));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static long Milliseconds(TimeSpan span) => (long)Math.Round(span.TotalMilliseconds);
}

// One endpoint notification as a diag run recorded it: when it arrived and on which thread and apartment.
internal sealed record RecordedNotification(
    long Sequence,
    DateTimeOffset Utc,
    EndpointNotificationKind Kind,
    string? DeviceId,
    uint NewState,
    int ThreadId,
    ApartmentState Apartment);

// diag only. A second IMMNotificationClient, registered straight on the audio worker's enumerator (the worker's
// own client slot holds the monitor's client), that records each callback's arguments with the time, thread id
// and apartment it arrived on. The callback thread is undocumented, which is what this records.
//
// The callback follows the documented rules: it copies the arguments and pushes them onto a lock-free stack,
// with no waits, no COM calls on MMDevice objects, no Register or Unregister and no release. The client is not
// AddRef'd by MMDevAPI, so it is held in a field from before Register until after a successful Unregister.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-registerendpointnotificationcallback
internal sealed class NotificationRecorder
{
    internal const string RegisterStep = "register-diag-notification-client";
    internal const string UnregisterStep = "unregister-diag-notification-client";

    private readonly ConcurrentStack<RecordedNotification> _entries = new();
    private readonly TimeProvider _time;
    private long _sequence;

    // Audio worker thread only.
    private NotificationClient? _client;

    public NotificationRecorder(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public IReadOnlyList<RecordedNotification> Entries => _entries.ToArray().OrderBy(e => e.Sequence).ToList();

    // Audio worker thread only.
    public StepOutcome Register(AudioWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (_client is not null)
        {
            return StepOutcomes.NotAttempted(RegisterStep, "The recorder is already registered.");
        }

        int hr = worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr < 0 ? hr : CoreAudio.E_POINTER);
        }

        var client = new NotificationClient(Record);
        _client = client;
        hr = enumerator.RegisterEndpointNotificationCallback(client);
        if (hr < 0)
        {
            _client = null;
        }

        return StepOutcomes.FromHResult(RegisterStep, hr);
    }

    // Audio worker thread only. After a failed Unregister the client stays referenced, because MMDevAPI may still
    // call it.
    public StepOutcome Unregister(AudioWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        if (_client is null)
        {
            return StepOutcomes.NotAttempted(UnregisterStep, "The recorder is not registered.");
        }

        int hr = worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr < 0 ? hr : CoreAudio.E_POINTER);
        }

        NotificationClient client = _client;
        hr = enumerator.UnregisterEndpointNotificationCallback(client);
        (int failures, Exception? last) = client.TakeSinkFailures();
        if (hr >= 0)
        {
            _client = null;
        }

        GC.KeepAlive(client);
        return StepOutcomes.FromHResult(UnregisterStep, hr,
            detail: failures == 0 ? null : failures.ToString(CultureInfo.InvariantCulture) + " callbacks could not be recorded: " + last?.Message);
    }

    // MMDevAPI's callback thread.
    private void Record(EndpointNotification notification) =>
        _entries.Push(new RecordedNotification(
            Interlocked.Increment(ref _sequence),
            _time.GetUtcNow(),
            notification.Kind,
            notification.DeviceId,
            notification.NewState,
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.GetApartmentState()));
}
