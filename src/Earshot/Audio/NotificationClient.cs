using Earshot.Interop;

namespace Earshot.Audio;

internal enum EndpointNotificationKind
{
    StateChanged,
    Added,
    Removed,
    DefaultChanged,
    PropertyChanged,
}

// A copy of one IMMNotificationClient callback's arguments. The device id string MMDevAPI passes is only
// valid during the call; the marshaller has already copied it into a managed string.
internal sealed record EndpointNotification(
    EndpointNotificationKind Kind,
    string? DeviceId,
    uint NewState,
    int Flow,
    int Role,
    Guid PropertyFormat,
    uint PropertyId)
{
    // Whether the device model can have changed. State changes, arrivals and removals always can. A
    // property change can only when it is one of the names or the container id the model is built from.
    // A default device change never changes grouping, names or states.
    public bool RequiresRefresh => Kind switch
    {
        EndpointNotificationKind.StateChanged => true,
        EndpointNotificationKind.Added => true,
        EndpointNotificationKind.Removed => true,
        EndpointNotificationKind.PropertyChanged => IsModelProperty(PropertyFormat, PropertyId),
        _ => false,
    };

    public static bool IsModelProperty(Guid format, uint id) =>
        Is(CoreAudio.PKEY_Device_FriendlyName, format, id) ||
        Is(CoreAudio.PKEY_DeviceInterface_FriendlyName, format, id) ||
        Is(CoreAudio.PKEY_Device_ContainerId, format, id);

    private static bool Is(PROPERTYKEY key, Guid format, uint id) => key.fmtid == format && key.pid == id;
}

// The endpoint notification client handed to IMMDeviceEnumerator::RegisterEndpointNotificationCallback.
//
// Methods are in the IMMNotificationClient vtable order of the SDK header, and OnPropertyValueChanged takes
// its PROPERTYKEY by value (Interop\CoreAudio.cs declares both). MMDevAPI calls them on a thread it does not
// document, so every callback here follows the documented rules and does only this: copy the arguments
// into an EndpointNotification, hand it to the sink (which only queues a re-evaluation) and return S_OK.
// No locks, no waits, no COM calls, no Register or Unregister, no ReleaseComObject.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
//
// MMDevAPI ignores the return value, so an exception escaping a callback would vanish into an HRESULT
// nobody reads. A sink failure is therefore counted and kept here with Interlocked (no lock), and the audio
// worker reads and logs it (TakeSinkFailures). The failed sink is what would have queued the next refresh, so
// the failure is also handed to sinkFailed, which only queues a report for the worker to log now rather than
// at some later enumeration. If even that cannot be queued, its failure is counted with the sink's.
//
// The client is not AddRef'd by MMDevAPI; AudioWorker holds it in a field from Register until after
// Unregister.
internal sealed class NotificationClient : IMMNotificationClient
{
    internal const int S_OK = 0;

    private readonly Action<EndpointNotification> _sink;
    private readonly Action? _sinkFailed;
    private int _sinkFailures;
    private Exception? _lastSinkFailure;

    // sinkFailed: called on the callback thread after the sink threw. Like the sink it must not block.
    public NotificationClient(Action<EndpointNotification> sink, Action? sinkFailed = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _sinkFailed = sinkFailed;
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondevicestatechanged
    public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) =>
        Forward(new EndpointNotification(EndpointNotificationKind.StateChanged, pwstrDeviceId, dwNewState, 0, 0, Guid.Empty, 0));

    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondeviceadded
    public int OnDeviceAdded(string pwstrDeviceId) =>
        Forward(new EndpointNotification(EndpointNotificationKind.Added, pwstrDeviceId, 0, 0, 0, Guid.Empty, 0));

    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondeviceremoved
    public int OnDeviceRemoved(string pwstrDeviceId) =>
        Forward(new EndpointNotification(EndpointNotificationKind.Removed, pwstrDeviceId, 0, 0, 0, Guid.Empty, 0));

    // The id is null when no default device exists for the flow and role.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-ondefaultdevicechanged
    public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId) =>
        Forward(new EndpointNotification(EndpointNotificationKind.DefaultChanged, pwstrDefaultDeviceId, 0, flow, role, Guid.Empty, 0));

    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-onpropertyvaluechanged
    public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) =>
        Forward(new EndpointNotification(EndpointNotificationKind.PropertyChanged, pwstrDeviceId, 0, 0, 0, key.fmtid, key.pid));

    // How many notifications the sink failed to take since the last call, and the last failure.
    internal (int Count, Exception? Last) TakeSinkFailures()
    {
        int count = Interlocked.Exchange(ref _sinkFailures, 0);
        Exception? last = Interlocked.Exchange(ref _lastSinkFailure, null);
        return (count, last);
    }

    private int Forward(EndpointNotification notification)
    {
        try
        {
            _sink(notification);
        }
        catch (Exception ex)
        {
            // Kept for the worker to log; see the header comment.
            Keep(ex);
            ReportSinkFailure();
        }

        return S_OK;
    }

    private void ReportSinkFailure()
    {
        if (_sinkFailed is null)
        {
            return;
        }

        try
        {
            _sinkFailed();
        }
        catch (Exception ex)
        {
            // The report could not be queued either. Counted with the sink's failure, so the next read of the
            // counters (at the next enumeration or at Unregister) still logs both.
            Keep(ex);
        }
    }

    private void Keep(Exception ex)
    {
        Interlocked.Exchange(ref _lastSinkFailure, ex);
        Interlocked.Increment(ref _sinkFailures);
    }
}
