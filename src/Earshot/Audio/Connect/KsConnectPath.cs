using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio.Connect;

internal enum ConnectAction
{
    Connect,     // KSPROPERTY_ONESHOT_RECONNECT
    Disconnect,  // KSPROPERTY_ONESHOT_DISCONNECT
}

// What a filter is for, taken from the flow of the device's endpoints that lead to it, never from the reference
// string a driver chose for it. On Windows 11 a Bluetooth headset's output endpoint plays over A2DP unless an app
// asks for Hands-Free, and on the owner's machine the render endpoint leads to the A2DP filter (\src) and the
// capture endpoint to the Hands-Free filter (\wave).
// https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio
//   A2dp       reached from a render endpoint
//   HandsFree  reached only from capture endpoints
internal enum FilterRole
{
    A2dp,
    HandsFree,
}

// Which of the device's filters a request goes to. The controller always uses All; diag ks can pick one by role.
//   Src   the A2DP filter (FilterRole.A2dp)
//   Wave  the Hands-Free filter (FilterRole.HandsFree)
internal enum FilterChoice
{
    All,
    Src,
    Wave,
}

// The data buffer sent with a one-shot request.
//   None             PropertyData null and DataLength 0: the documented request (property value type NULL). The
//                    controller only ever sends this.
//   ZeroedFourBytes  a 4-byte zeroed buffer. diag ks only, for the owner's live test of whether a driver answers
//                    the documented request with a buffer error.
internal enum KsPayload
{
    None,
    ZeroedFourBytes,
}

// The worker's enumerator, or the HRESULT of creating it. AudioWorker.TryGetEnumerator outside tests.
internal delegate int EnumeratorSource(out IMMDeviceEnumerator? enumerator);

// The target container's endpoints from one enumeration of every endpoint in every state.
//   Ok              the enumeration itself worked (a failed per-endpoint read does not clear it)
//   Endpoints       the container's endpoints, render first, then by id
//   FailedSteps     every failed step of the enumeration
//   ContainerSteps  the failed steps that can concern this container: those naming one of its endpoints and
//                   those naming no endpoint at all (an item or id read that failed could have been one)
internal sealed record EndpointRead(
    bool Ok,
    IReadOnlyList<AudioEndpoint> Endpoints,
    IReadOnlyList<StepOutcome> FailedSteps,
    IReadOnlyList<StepOutcome> ContainerSteps)
{
    public static EndpointRead Failed(IReadOnlyList<StepOutcome> steps) =>
        new(false, Array.Empty<AudioEndpoint>(), steps, steps);

    public static EndpointRead From(EndpointEnumeration enumeration, Guid container)
    {
        ArgumentNullException.ThrowIfNull(enumeration);
        if (!enumeration.Ok)
        {
            return Failed(enumeration.Steps);
        }

        List<AudioEndpoint> mine = NodeMatch.IsValidTargetContainer(container)
            ? enumeration.Readings
                .Select(r => r.Endpoint)
                .Where(e => e.ContainerId == container)
                .OrderBy(e => e.Flow == EndpointFlow.Render ? 0 : 1)
                .ThenBy(e => e.EndpointId, StringComparer.Ordinal)
                .ToList()
            : new List<AudioEndpoint>();

        var mineIds = new HashSet<string>(mine.Select(e => e.EndpointId), StringComparer.Ordinal);
        List<string> others = enumeration.Readings
            .Select(r => r.Endpoint.EndpointId)
            .Where(id => !mineIds.Contains(id))
            .ToList();

        List<StepOutcome> failed = enumeration.Steps.Where(s => !s.Ok).ToList();
        List<StepOutcome> concerning = failed
            .Where(s => !others.Any(id => s.Step.EndsWith(":" + id, StringComparison.Ordinal)))
            .ToList();
        return new EndpointRead(true, mine, failed, concerning);
    }
}

// One filter a request was meant for, and what happened to it.
//   Name           the reference string at the end of the adapter id ("src", "wave"), with "#2", "#3" added when
//                  two chosen filters share one, so every step name is unique
//   Role           from the endpoints that lead to the filter (FilterRole)
//   Visit          the guard and activation, from TopologyWalk.VisitFilters
//   Hr             the KsProperty HRESULT, or null when no request was sent
//   SentUtc        when KsProperty was called, or null
//   CallDuration   how long KsProperty took to return, or null
//   NotSentReason  why no request was sent, or null when one was
//   Step           exactly one "ks-reconnect:<name>" or "ks-disconnect:<name>" step. Its detail starts with the
//                  role and the adapter id ("a2dp: <id>", "hands-free: <id>"), see RoleOfStep. The code is the
//                  raw HRESULT when a request was sent, NOT_ATTEMPTED when none was.
internal sealed record FilterSend(
    AdapterPath Adapter,
    string Name,
    FilterRole Role,
    FilterVisit Visit,
    int? Hr,
    uint BytesReturned,
    DateTimeOffset? SentUtc,
    TimeSpan? CallDuration,
    string? NotSentReason,
    StepOutcome Step)
{
    public bool Sent => Hr is not null;

    // Only S_OK counts as an attempt made. Success means the driver attempted the change, not that it
    // happened.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect
    public bool Accepted => Hr == 0;
}

// Every filter found from the endpoints, the filters a request was meant for (in send order), and every step:
// discovery failures, then per filter its guard or activation failures followed by its ks step.
//   Fault  the exception that stopped the walk, or null. The filter it was thrown for carries a "visit-filter"
//          step with the exception's HResult, filters after it are sent nothing, and requests already sent keep
//          their steps. The caller logs it and surfaces it.
internal sealed record KsSendResult(
    IReadOnlyList<AdapterPath> Adapters,
    IReadOnlyList<FilterSend> Filters,
    IReadOnlyList<StepOutcome> Steps,
    Exception? Fault = null)
{
    public bool AnyAccepted => Filters.Any(f => f.Accepted);

    public static KsSendResult Failed(StepOutcome step) =>
        new(Array.Empty<AdapterPath>(), Array.Empty<FilterSend>(), new[] { step });
}

// The single call that sends a Bluetooth audio one-shot request to a filter. Behind an interface so the path
// can be exercised against fakes; no test and no build step sends a real request.
internal interface IKsPropertySender
{
    int Send(IKsControl control, ConnectAction action, KsPayload payload, out uint bytesReturned);
}

// KSPROPERTY_ONESHOT_RECONNECT and KSPROPERTY_ONESHOT_DISCONNECT. Both are Get requests on the filter:
// descriptor KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = 0 or 1, Flags = KSPROPERTY_TYPE_GET }, 24 bytes,
// property value type NULL, so PropertyData is null and DataLength is 0. Only these two ids can be built here.
// IKsControl is declared in SDK vtable order (KsProperty first), not the alphabetical order of the doc page.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kspropsetid-btaudio
// https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect
// https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-disconnect
// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nf-ksproxy-ikscontrol-ksproperty
internal sealed class KsPropertySender : IKsPropertySender
{
    public int Send(IKsControl control, ConnectAction action, KsPayload payload, out uint bytesReturned)
    {
        ArgumentNullException.ThrowIfNull(control);
        var property = new KSPROPERTY(KsControl.KSPROPSETID_BtAudio, PropertyId(action), KsControl.KSPROPERTY_TYPE_GET);
        switch (payload)
        {
            case KsPayload.None:
                return control.KsProperty(ref property, (uint)InteropLayout.KsIdentifierBytes, 0, 0, out bytesReturned);

            case KsPayload.ZeroedFourBytes:
                nint buffer = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    Marshal.WriteInt32(buffer, 0);
                    return control.KsProperty(ref property, (uint)InteropLayout.KsIdentifierBytes, buffer, sizeof(uint), out bytesReturned);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(payload), payload, "Unknown payload.");
        }
    }

    internal static uint PropertyId(ConnectAction action) => action switch
    {
        ConnectAction.Connect => KsControl.KSPROPERTY_ONESHOT_RECONNECT,
        ConnectAction.Disconnect => KsControl.KSPROPERTY_ONESHOT_DISCONNECT,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Only reconnect and disconnect can be sent."),
    };
}

// What the connection controller needs from Core Audio. Both members run on the audio worker thread only.
internal interface IKsConnectPath
{
    // Enumerates every endpoint in every state and keeps the container's.
    EndpointRead ReadEndpoints(Guid container);

    // Walks the endpoints to their filters, applies the guard and sends the documented request (no buffer) to
    // each chosen filter that passes it, in order. A cancelled token stops the walk before the next filter; a
    // request already sent is never recalled.
    KsSendResult Send(Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice, CancellationToken ct = default);
}

// The connect path over Core Audio: find the filters, guard them, send the property. Worker thread only. Discovery and the
// guard are TopologyWalk's: endpoint -> IDeviceTopology -> connector 0 -> GetDeviceIdConnectedTo, then per
// adapter GetDevice, GetState == ACTIVE, adapter ContainerId == target, and only then Activate(IKsControl).
// That container check keeps any other device's filter, such as the paired phone's Hands-Free filter, out.
// Filters reached from a render endpoint come first, so a connect reaches the A2DP filter (\src) before the
// Hands-Free filter (\wave); a disconnect goes to every distinct filter in the same order.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/using-the-ikscontrol-interface-to-access-audio-properties
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-topologies
//
// Every COM object is released once, in a finally block, by the method that obtained it (TopologyWalk and
// CoreAudioEndpointReader do that with the release passed here). Nothing is kept between calls: each click
// walks and activates afresh. The enumerator belongs to the audio worker and is never released here.
//
// An exception from one filter's walk or request (a marshalling failure, a released wrapper during a device
// transition) is not allowed to lose what was already sent: it becomes that filter's "visit-filter" step with
// the exception's HResult, the filters after it are sent nothing, and the partial result carries the exception
// as Fault for the caller to log and surface.
internal sealed class KsConnectPath : IKsConnectPath
{
    internal const string ReconnectStep = "ks-reconnect";
    internal const string DisconnectStep = "ks-disconnect";
    internal const string VisitFailedStep = "visit-filter";

    private const string A2dpRoleName = "a2dp";
    private const string HandsFreeRoleName = "hands-free";

    private readonly EnumeratorSource _enumerators;
    private readonly IKsPropertySender _sender;
    private readonly Action<object> _release;
    private readonly TimeProvider _time;

    public KsConnectPath(AudioWorker worker)
        : this(EnumeratorsOf(worker), new KsPropertySender(), ComRelease.Rcw, TimeProvider.System)
    {
    }

    internal KsConnectPath(EnumeratorSource enumerators, IKsPropertySender sender, Action<object> release, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(enumerators);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(time);
        _enumerators = enumerators;
        _sender = sender;
        _release = release;
        _time = time;
    }

    // EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL), read by the discovery code.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints
    public EndpointRead ReadEndpoints(Guid container)
    {
        int hr = _enumerators(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return EndpointRead.Failed(new[] { StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr < 0 ? hr : CoreAudio.E_POINTER) });
        }

        return EndpointRead.From(CoreAudioEndpointReader.ReadAll(enumerator, _release), container);
    }

    public KsSendResult Send(Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice, CancellationToken ct = default) =>
        Send(container, endpoints, action, choice, KsPayload.None, ct);

    internal KsSendResult Send(
        Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice, KsPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        string stepPrefix = StepPrefix(action);

        int hr = _enumerators(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return KsSendResult.Failed(StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr < 0 ? hr : CoreAudio.E_POINTER));
        }

        AdapterDiscovery discovery = TopologyWalk.FindAdapters(enumerator, endpoints, container, _release);
        var steps = new List<StepOutcome>(discovery.Steps);
        List<AdapterPath> chosen = discovery.Adapters.Where(a => IsChosen(choice, a)).ToList();
        IReadOnlyList<string> names = UniqueNames(chosen);

        var filters = new List<FilterSend>(chosen.Count);
        Exception? fault = null;
        for (int i = 0; i < chosen.Count; i++)
        {
            AdapterPath adapter = chosen[i];
            FilterRole role = RoleOf(adapter);
            string stepName = stepPrefix + ":" + names[i];
            string label = RoleName(role) + ": " + adapter.AdapterId;

            // A newer click may have cancelled this one while an earlier filter was still in the driver. What
            // was sent stays sent; nothing more is.
            string? stopped = fault is not null ? "the walk stopped at an earlier filter."
                : ct.IsCancellationRequested ? "the request was cancelled before this filter."
                : null;
            if (stopped is not null)
            {
                StepOutcome skipped = StepOutcomes.NotAttempted(stepName, label + ": no request was sent because " + stopped);
                steps.Add(skipped);
                filters.Add(new FilterSend(adapter, names[i], role, NotVisited(adapter), null, 0, null, null, stopped, skipped));
                continue;
            }

            SentRequest? sent = null;
            FilterVisit visit;
            try
            {
                visit = TopologyWalk.VisitFilters(enumerator, new[] { adapter }, container, (_, control) =>
                {
                    // Lent for this call only. The request is sent once; nothing here retries or waits.
                    DateTimeOffset sentUtc = _time.GetUtcNow();
                    long started = _time.GetTimestamp();
                    int ksHr = _sender.Send(control, action, payload, out uint bytes);
                    sent = new SentRequest(ksHr, bytes, sentUtc, _time.GetElapsedTime(started));
                    return Array.Empty<StepOutcome>();
                }, _release).Single();
            }
            catch (Exception ex)
            {
                // Recorded, never swallowed: the exception stays on the result as Fault, which the caller logs
                // and surfaces. The COM objects were released by the walk's finally blocks as it unwound.
                fault = ex;
                StepOutcome failed = StepOutcomes.FromHResult(VisitFailedStep + ":" + adapter.AdapterId, ex.HResult,
                    detail: ex.GetType().Name + ": " + ex.Message, ok: false);
                visit = new FilterVisit(adapter, null, null, false, false, new[] { failed });
            }

            steps.AddRange(visit.Steps);
            FilterSend filter;
            if (sent is not null)
            {
                StepOutcome step = StepOutcomes.FromHResult(stepName, sent.Hr, detail: label, ok: sent.Hr == 0);
                filter = new FilterSend(adapter, names[i], role, visit, sent.Hr, sent.BytesReturned, sent.SentUtc, sent.Duration, null, step);
            }
            else
            {
                string reason = fault is not null ? "the walk of this filter stopped with an exception." : NotSentReason(visit);
                StepOutcome step = StepOutcomes.NotAttempted(stepName, label + ": no request was sent because " + reason);
                filter = new FilterSend(adapter, names[i], role, visit, null, 0, null, null, reason, step);
            }

            steps.Add(filter.Step);
            filters.Add(filter);
        }

        return new KsSendResult(discovery.Adapters, filters, steps, fault);
    }

    private static EnumeratorSource EnumeratorsOf(AudioWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return worker.TryGetEnumerator;
    }

    internal static string StepPrefix(ConnectAction action) => action switch
    {
        ConnectAction.Connect => ReconnectStep,
        ConnectAction.Disconnect => DisconnectStep,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    internal static FilterRole RoleOf(AdapterPath adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.FromRender ? FilterRole.A2dp : FilterRole.HandsFree;
    }

    internal static string RoleName(FilterRole role) => role switch
    {
        FilterRole.A2dp => A2dpRoleName,
        FilterRole.HandsFree => HandsFreeRoleName,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    // The role a ks step's detail starts with, or null for any other step. Lets a caller of IConnectionController,
    // which sees only the steps, tell the A2DP filter's outcome from the Hands-Free filter's without relying on
    // the driver-chosen reference string in the step name.
    internal static FilterRole? RoleOfStep(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!(step.Step.StartsWith(ReconnectStep + ":", StringComparison.Ordinal) ||
              step.Step.StartsWith(DisconnectStep + ":", StringComparison.Ordinal)) || step.Detail is null)
        {
            return null;
        }

        return step.Detail.StartsWith(A2dpRoleName + ": ", StringComparison.Ordinal) ? FilterRole.A2dp
            : step.Detail.StartsWith(HandsFreeRoleName + ": ", StringComparison.Ordinal) ? FilterRole.HandsFree
            : null;
    }

    // The KS filter reference string, the part of the adapter id after its last backslash: "src" and "wave"
    // on the owner's machine. Adapter ids are otherwise opaque. An id without one is used whole.
    internal static string FilterName(string adapterId)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        int slash = adapterId.LastIndexOf('\\');
        return slash >= 0 && slash < adapterId.Length - 1 ? adapterId[(slash + 1)..] : adapterId;
    }

    // Filter names in order, with "#2", "#3" added to a name already used, so no two ks steps share a name.
    internal static IReadOnlyList<string> UniqueNames(IEnumerable<AdapterPath> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (AdapterPath adapter in adapters)
        {
            string name = FilterName(adapter.AdapterId);
            int count = seen.GetValueOrDefault(name) + 1;
            seen[name] = count;
            names.Add(count == 1 ? name : name + "#" + count.ToString(CultureInfo.InvariantCulture));
        }

        return names;
    }

    internal static bool IsChosen(FilterChoice choice, AdapterPath adapter) => choice switch
    {
        FilterChoice.All => true,
        FilterChoice.Src => RoleOf(adapter) == FilterRole.A2dp,
        FilterChoice.Wave => RoleOf(adapter) == FilterRole.HandsFree,
        _ => false,
    };

    // Why the walk sent nothing to a filter it visited. TopologyWalk leaves State null when the filter could not
    // be opened (GetDevice failed, 0xE0000225 when it has gone) or its state could not be read, and records that
    // step; it sets State when the guard itself refused the filter.
    internal static string NotSentReason(FilterVisit visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        string id = visit.Adapter.AdapterId;
        if (visit.ControlActivated)
        {
            return "the request was not made.";
        }

        if (visit.GuardPassed)
        {
            return "IKsControl could not be activated.";
        }

        if (visit.State is null && visit.Steps.Any(s => s.Step == TopologyWalk.GetAdapterStep + ":" + id))
        {
            return "the filter could not be opened.";
        }

        if (visit.State is null && visit.Steps.Any(s => s.Step == TopologyWalk.AdapterStateStep + ":" + id))
        {
            return "the filter state could not be read.";
        }

        return "the guard did not pass.";
    }

    private static FilterVisit NotVisited(AdapterPath adapter) =>
        new(adapter, null, null, false, false, Array.Empty<StepOutcome>());

    // Plain data from inside the walk's callback.
    private sealed record SentRequest(int Hr, uint BytesReturned, DateTimeOffset SentUtc, TimeSpan Duration);
}
