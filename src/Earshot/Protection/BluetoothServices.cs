using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.AudioProtection;

// A remembered Bluetooth Classic device as BluetoothFindFirstDevice returned it. Info is kept because
// BluetoothEnumerateInstalledServices and BluetoothSetServiceState take "a previously found" device.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal sealed class BluetoothDeviceEntry
{
    public BluetoothDeviceEntry(string address12, string name, bool connected, BLUETOOTH_DEVICE_INFO info)
    {
        ArgumentNullException.ThrowIfNull(address12);
        ArgumentNullException.ThrowIfNull(name);
        Address12 = address12;
        Name = name;
        Connected = connected;
        Info = info;
    }

    // 12 upper-case hex digits, the form used in BTHENUM instance ids.
    public string Address12 { get; }

    public string Name { get; }

    public bool Connected { get; }

    internal BLUETOOTH_DEVICE_INFO Info { get; }

    // The BOOL fields of BLUETOOTH_DEVICE_INFO read 32, 16 and 8 on this hardware, never 1, so they are
    // tested against zero.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_info_struct
    public static BluetoothDeviceEntry FromInfo(BLUETOOTH_DEVICE_INFO info) =>
        new(BluetoothApis.FormatAddress12(info.Address), BluetoothApis.GetName(info), info.fConnected != 0, info);
}

// The read-only Bluetooth calls protection uses. The tray and the probe hold only this.
internal interface IBluetoothServiceReader
{
    // Remembered, authenticated and connected Classic devices from the local cache, never with an inquiry.
    // Returns 0 when the enumeration ran to its end, otherwise the Win32 error that stopped it; devices holds
    // what was found before it. closeError is the error from closing the find handle, or 0.
    uint FindPairedDevices(out IReadOnlyList<BluetoothDeviceEntry> devices, out uint closeError);

    // BluetoothEnumerateInstalledServices: the service GUIDs enabled on the device. 0 is a complete list,
    // ERROR_MORE_DATA (234) a successful but incomplete one, anything else an error with an empty list.
    uint GetInstalledServices(BluetoothDeviceEntry device, out IReadOnlyList<Guid> services);
}

// The reader plus the one call that changes a device. Only the gate (and the owner's live test) holds one.
internal interface IBluetoothServiceApi : IBluetoothServiceReader
{
    // BluetoothSetServiceState with BLUETOOTH_SERVICE_ENABLE or BLUETOOTH_SERVICE_DISABLE. The error code is
    // the return value. It installs or removes the service's driver and blocks until that is done.
    uint SetServiceState(BluetoothDeviceEntry device, Guid service, bool enable);
}

// The real reader over Interop\BluetoothApis. Nothing here changes a device.
internal class BluetoothServiceReader : IBluetoothServiceReader
{
    public uint FindPairedDevices(out IReadOnlyList<BluetoothDeviceEntry> devices, out uint closeError)
    {
        uint rc = BluetoothApis.FindPairedDevices(out List<BLUETOOTH_DEVICE_INFO> found, out closeError);
        devices = found.Select(BluetoothDeviceEntry.FromInfo).ToList();
        return rc;
    }

    public uint GetInstalledServices(BluetoothDeviceEntry device, out IReadOnlyList<Guid> services)
    {
        ArgumentNullException.ThrowIfNull(device);
        uint rc = BluetoothApis.GetInstalledServices(device.Info, out Guid[] list);
        services = list;
        return rc;
    }
}

// The real service API. hRadio is NULL: the SDK header marks it optional, and the device and service reads
// worked with NULL on the owner's PC. The API page describes hRadio as a radio handle, so a hardware test
// (diag protect-unelevated) can also make the call with the first local radio's handle
// (SetServiceStateOnFirstRadio) when the NULL call is rejected with ERROR_INVALID_PARAMETER.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal sealed class BluetoothServiceApi : BluetoothServiceReader, IBluetoothServiceApi
{
    public unsafe uint SetServiceState(BluetoothDeviceEntry device, Guid service, bool enable)
    {
        ArgumentNullException.ThrowIfNull(device);
        BLUETOOTH_DEVICE_INFO info = device.Info;
        uint flags = enable ? BluetoothApis.BLUETOOTH_SERVICE_ENABLE : BluetoothApis.BLUETOOTH_SERVICE_DISABLE;
        return BluetoothApis.BluetoothSetServiceState(0, &info, &service, flags);
    }

    // diag protect-unelevated only. The same call with the handle of the first local radio. radioError is the
    // Win32 error from finding the radio (ERROR_NO_MORE_ITEMS when there is none); the call is then not made and
    // the result is null. closeError is the first error from closing the handles, or 0.
    public static unsafe uint? SetServiceStateOnFirstRadio(BluetoothDeviceEntry device, Guid service, bool enable, out uint radioError, out uint closeError)
    {
        ArgumentNullException.ThrowIfNull(device);
        closeError = BluetoothApis.ERROR_SUCCESS;
        radioError = BluetoothApis.OpenFirstRadio(out nint find, out nint radio);
        if (radioError != BluetoothApis.ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            BLUETOOTH_DEVICE_INFO info = device.Info;
            uint flags = enable ? BluetoothApis.BLUETOOTH_SERVICE_ENABLE : BluetoothApis.BLUETOOTH_SERVICE_DISABLE;
            return BluetoothApis.BluetoothSetServiceState(radio, &info, &service, flags);
        }
        finally
        {
            closeError = BluetoothApis.CloseRadio(find, radio);
        }
    }
}

// The service classes protection looks at.
internal static class ProtectedServices
{
    public static readonly Guid Handsfree = BluetoothApis.HandsfreeServiceClass;   // 0000111E
    public static readonly Guid Headset = BluetoothApis.HeadsetServiceClass;       // 00001108
    public static readonly Guid AudioSink = BluetoothApis.AudioSinkServiceClass;   // 0000110B, always left on

    // The services protection turns off, in the order it turns them off. A2DP sink is never among them.
    public static readonly IReadOnlyList<Guid> TurnedOff = [Handsfree, Headset];

    public static bool IsTurnedOff(Guid service) => service == Handsfree || service == Headset;

    public static string Label(Guid service) =>
        service == Handsfree ? "Handsfree"
        : service == Headset ? "Headset"
        : service == AudioSink ? "A2DP sink"
        : service.ToString("D");
}

// How one BluetoothSetServiceState call went.
internal enum ServiceChange
{
    Changed,         // 0
    AlreadyInState,  // E_INVALIDARG (0x80070057 in the DWORD): already enabled or disabled, not an error
    NotSupported,    // ERROR_SERVICE_DOES_NOT_EXIST (1060): the device has no such service, nothing to do
    BadFlags,        // ERROR_INVALID_PARAMETER (87): the call's parameters were rejected (the flags, or perhaps the NULL radio)
    Failed,          // anything else, including a possible ERROR_ACCESS_DENIED
}

// The documented BluetoothSetServiceState results. The code is the return value, not GetLastError.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
internal static class ServiceStateResults
{
    public static ServiceChange Map(uint rc) => rc switch
    {
        BluetoothApis.ERROR_SUCCESS => ServiceChange.Changed,
        BluetoothApis.E_INVALIDARG => ServiceChange.AlreadyInState,
        BluetoothApis.ERROR_SERVICE_DOES_NOT_EXIST => ServiceChange.NotSupported,
        BluetoothApis.ERROR_INVALID_PARAMETER => ServiceChange.BadFlags,
        _ => ServiceChange.Failed,
    };

    public static bool IsOk(ServiceChange change) =>
        change is ServiceChange.Changed or ServiceChange.AlreadyInState or ServiceChange.NotSupported;

    public static string StepName(Guid service, bool enable) =>
        (enable ? "bt-service-enable:" : "bt-service-disable:") + ProtectedServices.Label(service);

    public static string SkipStepName(Guid service, bool enable) =>
        (enable ? "bt-service-enable-skip:" : "bt-service-disable-skip:") + ProtectedServices.Label(service);

    // A service already in the wanted state, so BluetoothSetServiceState was not called. It is a success, but
    // it carries NOT_ATTEMPTED rather than 0, so it can never be read as a result the call returned.
    public static StepOutcome Skipped(Guid service, bool enable, string detail) =>
        new(SkipStepName(service, enable), Ok: true, NativeCodes.NotAttempted, NativeCodes.Name(NativeCodes.NotAttempted), detail);

    public static StepOutcome Step(Guid service, bool enable, uint rc, TimeSpan took)
    {
        ServiceChange change = Map(rc);
        string detail = change switch
        {
            ServiceChange.Changed => enable ? "Turned on." : "Turned off.",
            ServiceChange.AlreadyInState => (enable ? "Already on" : "Already off") + " (E_INVALIDARG), not an error.",
            ServiceChange.NotSupported => "This device does not have the service, nothing to do.",
            ServiceChange.BadFlags => "Windows rejected the call parameters (ERROR_INVALID_PARAMETER).",
            _ => "Windows did not change the service.",
        };
        string ms = ((long)took.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return StepOutcomes.FromWin32(StepName(service, enable), rc, detail + " Took " + ms + " ms.", ok: IsOk(change));
    }
}

// Finds the remembered device with the pinned address. Every call is recorded as a step.
internal static class BluetoothDeviceLookup
{
    public const string FindStep = "bt-find-device";

    // The device, or null. listFailed is true when the enumeration itself failed (rather than finding no
    // device with the address).
    public static BluetoothDeviceEntry? Find(IBluetoothServiceReader api, string address12, IList<StepOutcome> steps, out bool listFailed)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(steps);
        listFailed = false;
        if (!BoundaryValidation.IsAddress12(address12))
        {
            steps.Add(StepOutcomes.NotAttempted(FindStep, "The pinned address is not 12 upper-case hex characters."));
            return null;
        }

        uint rc = api.FindPairedDevices(out IReadOnlyList<BluetoothDeviceEntry> devices, out uint closeError);
        if (closeError != BluetoothApis.ERROR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromWin32("bt-find-close", closeError, "The device search handle did not close."));
        }

        if (rc != BluetoothApis.ERROR_SUCCESS)
        {
            steps.Add(StepOutcomes.FromWin32(FindStep, rc, "The paired device list could not be read in full."));
        }

        BluetoothDeviceEntry? match = devices.FirstOrDefault(d => string.Equals(d.Address12, address12, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
        }

        if (rc != BluetoothApis.ERROR_SUCCESS)
        {
            listFailed = true;
            return null;
        }

        steps.Add(StepOutcomes.FromWin32(FindStep, BluetoothApis.ERROR_NOT_FOUND, "No paired Bluetooth device has address " + address12 + "."));
        return null;
    }
}
