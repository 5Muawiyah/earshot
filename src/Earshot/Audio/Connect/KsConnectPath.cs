using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio.Connect;

internal enum ConnectAction
{
    Connect,     // KSPROPERTY_ONESHOT_RECONNECT
    Disconnect,  // KSPROPERTY_ONESHOT_DISCONNECT
}

// Which of the device's filters a request goes to. The controller always uses All; diag ks can pick one.
// A filter is named by the reference string at the end of its adapter id: "src" for the A2DP filter and
// "wave" for the Hands-Free filter on the owner's machine.
internal enum FilterChoice
{
    All,
    Src,
    Wave,
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
//   Name           the reference string at the end of the adapter id ("src", "wave")
//   Visit          the guard and activation, from TopologyWalk.VisitFilters
//   Hr             the KsProperty HRESULT, or null when no request was sent (the guard or activation failed)
//   Step           exactly one "ks-reconnect:<name>" or "ks-disconnect:<name>" step, with the adapter id as
//                  detail: the raw HRESULT when a request was sent, NOT_ATTEMPTED when none was
internal sealed record FilterSend(
    AdapterPath Adapter,
    string Name,
    FilterVisit Visit,
    int? Hr,
    uint BytesReturned,
    DateTimeOffset? SentUtc,
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
internal sealed record KsSendResult(
    IReadOnlyList<AdapterPath> Adapters,
    IReadOnlyList<FilterSend> Filters,
    IReadOnlyList<StepOutcome> Steps)
{
    public bool AnyAccepted => Filters.Any(f => f.Accepted);

    public static KsSendResult Failed(StepOutcome step) =>
        new(Array.Empty<AdapterPath>(), Array.Empty<FilterSend>(), new[] { step });
}

// The single call that sends a Bluetooth audio one-shot request to a filter. Behind an interface so the path
// can be exercised against fakes; no test and no build step sends a real request.
internal interface IKsPropertySender
{
    int Send(IKsControl control, ConnectAction action, out uint bytesReturned);
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
    public int Send(IKsControl control, ConnectAction action, out uint bytesReturned)
    {
        ArgumentNullException.ThrowIfNull(control);
        var property = new KSPROPERTY(KsControl.KSPROPSETID_BtAudio, PropertyId(action), KsControl.KSPROPERTY_TYPE_GET);
        return control.KsProperty(ref property, (uint)InteropLayout.KsIdentifierBytes, 0, 0, out bytesReturned);
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

    // Walks the endpoints to their filters, applies the guard and sends the request to each chosen filter
    // that passes it, in order.
    KsSendResult Send(Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice);
}

// The connect path over Core Audio (design sections F, steps 1 to 5). Worker thread only. Discovery and the
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
internal sealed class KsConnectPath : IKsConnectPath
{
    internal const string ReconnectStep = "ks-reconnect";
    internal const string DisconnectStep = "ks-disconnect";

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

    public KsSendResult Send(Guid container, IReadOnlyList<AudioEndpoint> endpoints, ConnectAction action, FilterChoice choice)
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
        List<AdapterPath> chosen = discovery.Adapters.Where(a => IsChosen(choice, FilterName(a.AdapterId))).ToList();

        var calls = new Dictionary<string, (int Hr, uint Bytes, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<FilterVisit> visits = TopologyWalk.VisitFilters(enumerator, chosen, container, (adapter, control) =>
        {
            // Lent for this call only. The request is sent once; nothing here retries or waits.
            DateTimeOffset at = _time.GetUtcNow();
            int ksHr = _sender.Send(control, action, out uint bytes);
            calls[adapter.AdapterId] = (ksHr, bytes, at);
            return Array.Empty<StepOutcome>();
        }, _release);

        var filters = new List<FilterSend>(visits.Count);
        foreach (FilterVisit visit in visits)
        {
            string id = visit.Adapter.AdapterId;
            string name = FilterName(id);
            steps.AddRange(visit.Steps);

            StepOutcome step;
            FilterSend filter;
            if (calls.TryGetValue(id, out (int Hr, uint Bytes, DateTimeOffset At) call))
            {
                step = StepOutcomes.FromHResult(stepPrefix + ":" + name, call.Hr, detail: id, ok: call.Hr == 0);
                filter = new FilterSend(visit.Adapter, name, visit, call.Hr, call.Bytes, call.At, step);
            }
            else
            {
                step = StepOutcomes.NotAttempted(stepPrefix + ":" + name,
                    "No request was sent to " + id + " because " + (visit.GuardPassed ? "IKsControl could not be activated." : "the guard did not pass."));
                filter = new FilterSend(visit.Adapter, name, visit, null, 0, null, step);
            }

            steps.Add(step);
            filters.Add(filter);
        }

        return new KsSendResult(discovery.Adapters, filters, steps);
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

    // The KS filter reference string, the part of the adapter id after its last backslash: "src" and "wave"
    // on the owner's machine. Adapter ids are otherwise opaque. An id without one is used whole.
    internal static string FilterName(string adapterId)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        int slash = adapterId.LastIndexOf('\\');
        return slash >= 0 && slash < adapterId.Length - 1 ? adapterId[(slash + 1)..] : adapterId;
    }

    internal static bool IsChosen(FilterChoice choice, string filterName) => choice switch
    {
        FilterChoice.All => true,
        FilterChoice.Src => string.Equals(filterName, "src", StringComparison.OrdinalIgnoreCase),
        FilterChoice.Wave => string.Equals(filterName, "wave", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
