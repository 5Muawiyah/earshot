using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// Managed stand-ins for the Core Audio and DeviceTopology objects the endpoint reads and the topology walk use.
// Nothing here reaches Windows except PropVariantClear and CoTaskMemFree on memory the fakes allocate. Every
// object a fake hands out is recorded in a ReleaseLedger, and the code under test releases through the same
// ledger, so a test can check that each object was released exactly once.
internal static class FakeHr
{
    public const int SOk = 0;
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int EFail = unchecked((int)0x80004005);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int EOutOfMemory = unchecked((int)0x8007000E);
}

internal sealed class ReleaseLedger
{
    private readonly Dictionary<object, int> _lent = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _released = new(ReferenceEqualityComparer.Instance);

    public T Lend<T>(T item)
        where T : class
    {
        _lent[item] = _lent.GetValueOrDefault(item) + 1;
        return item;
    }

    public void Release(object item)
    {
        Assert.IsTrue(_lent.ContainsKey(item), "Released an object that was never handed out: " + item.GetType().Name);
        _released[item] = _released.GetValueOrDefault(item) + 1;
    }

    public int Lent(object item) => _lent.GetValueOrDefault(item);

    public int Released(object item) => _released.GetValueOrDefault(item);

    public void AssertBalanced()
    {
        foreach ((object item, int count) in _lent)
        {
            Assert.AreEqual(count, Released(item), item.GetType().Name + " handed out " + count + " times");
        }
    }
}

internal sealed class FakeEnumerator(ReleaseLedger ledger) : IMMDeviceEnumerator
{
    public Dictionary<string, FakeDevice> Devices { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> GetDeviceFailures { get; } = new(StringComparer.Ordinal);

    public List<string> GetDeviceCalls { get; } = new();

    public FakeCollection? Collection { get; set; }

    public int EnumerateHr { get; set; }

    public (int DataFlow, uint StateMask)? EnumerateArguments { get; private set; }

    public FakeDevice Add(FakeDevice device)
    {
        Devices.Add(device.Id, device);
        return device;
    }

    public int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection? ppDevices)
    {
        EnumerateArguments = (dataFlow, dwStateMask);
        if (EnumerateHr < 0 || Collection is null)
        {
            ppDevices = null;
            return EnumerateHr < 0 ? EnumerateHr : FakeHr.EFail;
        }

        ppDevices = ledger.Lend(Collection);
        return FakeHr.SOk;
    }

    public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppEndpoint)
    {
        ppEndpoint = null;
        return FakeHr.ENotImpl;
    }

    public int GetDevice(string pwstrId, out IMMDevice? ppDevice)
    {
        GetDeviceCalls.Add(pwstrId);
        if (GetDeviceFailures.TryGetValue(pwstrId, out int hr))
        {
            ppDevice = null;
            return hr;
        }

        if (!Devices.TryGetValue(pwstrId, out FakeDevice? device))
        {
            ppDevice = null;
            return CoreAudio.E_NOTFOUND;
        }

        ppDevice = ledger.Lend(device);
        return FakeHr.SOk;
    }

    public int RegisterHr { get; set; } = FakeHr.ENotImpl;

    public int UnregisterHr { get; set; } = FakeHr.ENotImpl;

    public int RegisterEndpointNotificationCallback(IMMNotificationClient pClient) => RegisterHr;

    public int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient) => UnregisterHr;
}

internal sealed class FakeCollection(ReleaseLedger ledger) : IMMDeviceCollection
{
    public List<(int Hr, FakeDevice? Device)> Items { get; } = new();

    public int CountHr { get; set; }

    public int GetCount(out uint pcDevices)
    {
        pcDevices = CountHr < 0 ? 0 : (uint)Items.Count;
        return CountHr;
    }

    public int Item(uint nDevice, out IMMDevice? ppDevice)
    {
        (int hr, FakeDevice? device) = Items[(int)nDevice];
        if (hr < 0 || device is null)
        {
            ppDevice = null;
            return hr;
        }

        ppDevice = ledger.Lend(device);
        return FakeHr.SOk;
    }
}

// An adapter (KS filter) device, or the base of an endpoint device. Activate hands out the topology or the
// control through a real COM callable wrapper, so CoreAudio.Activate and ComActivation.TakeInterface run
// exactly as they do against MMDevAPI.
internal class FakeDevice(ReleaseLedger ledger, string id) : IMMDevice
{
    public string Id { get; } = id;

    public int IdHr { get; set; }

    public uint State { get; set; } = CoreAudio.DEVICE_STATE_ACTIVE;

    public int StateHr { get; set; }

    public FakePropertyStore? Store { get; set; }

    public int OpenStoreHr { get; set; }

    public FakeTopology? Topology { get; set; }

    public FakeKsControl? Control { get; set; }

    public Dictionary<Guid, int> ActivateFailures { get; } = new();

    public List<Guid> Activations { get; } = new();

    protected ReleaseLedger Ledger { get; } = ledger;

    public int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, out nint ppInterface)
    {
        Activations.Add(iid);
        ppInterface = 0;
        Assert.AreEqual(0, pActivationParams, "Activation parameters must be null.");
        if (ActivateFailures.TryGetValue(iid, out int hr))
        {
            return hr;
        }

        if (iid == typeof(IDeviceTopology).GUID && Topology is not null)
        {
            ppInterface = Marshal.GetComInterfaceForObject(Ledger.Lend(Topology), typeof(IDeviceTopology));
            return FakeHr.SOk;
        }

        if (iid == typeof(IKsControl).GUID && Control is not null)
        {
            ppInterface = Marshal.GetComInterfaceForObject(Ledger.Lend(Control), typeof(IKsControl));
            return FakeHr.SOk;
        }

        return CoreAudio.E_NOINTERFACE;
    }

    public int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties)
    {
        Assert.AreEqual(CoreAudio.STGM_READ, stgmAccess, "Property stores are opened for reading only.");
        if (OpenStoreHr < 0 || Store is null)
        {
            ppProperties = null;
            return OpenStoreHr < 0 ? OpenStoreHr : FakeHr.EFail;
        }

        ppProperties = Ledger.Lend(Store);
        return FakeHr.SOk;
    }

    public int GetId(out nint ppstrId)
    {
        ppstrId = IdHr < 0 ? 0 : Marshal.StringToCoTaskMemUni(Id);
        return IdHr;
    }

    public int GetState(out uint pdwState)
    {
        pdwState = StateHr < 0 ? 0 : State;
        return StateHr;
    }
}

internal sealed class FakeEndpointDevice(ReleaseLedger ledger, string id) : FakeDevice(ledger, id), IMMEndpoint
{
    public int DataFlow { get; set; } = CoreAudio.eRender;

    public int DataFlowHr { get; set; }

    public int GetDataFlow(out int pDataFlow)
    {
        pDataFlow = DataFlow;
        return DataFlowHr;
    }
}

// Values are handed out the way a real store does: VT_LPWSTR and VT_CLSID point to CoTaskMem memory that the
// reader's PropVariantClear frees. A key with no value gives VT_EMPTY.
internal sealed class FakePropertyStore : IPropertyStore
{
    private readonly Dictionary<(Guid, uint), (int Hr, object? Value)> _values = new();

    public int Writes { get; private set; }

    public FakePropertyStore Set(PROPERTYKEY key, object value)
    {
        _values[(key.fmtid, key.pid)] = (FakeHr.SOk, value);
        return this;
    }

    public FakePropertyStore Fail(PROPERTYKEY key, int hr)
    {
        _values[(key.fmtid, key.pid)] = (hr, null);
        return this;
    }

    public int GetCount(out uint cProps)
    {
        cProps = (uint)_values.Count;
        return FakeHr.SOk;
    }

    public int GetAt(uint iProp, out PROPERTYKEY pkey)
    {
        pkey = default;
        return FakeHr.ENotImpl;
    }

    public int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv)
    {
        pv = default;
        if (!_values.TryGetValue((key.fmtid, key.pid), out (int Hr, object? Value) entry))
        {
            return FakeHr.SOk;
        }

        if (entry.Hr < 0)
        {
            return entry.Hr;
        }

        switch (entry.Value)
        {
            case string text:
                pv.vt = PropVariantInterop.VT_LPWSTR;
                pv.pointerValue = Marshal.StringToCoTaskMemUni(text);
                break;
            case Guid guid:
                pv.vt = PropVariantInterop.VT_CLSID;
                pv.pointerValue = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
                Marshal.StructureToPtr(guid, pv.pointerValue, fDeleteOld: false);
                break;
            case uint number:
                pv.vt = PropVariantInterop.VT_UI4;
                pv.ulVal = number;
                break;
            default:
                throw new AssertFailedException("The fake store cannot hold a " + entry.Value?.GetType().Name + ".");
        }

        return FakeHr.SOk;
    }

    public int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar)
    {
        Writes++;
        return FakeHr.ENotImpl;
    }

    public int Commit()
    {
        Writes++;
        return FakeHr.ENotImpl;
    }
}

internal sealed class FakeTopology(ReleaseLedger ledger) : IDeviceTopology
{
    public FakeConnector? Connector { get; set; }

    public int GetConnectorHr { get; set; }

    public int GetConnectorCount(out uint pCount)
    {
        pCount = Connector is null ? 0u : 1u;
        return FakeHr.SOk;
    }

    public int GetConnector(uint nIndex, out IConnector? ppConnector)
    {
        Assert.AreEqual(0u, nIndex, "An endpoint topology has one connector, number 0.");
        if (GetConnectorHr < 0 || Connector is null)
        {
            ppConnector = null;
            return GetConnectorHr < 0 ? GetConnectorHr : CoreAudio.E_NOTFOUND;
        }

        ppConnector = ledger.Lend(Connector);
        return FakeHr.SOk;
    }

    public int GetSubunitCount(out uint pCount)
    {
        pCount = 0;
        return FakeHr.SOk;
    }

    public int GetSubunit(uint nIndex, out object? ppSubunit)
    {
        ppSubunit = null;
        return FakeHr.ENotImpl;
    }

    public int GetPartById(uint nId, out IPart? ppPart)
    {
        ppPart = null;
        return FakeHr.ENotImpl;
    }

    public int GetDeviceId(out nint ppwstrDeviceId)
    {
        ppwstrDeviceId = 0;
        return FakeHr.ENotImpl;
    }

    public int GetSignalPath(IPart pIPartFrom, IPart pIPartTo, int bRejectMixedPaths, out object? ppParts)
    {
        ppParts = null;
        return FakeHr.ENotImpl;
    }
}

internal sealed class FakeConnector : IConnector
{
    public string? AdapterId { get; set; }

    public int AdapterIdHr { get; set; }

    // ConnectTo and Disconnect edit the software topology. The walk must never call them.
    public int TopologyEdits { get; private set; }

    public int GetConnectorType(out int pType)
    {
        pType = DeviceTopology.Software_Fixed;
        return FakeHr.SOk;
    }

    public int GetDataFlow(out int pFlow)
    {
        pFlow = DeviceTopology.In;
        return FakeHr.SOk;
    }

    public int ConnectTo(IConnector pConnectTo)
    {
        TopologyEdits++;
        return FakeHr.ENotImpl;
    }

    public int Disconnect()
    {
        TopologyEdits++;
        return FakeHr.ENotImpl;
    }

    public int IsConnected(out int pbConnected)
    {
        pbConnected = AdapterId is null ? 0 : 1;
        return FakeHr.SOk;
    }

    public int GetConnectedTo(out IConnector? ppConTo)
    {
        ppConTo = null;
        return FakeHr.ENotImpl;
    }

    public int GetConnectorIdConnectedTo(out nint ppwstrConnectorId)
    {
        ppwstrConnectorId = 0;
        return FakeHr.ENotImpl;
    }

    public int GetDeviceIdConnectedTo(out nint ppwstrDeviceId)
    {
        if (AdapterIdHr < 0 || AdapterId is null)
        {
            ppwstrDeviceId = 0;
            return AdapterIdHr < 0 ? AdapterIdHr : CoreAudio.E_NOTFOUND;
        }

        ppwstrDeviceId = Marshal.StringToCoTaskMemUni(AdapterId);
        return FakeHr.SOk;
    }
}

internal sealed record KsRequest(Guid Set, uint Id, uint Flags, uint PropertyLength, uint DataLength);

// Answers KSPROPERTY_PIN_CTYPES and records every request. Method and event requests are counted and refused.
internal sealed class FakeKsControl : IKsControl
{
    public uint PinCount { get; set; } = 2;

    public List<KsRequest> Properties { get; } = new();

    public int MethodsAndEvents { get; private set; }

    public int KsProperty(ref KSIDENTIFIER property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
    {
        Properties.Add(new KsRequest(property.Set, property.Id, property.Flags, propertyLength, dataLength));
        if (property.Set == KsControl.KSPROPSETID_Pin && property.Id == KsControl.KSPROPERTY_PIN_CTYPES &&
            property.Flags == KsControl.KSPROPERTY_TYPE_GET && propertyData != 0 && dataLength >= sizeof(uint))
        {
            Marshal.WriteInt32(propertyData, unchecked((int)PinCount));
            bytesReturned = sizeof(uint);
            return FakeHr.SOk;
        }

        bytesReturned = 0;
        return FakeHr.ENotImpl;
    }

    public int KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
    {
        MethodsAndEvents++;
        bytesReturned = 0;
        return FakeHr.ENotImpl;
    }

    public int KsEvent(ref KSIDENTIFIER eventProperty, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
    {
        MethodsAndEvents++;
        bytesReturned = 0;
        return FakeHr.ENotImpl;
    }
}
