using Earshot.AudioProtection;
using Earshot.Interop;
using Earshot.Tests.Phase4;

namespace Earshot.Tests.Phase6;

internal sealed record BluetoothCall(string Kind, Guid Service, bool Enable);

// An in-memory Bluetooth service stack. Each remembered device has the services it supports and the subset
// enabled now. SetServiceState behaves as documented unless a test overrides the result: 1060 for a service
// the device does not have, E_INVALIDARG when the service is already in the requested state, otherwise 0
// with the change applied. Every call is recorded so a test can assert exactly what was called.
internal sealed class FakeBluetoothServices : IBluetoothServiceApi
{
    public static readonly Guid Sdp = new("00001000-0000-1000-8000-00805F9B34FB");
    public static readonly Guid AvrcpTarget = new("0000110C-0000-1000-8000-00805F9B34FB");
    public static readonly Guid Avrcp = new("0000110E-0000-1000-8000-00805F9B34FB");
    public static readonly Guid Gatt = new("00001801-0000-1000-8000-00805F9B34FB");
    public static readonly Guid Uarps = new("4715650B-5E9D-4AC2-B898-A4FC0AA5DF78");
    public static readonly Guid AapServer = new("74EC2172-0BAD-4D01-8F77-997B2BE0722A");

    // The eight installed services recorded on the owner's AirPods in phase 0. Headset is not among them.
    public static readonly Guid[] RecordedAirPodsServices =
    [
        Sdp, ProtectedServices.AudioSink, AvrcpTarget, Avrcp, ProtectedServices.Handsfree, Gatt, Uarps, AapServer,
    ];

    private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.Ordinal);

    public List<BluetoothCall> Calls { get; } = new();

    public IEnumerable<BluetoothCall> SetCalls => Calls.Where(c => c.Kind == "set");

    public uint FindResult { get; set; }

    public uint CloseError { get; set; }

    // A result other than 0 or 234 makes GetInstalledServices fail with an empty list. 234 returns
    // IncompleteList (or the enabled set) as an incomplete list.
    public uint ServicesResult { get; set; }

    public IReadOnlyList<Guid>? IncompleteList { get; set; }

    // Returns the code for a call, or null for the documented behaviour.
    public Func<Guid, bool, uint?>? SetResult { get; set; }

    // Runs before a SetServiceState call is answered.
    public Action<Guid, bool>? OnSet { get; set; }

    public static FakeBluetoothServices AirPods()
    {
        var fake = new FakeBluetoothServices();
        fake.Add(RecordedNodes.AirPodsAddress, "Owner\u2019s AirPods Pro", connected: true, RecordedAirPodsServices);
        fake.Add(RecordedNodes.IPhoneAddress, "iPhone", connected: false, [Sdp, AvrcpTarget, Avrcp]);
        return fake;
    }

    public FakeDevice Add(string address12, string name, bool connected, IEnumerable<Guid> services)
    {
        var device = new FakeDevice(address12, name, connected, services);
        _devices[address12] = device;
        return device;
    }

    public void Remove(string address12) => _devices.Remove(address12);

    public FakeDevice this[string address12] => _devices[address12];

    public uint FindPairedDevices(out IReadOnlyList<BluetoothDeviceEntry> devices, out uint closeError)
    {
        Calls.Add(new BluetoothCall("find", Guid.Empty, false));
        closeError = CloseError;
        devices = FindResult == 0
            ? _devices.Values.Select(d => new BluetoothDeviceEntry(d.Address12, d.Name, d.Connected, default(BLUETOOTH_DEVICE_INFO))).ToList()
            : [];
        return FindResult;
    }

    public uint GetInstalledServices(BluetoothDeviceEntry device, out IReadOnlyList<Guid> services)
    {
        Calls.Add(new BluetoothCall("list", Guid.Empty, false));
        FakeDevice fake = _devices[device.Address12];
        if (ServicesResult is not (BluetoothApis.ERROR_SUCCESS or BluetoothApis.ERROR_MORE_DATA))
        {
            services = [];
            return ServicesResult;
        }

        services = ServicesResult == BluetoothApis.ERROR_MORE_DATA && IncompleteList is not null
            ? IncompleteList.ToList()
            : fake.Enabled.ToList();
        return ServicesResult;
    }

    public uint SetServiceState(BluetoothDeviceEntry device, Guid service, bool enable)
    {
        Calls.Add(new BluetoothCall("set", service, enable));
        OnSet?.Invoke(service, enable);
        FakeDevice fake = _devices[device.Address12];
        uint? forced = SetResult?.Invoke(service, enable);
        if (forced is uint code)
        {
            if (code == BluetoothApis.ERROR_SUCCESS)
            {
                fake.Apply(service, enable);
            }

            return code;
        }

        if (!fake.Supported.Contains(service))
        {
            return BluetoothApis.ERROR_SERVICE_DOES_NOT_EXIST;
        }

        if (fake.Enabled.Contains(service) == enable)
        {
            return BluetoothApis.E_INVALIDARG;
        }

        fake.Apply(service, enable);
        return BluetoothApis.ERROR_SUCCESS;
    }
}

internal sealed class FakeDevice
{
    public FakeDevice(string address12, string name, bool connected, IEnumerable<Guid> services)
    {
        Address12 = address12;
        Name = name;
        Connected = connected;
        Supported = services.ToHashSet();
        Enabled = services.ToList();
    }

    public string Address12 { get; }

    public string Name { get; }

    public bool Connected { get; }

    public HashSet<Guid> Supported { get; }

    // Kept in order so a list reads back the way it was installed.
    public List<Guid> Enabled { get; }

    public void Apply(Guid service, bool enable)
    {
        Enabled.Remove(service);
        if (enable)
        {
            Supported.Add(service);
            Enabled.Add(service);
        }
    }

    // A service turned off outside Earshot (for example in the Bluetooth settings).
    public void TurnOffOutside(Guid service) => Enabled.Remove(service);

    public void AddSupported(Guid service)
    {
        Supported.Add(service);
        if (!Enabled.Contains(service))
        {
            Enabled.Add(service);
        }
    }
}
