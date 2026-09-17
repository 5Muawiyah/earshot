using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Audio;

// The result of one endpoint enumeration. Ok is false only when the enumeration itself failed (no
// enumerator, EnumAudioEndpoints or GetCount failed); Steps then holds the failing call. When Ok is true,
// Steps holds the per-endpoint reads that failed, which are expected on NOTPRESENT endpoints and are
// never fatal.
internal sealed record EndpointEnumeration(bool Ok, IReadOnlyList<EndpointReading> Readings, IReadOnlyList<StepOutcome> Steps)
{
    public static EndpointEnumeration Failed(StepOutcome step) => new(false, Array.Empty<EndpointReading>(), new[] { step });
}

// Releases an RCW that discovery created. Production code passes Rcw, which is Marshal.ReleaseComObject.
// The endpoint reads and the topology walk take the release as a parameter so their guards and failure
// paths can run against managed test doubles, for which Marshal.ReleaseComObject throws ArgumentException.
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.releasecomobject
internal static class ComRelease
{
    public static readonly Action<object> Rcw = static rcw => Marshal.ReleaseComObject(rcw);
}

// What the device monitor needs from Core Audio. Every member is called on the audio worker thread.
internal interface IEndpointSource
{
    // Registers for endpoint notifications; the sink is called on MMDevAPI's callback thread, and sinkFailed
    // on the same thread right after the sink threw.
    StepOutcome Subscribe(Action<EndpointNotification> sink, Action sinkFailed);

    // Unregisters what Subscribe registered.
    StepOutcome Unsubscribe();

    EndpointEnumeration Enumerate();

    // Notifications the sink failed to take since the last call.
    (int Count, Exception? Last) TakeCallbackFailures();
}

// The real source: the audio worker's enumerator and notification client.
internal sealed class CoreAudioEndpointSource : IEndpointSource
{
    private readonly AudioWorker _worker;

    public CoreAudioEndpointSource(AudioWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
    }

    public StepOutcome Subscribe(Action<EndpointNotification> sink, Action sinkFailed) =>
        _worker.RegisterNotificationClient(new NotificationClient(sink, sinkFailed));

    public StepOutcome Unsubscribe() => _worker.UnregisterNotificationClient();

    public (int Count, Exception? Last) TakeCallbackFailures() =>
        _worker.RegisteredClient?.TakeSinkFailures() ?? (0, null);

    public EndpointEnumeration Enumerate()
    {
        int hr = _worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            return EndpointEnumeration.Failed(StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr));
        }

        return CoreAudioEndpointReader.ReadAll(enumerator);
    }
}

// Reads every audio endpoint, in every state, with the data the device model needs. Worker thread only;
// every RCW made here is released here. Reads only: nothing is set, activated or sent.
internal static class CoreAudioEndpointReader
{
    internal const string EnumerateStep = "enumerate-endpoints";
    internal const string CountStep = "endpoint-count";
    internal const string ItemStep = "endpoint-item";
    internal const string IdStep = "endpoint-id";
    internal const string StateStep = "endpoint-state";
    internal const string FlowStep = "endpoint-flow";
    internal const string StoreStep = "open-property-store";
    internal const string FriendlyNameStep = "friendly-name";
    internal const string InterfaceNameStep = "interface-name";
    internal const string ContainerStep = "container-id";
    internal const string ClearStep = "clear-propvariant";

    // EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL): render and capture endpoints in every state, because a
    // disconnected device is UNPLUGGED and a blocked one NOTPRESENT.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints
    public static EndpointEnumeration ReadAll(IMMDeviceEnumerator enumerator) => ReadAll(enumerator, ComRelease.Rcw);

    // release is called once for every COM object this read obtains.
    internal static EndpointEnumeration ReadAll(IMMDeviceEnumerator enumerator, Action<object> release)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(release);
        int hr = enumerator.EnumAudioEndpoints(CoreAudio.eAll, CoreAudio.DEVICE_STATEMASK_ALL, out IMMDeviceCollection? collection);
        if (hr < 0 || collection is null)
        {
            return EndpointEnumeration.Failed(StepOutcomes.FromHResult(EnumerateStep, hr < 0 ? hr : CoreAudio.E_POINTER));
        }

        var readings = new List<EndpointReading>();
        var steps = new List<StepOutcome>();
        try
        {
            hr = collection.GetCount(out uint count);
            if (hr < 0)
            {
                return EndpointEnumeration.Failed(StepOutcomes.FromHResult(CountStep, hr));
            }

            for (uint index = 0; index < count; index++)
            {
                hr = collection.Item(index, out IMMDevice? device);
                if (hr < 0 || device is null)
                {
                    steps.Add(StepOutcomes.FromHResult(ItemStep + ":" + index, hr < 0 ? hr : CoreAudio.E_POINTER));
                    continue;
                }

                try
                {
                    EndpointReading? reading = ReadEndpoint(device, steps, release);
                    if (reading is not null)
                    {
                        readings.Add(reading);
                    }
                }
                finally
                {
                    release(device);
                }
            }
        }
        finally
        {
            release(collection);
        }

        return new EndpointEnumeration(true, readings, steps);
    }

    // One endpoint. Returns null (with a step) only when the endpoint cannot be identified: no id or no
    // data flow. A failed state or property read leaves that value empty and adds a step; on NOTPRESENT
    // endpoints GetValue(PKEY_Device_FriendlyName) can fail with 0xE000020B, which is expected.
    // The device stays the caller's to release; the property store opened here is released with release.
    internal static EndpointReading? ReadEndpoint(IMMDevice device, List<StepOutcome> steps, Action<object> release)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(release);

        // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid
        int hr = device.GetId(out nint idPointer);
        string? id = CoreAudio.TakeCoTaskString(idPointer);
        if (hr < 0 || string.IsNullOrEmpty(id))
        {
            steps.Add(StepOutcomes.FromHResult(IdStep, hr < 0 ? hr : CoreAudio.E_POINTER));
            return null;
        }

        // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getstate
        EndpointState state = 0;
        hr = device.GetState(out uint rawState);
        if (hr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(StateStep + ":" + id, hr));
        }
        else
        {
            state = (EndpointState)rawState;
        }

        // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immendpoint-getdataflow
        if (device is not IMMEndpoint endpoint)
        {
            steps.Add(StepOutcomes.FromHResult(FlowStep + ":" + id, CoreAudio.E_NOINTERFACE));
            return null;
        }

        hr = endpoint.GetDataFlow(out int dataFlow);
        if (hr < 0 || dataFlow is not (CoreAudio.eRender or CoreAudio.eCapture))
        {
            steps.Add(StepOutcomes.FromHResult(FlowStep + ":" + id, hr, detail: hr < 0 ? null : "unexpected data flow " + dataFlow, ok: false));
            return null;
        }

        EndpointFlow flow = dataFlow == CoreAudio.eRender ? EndpointFlow.Render : EndpointFlow.Capture;
        string? friendlyName = null;
        string? interfaceName = null;
        Guid container = Guid.Empty;

        // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-openpropertystore
        hr = device.OpenPropertyStore(CoreAudio.STGM_READ, out IPropertyStore? store);
        if (hr < 0 || store is null)
        {
            steps.Add(StepOutcomes.FromHResult(StoreStep + ":" + id, hr < 0 ? hr : CoreAudio.E_POINTER));
        }
        else
        {
            try
            {
                friendlyName = ReadName(store, CoreAudio.PKEY_Device_FriendlyName, FriendlyNameStep + ":" + id, steps);
                interfaceName = ReadName(store, CoreAudio.PKEY_DeviceInterface_FriendlyName, InterfaceNameStep + ":" + id, steps);
                container = ReadContainer(store, ContainerStep + ":" + id, steps);
            }
            finally
            {
                release(store);
            }
        }

        return new EndpointReading(new AudioEndpoint(id, flow, state, friendlyName, container), interfaceName);
    }

    private static string? ReadName(IPropertyStore store, PROPERTYKEY key, string step, List<StepOutcome> steps)
    {
        PropertyRead<string> read = PropVariantInterop.ReadString(store, key);
        RecordClear(read.ClearHr, step, steps);
        if (read.Hr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(step, read.Hr));
            return null;
        }

        if (!read.HasValue && read.VarType != PropVariantInterop.VT_EMPTY)
        {
            steps.Add(StepOutcomes.FromHResult(step, read.Hr, detail: "unexpected type vt " + read.VarType, ok: false));
        }

        return read.Value;
    }

    // PKEY_Device_ContainerId arrives as VT_CLSID through an endpoint property store.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid
    private static Guid ReadContainer(IPropertyStore store, string step, List<StepOutcome> steps)
    {
        PropertyRead<Guid> read = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);
        RecordClear(read.ClearHr, step, steps);
        if (read.Hr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(step, read.Hr));
            return Guid.Empty;
        }

        if (!read.HasValue)
        {
            steps.Add(StepOutcomes.FromHResult(step, read.Hr, detail: "no container id (vt " + read.VarType + ")", ok: false));
            return Guid.Empty;
        }

        return read.Value;
    }

    private static void RecordClear(int clearHr, string step, List<StepOutcome> steps)
    {
        if (clearHr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(ClearStep + ":" + step, clearHr));
        }
    }
}
