using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.AudioProtection;

// One read of a device's installed Bluetooth services.
//   DeviceFound  a remembered device with the pinned address was found
//   Listed       BluetoothEnumerateInstalledServices returned 0 or ERROR_MORE_DATA
//   Complete     it returned 0, so a service missing from Services is really not enabled
internal sealed record ServiceReadResult(
    string Address12,
    bool DeviceFound,
    bool Listed,
    bool Complete,
    string? DeviceName,
    bool Connected,
    IReadOnlyList<Guid> Services,
    IReadOnlyList<StepOutcome> Steps)
{
    public static ServiceReadResult NotRead(string address12, IReadOnlyList<StepOutcome> steps) =>
        new(address12, DeviceFound: false, Listed: false, Complete: false, DeviceName: null, Connected: false, [], steps);
}

// Reads the installed services of the pinned device without elevation. Nothing here changes a device.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothenumerateinstalledservices
internal sealed class ServiceStateReader
{
    public const string ServicesStep = "bt-installed-services";

    private readonly IBluetoothServiceReader _api;

    public ServiceStateReader(IBluetoothServiceReader api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ServiceReadResult Read(string address12)
    {
        ArgumentNullException.ThrowIfNull(address12);
        var steps = new List<StepOutcome>();
        BluetoothDeviceEntry? device = BluetoothDeviceLookup.Find(_api, address12, steps, out _);
        if (device is null)
        {
            return ServiceReadResult.NotRead(address12, steps);
        }

        uint rc = ReadServices(_api, device, ServicesStep, steps, out IReadOnlyList<Guid> services);
        bool listed = rc is BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA;
        return new ServiceReadResult(address12, DeviceFound: true, listed, rc == BluetoothApis.ERROR_SUCCESS,
            device.Name, device.Connected, listed ? services : [], steps);
    }

    // One BluetoothEnumerateInstalledServices call recorded as a step. 0 and ERROR_MORE_DATA (234) are
    // success; 234 means the list may be missing entries.
    public static uint ReadServices(IBluetoothServiceReader api, BluetoothDeviceEntry device, string step, IList<StepOutcome> steps, out IReadOnlyList<Guid> services)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(steps);
        uint rc = api.GetInstalledServices(device, out services);
        string detail = rc switch
        {
            BluetoothApis.ERROR_SUCCESS => Describe(services, complete: true),
            BluetoothApis.ERROR_MORE_DATA => Describe(services, complete: false),
            _ => "The installed services could not be read.",
        };
        steps.Add(StepOutcomes.FromWin32(step, rc, detail, ok: rc is BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA));
        return rc;
    }

    private static string Describe(IReadOnlyList<Guid> services, bool complete)
    {
        Guid[] watched = [ProtectedServices.Handsfree, ProtectedServices.Headset, ProtectedServices.AudioSink];
        string flags = string.Join(", ", watched.Select(g =>
            ProtectedServices.Label(g) + (services.Contains(g) ? " on" : complete ? " off" : " not listed")));
        return services.Count + (services.Count == 1 ? " service" : " services") +
               (complete ? "" : ", list incomplete") + ": " + flags + ".";
    }
}

// Turns a service read into the protection state the tray shows. Pure; a state it cannot know is Unknown,
// never guessed.
//   Handsfree and Headset both absent from a complete list   Protected (these AirPods never advertise Headset)
//   Handsfree (0000111E) listed                              NotProtected
//   Headset (00001108) listed, Handsfree absent               Partial
//   device not found, list unreadable, or Handsfree missing from an incomplete list   Unknown
internal static class ProtectionClassifier
{
    public static AudioProtectionState Classify(bool listed, bool complete, IReadOnlyCollection<Guid> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!listed)
        {
            return AudioProtectionState.Unknown;
        }

        bool handsfree = services.Contains(ProtectedServices.Handsfree);
        bool headset = services.Contains(ProtectedServices.Headset);
        if (handsfree)
        {
            return AudioProtectionState.NotProtected;
        }

        // An incomplete list proves only what it holds.
        if (!complete)
        {
            return AudioProtectionState.Unknown;
        }

        return headset ? AudioProtectionState.Partial : AudioProtectionState.Protected;
    }

    public static AudioProtectionSnapshot Snapshot(ServiceReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        bool listed = read.DeviceFound && read.Listed;
        return new AudioProtectionSnapshot(
            Classify(listed, read.Complete, read.Services),
            HandsfreeInstalled: listed && read.Services.Contains(ProtectedServices.Handsfree),
            HeadsetInstalled: listed && read.Services.Contains(ProtectedServices.Headset));
    }
}
