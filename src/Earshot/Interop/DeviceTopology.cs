using System.Runtime.InteropServices;

namespace Earshot.Interop;

// DeviceTopology API declarations, in SDK IDL order (um\devicetopology.idl).
// The documented path from an endpoint to its KS filter is:
//   endpoint IMMDevice.Activate(IDeviceTopology) -> GetConnector(0) -> GetDeviceIdConnectedTo
//   -> IMMDeviceEnumerator.GetDevice(adapterId) -> adapter IMMDevice.Activate(IKsControl)
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-topologies
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/using-the-ikscontrol-interface-to-access-audio-properties
internal static class DeviceTopology
{
    // CLSCTX (wtypesbase.h). CLSCTX_ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER.
    // https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ne-wtypesbase-clsctx
    internal const uint CLSCTX_INPROC_SERVER = 0x1;
    internal const uint CLSCTX_INPROC_HANDLER = 0x2;
    internal const uint CLSCTX_LOCAL_SERVER = 0x4;
    internal const uint CLSCTX_REMOTE_SERVER = 0x10;
    internal const uint CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER;

    // ConnectorType
    // https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/ne-devicetopology-connectortype
    internal const int Unknown_Connector = 0;
    internal const int Physical_Internal = 1;
    internal const int Physical_External = 2;
    internal const int Software_IO = 3;
    internal const int Software_Fixed = 4;
    internal const int Network = 5;

    // DataFlow (topology data flow, not EDataFlow).
    // https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/ne-devicetopology-dataflow
    internal const int In = 0;
    internal const int Out = 1;

    // PartType
    // https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/ne-devicetopology-parttype
    internal const int Connector = 0;
    internal const int Subunit = 1;

    // IPart::Activate for interface T (IID from T's [Guid]) with CLSCTX_ALL. Note the argument order
    // differs from IMMDevice::Activate. Returns the HRESULT; result is null on failure.
    // https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nf-devicetopology-ipart-activate
    internal static int Activate<T>(IPart part, out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(part);
        Guid iid = typeof(T).GUID;
        int hr = part.Activate(CLSCTX_ALL, ref iid, out nint pointer);
        return ComActivation.TakeInterface(hr, pointer, out result);
    }
}

// An endpoint's topology always has exactly one connector, number 0.
// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-idevicetopology
[ComImport]
[Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    [PreserveSig]
    int GetConnectorCount(out uint pCount);

    [PreserveSig]
    int GetConnector(uint nIndex, out IConnector? ppConnector);

    [PreserveSig]
    int GetSubunitCount(out uint pCount);

    // ISubunit is not declared; the object comes back as IUnknown.
    [PreserveSig]
    int GetSubunit(uint nIndex, [MarshalAs(UnmanagedType.IUnknown)] out object? ppSubunit);

    [PreserveSig]
    int GetPartById(uint nId, out IPart? ppPart);

    // ppwstrDeviceId is CoTaskMem-allocated: read it with CoreAudio.TakeCoTaskString.
    [PreserveSig]
    int GetDeviceId(out nint ppwstrDeviceId);

    // IPartsList is not declared; the list comes back as IUnknown.
    [PreserveSig]
    int GetSignalPath(IPart pIPartFrom, IPart pIPartTo, int bRejectMixedPaths, [MarshalAs(UnmanagedType.IUnknown)] out object? ppParts);
}

// Never call ConnectTo or Disconnect: they edit the software topology, they do not connect Bluetooth.
// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iconnector
[ComImport]
[Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    // IConnector::GetType. Renamed so it does not hide object.GetType; only the slot order matters.
    [PreserveSig]
    int GetConnectorType(out int pType);

    [PreserveSig]
    int GetDataFlow(out int pFlow);

    [PreserveSig]
    int ConnectTo(IConnector pConnectTo);

    [PreserveSig]
    int Disconnect();

    [PreserveSig]
    int IsConnected(out int pbConnected);

    // Fails (0x80070002 or 0xE0000225) when the adapter is gone, where GetDeviceIdConnectedTo still works.
    [PreserveSig]
    int GetConnectedTo(out IConnector? ppConTo);

    // CoTaskMem-allocated: read it with CoreAudio.TakeCoTaskString.
    [PreserveSig]
    int GetConnectorIdConnectedTo(out nint ppwstrConnectorId);

    // The robust call for the adapter id. CoTaskMem-allocated; E_NOTFOUND when not connected.
    // https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nf-devicetopology-iconnector-getdeviceidconnectedto
    [PreserveSig]
    int GetDeviceIdConnectedTo(out nint ppwstrDeviceId);
}

// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-ipart
[ComImport]
[Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    // CoTaskMem-allocated: read it with CoreAudio.TakeCoTaskString.
    [PreserveSig]
    int GetName(out nint ppwstrName);

    [PreserveSig]
    int GetLocalId(out uint pnId);

    // CoTaskMem-allocated: read it with CoreAudio.TakeCoTaskString.
    [PreserveSig]
    int GetGlobalId(out nint ppwstrGlobalId);

    [PreserveSig]
    int GetPartType(out int pPartType);

    [PreserveSig]
    int GetSubType(out Guid pSubType);

    [PreserveSig]
    int GetControlInterfaceCount(out uint pCount);

    // IControlInterface is not declared; the object comes back as IUnknown.
    [PreserveSig]
    int GetControlInterface(uint nIndex, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterfaceDesc);

    [PreserveSig]
    int EnumPartsIncoming([MarshalAs(UnmanagedType.IUnknown)] out object? ppParts);

    [PreserveSig]
    int EnumPartsOutgoing([MarshalAs(UnmanagedType.IUnknown)] out object? ppParts);

    [PreserveSig]
    int GetTopologyObject(out IDeviceTopology? ppTopology);

    // Argument order: class context first, then the IID. Use DeviceTopology.Activate<T>.
    [PreserveSig]
    int Activate(uint dwClsContext, ref Guid refiid, out nint ppvObject);

    // IControlChangeNotify is not declared; Earshot does not register control callbacks.
    [PreserveSig]
    int RegisterControlChangeCallback(ref Guid riid, nint pNotify);

    [PreserveSig]
    int UnregisterControlChangeCallback(nint pNotify);
}

// Reached through the adapter-side connector: IPart.Activate(CLSCTX_ALL, IID_IKsJackDescription).
// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iksjackdescription
[ComImport]
[Guid("4509F757-2D46-4637-8E62-CE7DB944F57B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsJackDescription
{
    [PreserveSig]
    int GetJackCount(out uint pcJacks);

    [PreserveSig]
    int GetJackDescription(uint nJack, out KSJACK_DESCRIPTION pDescription);
}

// Seven 4-byte fields, 28 bytes. IsConnected is a BOOL: test it with != 0.
// https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/ns-devicetopology-ksjack_description
[StructLayout(LayoutKind.Sequential)]
internal struct KSJACK_DESCRIPTION
{
    public uint ChannelMapping;
    public uint Color;
    public int ConnectionType;
    public int GeoLocation;
    public int GenLocation;
    public int PortConnection;
    public int IsConnected;
}
