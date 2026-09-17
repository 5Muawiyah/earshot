using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio;

// One adapter (kernel streaming filter) reached from the target device's endpoints: its id, and the
// endpoints (with their flow) that lead to it. A filter reached from a render endpoint is the A2DP one.
internal sealed record AdapterPath(string AdapterId, IReadOnlyList<AudioEndpoint> FromEndpoints)
{
    public bool FromRender => FromEndpoints.Any(e => e.Flow == EndpointFlow.Render);
}

internal sealed record AdapterDiscovery(IReadOnlyList<AdapterPath> Adapters, IReadOnlyList<StepOutcome> Steps);

// How one adapter fared under the guard.
//   State              adapter GetState, or null when GetDevice or GetState failed
//   ContainerId        the adapter's PKEY_Device_ContainerId, or null when it was not read
//   GuardPassed        state is ACTIVE and the container is the target container
//   ControlActivated   IKsControl was activated (only after the guard passed)
internal sealed record FilterVisit(
    AdapterPath Adapter,
    EndpointState? State,
    Guid? ContainerId,
    bool GuardPassed,
    bool ControlActivated,
    IReadOnlyList<StepOutcome> Steps);

// The documented walk from a device's endpoints to its kernel streaming filters, up to the IKsControl each
// one offers. Worker thread only; every RCW is created and released inside these calls, and no interface leaves
// them except the IKsControl lent to the caller's callback for the duration of that call.
//
//   1-2  for each endpoint of the target container (any flow, any state): IMMDevice.Activate(IDeviceTopology)
//        -> GetConnector(0) -> IConnector.GetDeviceIdConnectedTo, collecting distinct adapter ids.
//        GetDeviceIdConnectedTo is used rather than GetConnectedTo because it still answers when the adapter
//        side cannot be loaded. E_NOTFOUND means the connector is not connected.
//   3    for each adapter id: IMMDeviceEnumerator.GetDevice (0xE0000225 means the filter is gone), then the
//        guard: GetState must be DEVICE_STATE_ACTIVE and the adapter's own container id must equal the target
//        container. Only then IMMDevice.Activate(IKsControl) (0x80070002 or 0x80070003 means absent). The
//        container check keeps a filter of another device, such as the paired phone's Hands-Free filter, out
//        even if the endpoint list were wrong.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-topologies
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/using-the-ikscontrol-interface-to-access-audio-properties
// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nf-devicetopology-iconnector-getdeviceidconnectedto
//
// Every COM object obtained here is released through the release delegate (ComRelease.Rcw outside tests),
// once, in a finally block in the method that obtained it.
//
// Nothing here changes a device. IConnector.ConnectTo and Disconnect (software topology edits) are never
// called. The only kernel streaming request in this file is ReadPinCount, a KSPROPSETID_Pin Get.
internal static class TopologyWalk
{
    internal const string GetEndpointStep = "get-endpoint";
    internal const string ActivateTopologyStep = "activate-topology";
    internal const string GetConnectorStep = "get-connector";
    internal const string AdapterIdStep = "adapter-id";
    internal const string GetAdapterStep = "get-adapter";
    internal const string AdapterStateStep = "adapter-state";
    internal const string AdapterStoreStep = "adapter-property-store";
    internal const string AdapterContainerStep = "adapter-container";
    internal const string GuardStep = "guard";
    internal const string ActivateControlStep = "activate-kscontrol";
    internal const string PinCountStep = "pin-ctypes";

    // Steps 1 and 2. Endpoints outside the target container are skipped, so the caller cannot widen the walk.
    public static AdapterDiscovery FindAdapters(IMMDeviceEnumerator enumerator, IEnumerable<AudioEndpoint> endpoints, Guid targetContainer) =>
        FindAdapters(enumerator, endpoints, targetContainer, ComRelease.Rcw);

    internal static AdapterDiscovery FindAdapters(
        IMMDeviceEnumerator enumerator, IEnumerable<AudioEndpoint> endpoints, Guid targetContainer, Action<object> release)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(release);

        var steps = new List<StepOutcome>();
        if (!NodeMatch.IsValidTargetContainer(targetContainer))
        {
            steps.Add(StepOutcomes.NotAttempted(GuardStep, "The container " + Format(targetContainer) + " is never a target."));
            return new AdapterDiscovery(Array.Empty<AdapterPath>(), steps);
        }

        var found = new Dictionary<string, List<AudioEndpoint>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (AudioEndpoint endpoint in endpoints)
        {
            if (endpoint.ContainerId != targetContainer)
            {
                continue;
            }

            string? adapterId = AdapterIdOf(enumerator, endpoint.EndpointId, steps, release);
            if (adapterId is null)
            {
                continue;
            }

            if (!found.TryGetValue(adapterId, out List<AudioEndpoint>? from))
            {
                from = new List<AudioEndpoint>();
                found.Add(adapterId, from);
                order.Add(adapterId);
            }

            from.Add(endpoint);
        }

        // Filters reached from a render endpoint first (A2DP before Hands-Free), then by id.
        List<AdapterPath> adapters = order
            .Select(id => new AdapterPath(id, found[id]))
            .OrderBy(a => a.FromRender ? 0 : 1)
            .ThenBy(a => a.AdapterId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AdapterDiscovery(adapters, steps);
    }

    // Step 3. For each adapter that passes the guard, lends its IKsControl to use (on this thread, for that
    // call only; do not keep it) and collects the steps use returns. The control is released afterwards.
    public static IReadOnlyList<FilterVisit> VisitFilters(
        IMMDeviceEnumerator enumerator,
        IEnumerable<AdapterPath> adapters,
        Guid targetContainer,
        Func<AdapterPath, IKsControl, IReadOnlyList<StepOutcome>> use) =>
        VisitFilters(enumerator, adapters, targetContainer, use, ComRelease.Rcw);

    internal static IReadOnlyList<FilterVisit> VisitFilters(
        IMMDeviceEnumerator enumerator,
        IEnumerable<AdapterPath> adapters,
        Guid targetContainer,
        Func<AdapterPath, IKsControl, IReadOnlyList<StepOutcome>> use,
        Action<object> release)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(release);

        var visits = new List<FilterVisit>();
        foreach (AdapterPath adapter in adapters)
        {
            visits.Add(VisitFilter(enumerator, adapter, targetContainer, use, release));
        }

        return visits;
    }

    // KSPROPERTY_PIN_CTYPES: a Get on KSPROPSETID_Pin with a KSPROPERTY descriptor, returning the number of
    // pin factories as a ULONG. Read-only. The request is built here, so no other property set can be sent
    // through this method.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-pin-ctypes
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nf-ksproxy-ikscontrol-ksproperty
    public static StepOutcome ReadPinCount(IKsControl control, string adapterId, out uint? pinCount)
    {
        ArgumentNullException.ThrowIfNull(control);
        pinCount = null;
        var property = new KSPROPERTY(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_CTYPES, KsControl.KSPROPERTY_TYPE_GET);
        nint buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, 0);
            int hr = control.KsProperty(ref property, (uint)InteropLayout.KsIdentifierBytes, buffer, sizeof(uint), out uint bytesReturned);
            if (hr < 0)
            {
                return StepOutcomes.FromHResult(PinCountStep + ":" + adapterId, hr);
            }

            if (bytesReturned < sizeof(uint))
            {
                return StepOutcomes.FromHResult(PinCountStep + ":" + adapterId, hr,
                    detail: "returned " + bytesReturned.ToString(CultureInfo.InvariantCulture) + " bytes", ok: false);
            }

            pinCount = unchecked((uint)Marshal.ReadInt32(buffer));
            return StepOutcomes.FromHResult(PinCountStep + ":" + adapterId, hr);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string Format(Guid container) =>
        container.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string? AdapterIdOf(IMMDeviceEnumerator enumerator, string endpointId, List<StepOutcome> steps, Action<object> release)
    {
        int hr = enumerator.GetDevice(endpointId, out IMMDevice? device);
        if (hr < 0 || device is null)
        {
            steps.Add(StepOutcomes.FromHResult(GetEndpointStep + ":" + endpointId, hr < 0 ? hr : CoreAudio.E_POINTER));
            return null;
        }

        IDeviceTopology? topology = null;
        IConnector? connector = null;
        try
        {
            // pActivationParams must be null for IDeviceTopology.
            // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-activate
            hr = CoreAudio.Activate(device, out topology);
            if (hr < 0 || topology is null)
            {
                steps.Add(StepOutcomes.FromHResult(ActivateTopologyStep + ":" + endpointId, hr < 0 ? hr : CoreAudio.E_POINTER));
                return null;
            }

            // An endpoint's topology has exactly one connector, number 0.
            hr = topology.GetConnector(0, out connector);
            if (hr < 0 || connector is null)
            {
                steps.Add(StepOutcomes.FromHResult(GetConnectorStep + ":" + endpointId, hr < 0 ? hr : CoreAudio.E_POINTER));
                return null;
            }

            hr = connector.GetDeviceIdConnectedTo(out nint idPointer);
            string? adapterId = CoreAudio.TakeCoTaskString(idPointer);
            if (hr < 0 || string.IsNullOrEmpty(adapterId))
            {
                steps.Add(StepOutcomes.FromHResult(AdapterIdStep + ":" + endpointId, hr < 0 ? hr : CoreAudio.E_POINTER));
                return null;
            }

            return adapterId;
        }
        finally
        {
            if (connector is not null)
            {
                release(connector);
            }

            if (topology is not null)
            {
                release(topology);
            }

            release(device);
        }
    }

    private static FilterVisit VisitFilter(
        IMMDeviceEnumerator enumerator,
        AdapterPath adapter,
        Guid targetContainer,
        Func<AdapterPath, IKsControl, IReadOnlyList<StepOutcome>> use,
        Action<object> release)
    {
        string id = adapter.AdapterId;
        var steps = new List<StepOutcome>();

        if (!NodeMatch.IsValidTargetContainer(targetContainer))
        {
            steps.Add(StepOutcomes.NotAttempted(GuardStep + ":" + id, "The container " + Format(targetContainer) + " is never a target."));
            return new FilterVisit(adapter, null, null, false, false, steps);
        }

        int hr = enumerator.GetDevice(id, out IMMDevice? device);
        if (hr < 0 || device is null)
        {
            steps.Add(StepOutcomes.FromHResult(GetAdapterStep + ":" + id, hr < 0 ? hr : CoreAudio.E_POINTER));
            return new FilterVisit(adapter, null, null, false, false, steps);
        }

        try
        {
            hr = device.GetState(out uint rawState);
            if (hr < 0)
            {
                steps.Add(StepOutcomes.FromHResult(AdapterStateStep + ":" + id, hr));
                return new FilterVisit(adapter, null, null, false, false, steps);
            }

            var state = (EndpointState)rawState;
            if (rawState != CoreAudio.DEVICE_STATE_ACTIVE)
            {
                steps.Add(StepOutcomes.NotAttempted(GuardStep + ":" + id,
                    "The adapter state is 0x" + rawState.ToString("X", CultureInfo.InvariantCulture) + ", not ACTIVE."));
                return new FilterVisit(adapter, state, null, false, false, steps);
            }

            Guid? container = ReadAdapterContainer(device, id, steps, release);
            if (container != targetContainer)
            {
                steps.Add(StepOutcomes.NotAttempted(GuardStep + ":" + id,
                    "The adapter container " + (container is Guid c ? Format(c) : "could not be read") +
                    (container is null ? "" : " is not the target " + Format(targetContainer)) + "."));
                return new FilterVisit(adapter, state, container, false, false, steps);
            }

            hr = CoreAudio.Activate(device, out IKsControl? control);
            if (hr < 0 || control is null)
            {
                steps.Add(StepOutcomes.FromHResult(ActivateControlStep + ":" + id, hr < 0 ? hr : CoreAudio.E_POINTER));
                return new FilterVisit(adapter, state, container, true, false, steps);
            }

            try
            {
                steps.AddRange(use(adapter, control));
            }
            finally
            {
                release(control);
            }

            return new FilterVisit(adapter, state, container, true, true, steps);
        }
        finally
        {
            release(device);
        }
    }

    private static Guid? ReadAdapterContainer(IMMDevice device, string id, List<StepOutcome> steps, Action<object> release)
    {
        int hr = device.OpenPropertyStore(CoreAudio.STGM_READ, out IPropertyStore? store);
        if (hr < 0 || store is null)
        {
            steps.Add(StepOutcomes.FromHResult(AdapterStoreStep + ":" + id, hr < 0 ? hr : CoreAudio.E_POINTER));
            return null;
        }

        try
        {
            PropertyRead<Guid> read = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);
            if (read.ClearHr < 0)
            {
                steps.Add(StepOutcomes.FromHResult(CoreAudioEndpointReader.ClearStep + ":" + AdapterContainerStep + ":" + id, read.ClearHr));
            }

            if (read.Hr < 0 || !read.HasValue)
            {
                steps.Add(StepOutcomes.FromHResult(AdapterContainerStep + ":" + id, read.Hr,
                    detail: read.Hr < 0 ? null : "no container id (vt " + read.VarType.ToString(CultureInfo.InvariantCulture) + ")", ok: false));
                return null;
            }

            return read.Value;
        }
        finally
        {
            release(store);
        }
    }
}
