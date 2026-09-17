using System.Runtime.InteropServices;

namespace Earshot.Interop;

// MMDevice API declarations. Vtable order is the SDK IDL order (um\mmdeviceapi.idl, um\propsys.idl),
// not the alphabetical order the learn.microsoft.com interface pages use.
//
// Every interface here is [local] with no proxy/stub and the objects are not agile, so an RCW is only
// usable from the apartment that created it. All of this runs on the one MTA audio worker.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
//
// Methods keep their HRESULT ([PreserveSig]) so every failure is recorded as a StepOutcome rather than
// thrown. Several failures are raw SetupAPI codes (0xE0000225, 0xE000020B), not HRESULT_FROM_WIN32.
// Objects are created and interface pointers wrapped through ComActivation, which also returns the
// HRESULT instead of throwing.
internal static class CoreAudio
{
    // EDataFlow
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-edataflow
    internal const int eRender = 0;
    internal const int eCapture = 1;
    internal const int eAll = 2;

    // ERole
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-erole
    internal const int eConsole = 0;
    internal const int eMultimedia = 1;
    internal const int eCommunications = 2;

    // DEVICE_STATE_XXX. NOTPRESENT also covers an adapter devnode disabled in Device Manager.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
    internal const uint DEVICE_STATE_ACTIVE = 0x00000001;
    internal const uint DEVICE_STATE_DISABLED = 0x00000002;
    internal const uint DEVICE_STATE_NOTPRESENT = 0x00000004;
    internal const uint DEVICE_STATE_UNPLUGGED = 0x00000008;
    internal const uint DEVICE_STATEMASK_ALL = 0x0000000F;

    // STGM_READ (coml2api.h). Non-admin clients only get read access to endpoint property stores.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-openpropertystore
    internal const uint STGM_READ = 0x00000000;

    // HRESULTs this path classifies as "not available". Names come from NativeCodes.
    internal const int E_NOTFOUND = unchecked((int)0x80070490);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    internal const int ERROR_NO_SUCH_DEVICE_INTERFACE = unchecked((int)0xE0000225);
    internal const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);
    internal const int HRESULT_ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);
    internal const int HRESULT_ERROR_PATH_NOT_FOUND = unchecked((int)0x80070003);

    // CLSID_MMDeviceEnumerator.
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    // PKEY_Device_FriendlyName, VT_LPWSTR.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-device-friendlyname
    internal static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // PKEY_DeviceInterface_FriendlyName, VT_LPWSTR (functiondiscoverykeys_devpkey.h).
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-deviceinterface-friendlyname
    internal static readonly PROPERTYKEY PKEY_DeviceInterface_FriendlyName =
        new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);

    // PKEY_Device_ContainerId (= DEVPKEY_Device_ContainerId). Arrives as VT_CLSID through the
    // endpoint and adapter property stores.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid
    internal static readonly PROPERTYKEY PKEY_Device_ContainerId =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    // PKEY_AudioEndpoint_FormFactor, VT_UI4.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-formfactor
    internal static readonly PROPERTYKEY PKEY_AudioEndpoint_FormFactor =
        new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    // Creates the enumerator with CoCreateInstance and CLSCTX_INPROC_SERVER, as Microsoft's sample does.
    // Call it on the thread that will use the RCW. Returns the HRESULT; enumerator is null on failure.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events
    internal static int TryCreateEnumerator(out IMMDeviceEnumerator? enumerator) =>
        ComActivation.Create(CLSID_MMDeviceEnumerator, ComActivation.CLSCTX_INPROC_SERVER, out enumerator);

    // Reads and frees a CoTaskMem string returned through an LPWSTR* out parameter (GetId,
    // GetDeviceIdConnectedTo, GetName). Returns null for a null pointer.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid
    internal static string? TakeCoTaskString(nint pointer)
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    // IMMDevice::Activate for interface T (IID taken from T's [Guid]) with CLSCTX_ALL and no activation
    // parameters, as the documented topology path does. Returns the HRESULT; result is null on failure.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-activate
    internal static int Activate<T>(IMMDevice device, out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(device);
        Guid iid = typeof(T).GUID;
        int hr = device.Activate(ref iid, DeviceTopology.CLSCTX_ALL, 0, out nint pointer);
        return ComActivation.TakeInterface(hr, pointer, out result);
    }
}

// PROPERTYKEY { GUID fmtid; DWORD pid; }, 20 bytes.
// https://learn.microsoft.com/en-us/windows/win32/api/wtypes/ns-wtypes-propertykey
[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection? ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppEndpoint);

    // Also accepts an adapter id from IConnector::GetDeviceIdConnectedTo. Unknown ids give E_NOTFOUND;
    // a vanished KS filter gives 0xE0000225.
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);

    // Does not AddRef the client: keep the client object alive until after Unregister. Never call
    // Register or Unregister from inside a callback.
    // https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-registerendpointnotificationcallback
    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdevicecollection
[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice? ppDevice);
}

// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdevice
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // pActivationParams must be null (0) for IDeviceTopology and IKsControl. ppInterface is an owned
    // reference: use CoreAudio.Activate<T>, which wraps and releases it.
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, out nint ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties);

    // ppstrId is CoTaskMem-allocated: read it with CoreAudio.TakeCoTaskString.
    [PreserveSig]
    int GetId(out nint ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

// Endpoints only. Adapter devices from GetDevice(adapterId) did not support it on the owner's PC.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immendpoint
[ComImport]
[Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpoint
{
    [PreserveSig]
    int GetDataFlow(out int pDataFlow);
}

// https://learn.microsoft.com/en-us/windows/win32/api/propsys/nn-propsys-ipropertystore
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    // Every value must be released with PropVariantClear: use the PropVariantInterop.Read* helpers.
    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

// Implemented by Earshot and handed to RegisterEndpointNotificationCallback. Callbacks arrive on an
// undocumented thread, must not block or wait, must not Register/Unregister, and must not release the
// last reference to an MMDevice object. Device id strings are valid only during the call. Return
// values are ignored by MMDevAPI; return 0 (S_OK).
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    // pwstrDefaultDeviceId may be null when no default device exists for the flow and role.
    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);

    // key is passed by value (const PROPERTYKEY, 20 bytes).
    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
}
