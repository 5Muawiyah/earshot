using System.Globalization;
using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Bluetooth Classic APIs (bluetoothapis.h).
//
// DLL: the documented DLL is bthprops.cpl, but on this Windows build every one of its exports is a
// forwarder to the ext-ms-win-bluetooth-apis API set, while System32\BluetoothApis.dll exports the same
// functions directly. The research probe ran these declarations against BluetoothApis.dll on
// net10.0-windows x64. The page metadata lists both DLLs.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
//
// Struct sizes are x64 (the project is x64 only). dwSize must be set from sizeof, never a literal:
// a wrong size fails with ERROR_REVISION_MISMATCH.
internal static unsafe partial class BluetoothApis
{
    private const string Dll = "BluetoothApis.dll";

    // BLUETOOTH_MAX_NAME_SIZE (bluetoothapis.h).
    internal const int BLUETOOTH_MAX_NAME_SIZE = 248;

    // BluetoothSetServiceState dwServiceFlags.
    internal const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
    internal const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    // Win32 codes these functions return (winerror.h). BluetoothSetServiceState and
    // BluetoothEnumerateInstalledServices return them directly; the find functions set last error.
    // Decode them with NativeCodes.Win32 (StepOutcomes.FromWin32), never NativeCodes.Name: 5 is also
    // the CONFIGRET CR_INVALID_DEVNODE.
    // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
    internal const uint ERROR_SUCCESS = 0;
    internal const uint ERROR_ACCESS_DENIED = 5;
    internal const uint ERROR_INVALID_PARAMETER = 87;
    internal const uint ERROR_MORE_DATA = 234;
    internal const uint ERROR_NO_MORE_ITEMS = 259;
    internal const uint ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const uint ERROR_NOT_FOUND = 1168;
    internal const uint ERROR_REVISION_MISMATCH = 1306;

    // BluetoothSetServiceState returns this HRESULT inside its DWORD when the service is already in the
    // requested state. Compare as uint. It is not an error.
    internal const uint E_INVALIDARG = 0x80070057;

    // Service class GUIDs (Bluetooth base UUID 0000xxxx-0000-1000-8000-00805F9B34FB).
    internal static readonly Guid HandsfreeServiceClass = new("0000111E-0000-1000-8000-00805F9B34FB");
    internal static readonly Guid HeadsetServiceClass = new("00001108-0000-1000-8000-00805F9B34FB");
    internal static readonly Guid AudioSinkServiceClass = new("0000110B-0000-1000-8000-00805F9B34FB");

    // HBLUETOOTH_RADIO_FIND. phRadio receives a radio handle to close with CloseHandle; close the find
    // handle with BluetoothFindRadioClose. NULL with ERROR_NO_MORE_ITEMS when there is no radio.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindfirstradio
    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint BluetoothFindFirstRadio(BLUETOOTH_FIND_RADIO_PARAMS* pbtfrp, nint* phRadio);

    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindnextradio
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BluetoothFindNextRadio(nint hFind, nint* phRadio);

    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindradioclose
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BluetoothFindRadioClose(nint hFind);

    // Classic devices only (no Bluetooth LE). Never set fIssueInquiry: an inquiry pages nearby devices.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindfirstdevice
    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint BluetoothFindFirstDevice(BLUETOOTH_DEVICE_SEARCH_PARAMS* pbtsp, BLUETOOTH_DEVICE_INFO* pbtdi);

    // FALSE with ERROR_NO_MORE_ITEMS at the end of the enumeration.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindnextdevice
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BluetoothFindNextDevice(nint hFind, BLUETOOTH_DEVICE_INFO* pbtdi);

    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfinddeviceclose
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BluetoothFindDeviceClose(nint hFind);

    // Needs dwSize and Address set. Returns the error code directly.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothgetdeviceinfo
    [LibraryImport(Dll)]
    internal static partial uint BluetoothGetDeviceInfo(nint hRadio, BLUETOOTH_DEVICE_INFO* pbtdi);

    // Lists the ENABLED service GUIDs of a device. hRadio 0 searches all radios. ERROR_MORE_DATA (234)
    // means success with an incomplete list, and a size query with a null buffer returns 234, not 0.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothenumerateinstalledservices
    [LibraryImport(Dll)]
    internal static partial uint BluetoothEnumerateInstalledServices(nint hRadio, BLUETOOTH_DEVICE_INFO* pbtdi, uint* pcServiceInout, Guid* pGuidServices);

    // Declaration only: runs inside the SYSTEM gate, never in the tray. Installs or removes the service's
    // driver, blocks for an undocumented time, and has no documented privilege requirement. Returns the
    // error code directly: 0 changed, E_INVALIDARG (0x80070057) already in state, 1060 not supported,
    // 87 bad flags.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate
    [LibraryImport(Dll)]
    internal static partial uint BluetoothSetServiceState(nint hRadio, BLUETOOTH_DEVICE_INFO* pbtdi, Guid* pGuidService, uint dwServiceFlags);

    // Counts local radios, closing every handle it opens. Returns 0 when the enumeration ran to its end
    // (including no radio at all), otherwise the Win32 error that stopped it. closeError is the first
    // error from closing a handle, or 0.
    internal static uint CountRadios(out int radios, out uint closeError)
    {
        radios = 0;
        closeError = ERROR_SUCCESS;
        var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)sizeof(BLUETOOTH_FIND_RADIO_PARAMS) };
        nint radio = 0;
        nint find = BluetoothFindFirstRadio(&findParams, &radio);
        if (find == 0)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            return error == ERROR_NO_MORE_ITEMS ? ERROR_SUCCESS : error;
        }

        uint result = ERROR_SUCCESS;
        try
        {
            do
            {
                radios++;
                if (!NativeMethods.CloseHandle(radio) && closeError == ERROR_SUCCESS)
                {
                    closeError = (uint)Marshal.GetLastPInvokeError();
                }

                radio = 0;
            }
            while (BluetoothFindNextRadio(find, &radio));

            uint last = (uint)Marshal.GetLastPInvokeError();
            if (last != ERROR_NO_MORE_ITEMS)
            {
                result = last;
            }
        }
        finally
        {
            if (!BluetoothFindRadioClose(find) && closeError == ERROR_SUCCESS)
            {
                closeError = (uint)Marshal.GetLastPInvokeError();
            }
        }

        return result;
    }

    // Opens the first local radio. Returns 0 with both handles set (close them with CloseRadio), ERROR_NO_MORE_ITEMS
    // when there is no radio, or the Win32 error from BluetoothFindFirstRadio; the handles are 0 then.
    // https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindfirstradio
    internal static uint OpenFirstRadio(out nint find, out nint radio)
    {
        var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)sizeof(BLUETOOTH_FIND_RADIO_PARAMS) };
        nint handle = 0;
        find = BluetoothFindFirstRadio(&findParams, &handle);
        if (find == 0)
        {
            radio = 0;
            return (uint)Marshal.GetLastPInvokeError();
        }

        radio = handle;
        return ERROR_SUCCESS;
    }

    // Closes what OpenFirstRadio opened. Returns the first close error, or 0.
    internal static uint CloseRadio(nint find, nint radio)
    {
        uint closeError = ERROR_SUCCESS;
        if (radio != 0 && !NativeMethods.CloseHandle(radio))
        {
            closeError = (uint)Marshal.GetLastPInvokeError();
        }

        if (find != 0 && !BluetoothFindRadioClose(find) && closeError == ERROR_SUCCESS)
        {
            closeError = (uint)Marshal.GetLastPInvokeError();
        }

        return closeError;
    }

    // Enumerates remembered, authenticated and connected Classic devices from the local cache. It never
    // issues an inquiry (fIssueInquiry = 0), so no device is paged. Returns 0 when the enumeration ran
    // to its end (including no device), otherwise the Win32 error that stopped it; devices holds what
    // was found before the error. closeError is the error from closing the find handle, or 0.
    internal static uint FindPairedDevices(out List<BLUETOOTH_DEVICE_INFO> devices, out uint closeError)
    {
        devices = new List<BLUETOOTH_DEVICE_INFO>();
        closeError = ERROR_SUCCESS;
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)sizeof(BLUETOOTH_DEVICE_SEARCH_PARAMS),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnUnknown = 0,
            fReturnConnected = 1,
            fIssueInquiry = 0,
            cTimeoutMultiplier = 0,
            hRadio = 0,
        };
        BLUETOOTH_DEVICE_INFO info = NewDeviceInfo();
        nint find = BluetoothFindFirstDevice(&search, &info);
        if (find == 0)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            return error == ERROR_NO_MORE_ITEMS ? ERROR_SUCCESS : error;
        }

        uint result = ERROR_SUCCESS;
        try
        {
            do
            {
                devices.Add(info);
                info = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(find, &info));

            uint last = (uint)Marshal.GetLastPInvokeError();
            if (last != ERROR_NO_MORE_ITEMS)
            {
                result = last;
            }
        }
        finally
        {
            if (!BluetoothFindDeviceClose(find))
            {
                closeError = (uint)Marshal.GetLastPInvokeError();
            }
        }

        return result;
    }

    // Reads the enabled service GUIDs of a device found by FindPairedDevices. Returns 0 for a complete
    // list, ERROR_MORE_DATA when the list is still incomplete after the retries (services holds what came
    // back), or another Win32 error with an empty list.
    internal static uint GetInstalledServices(BLUETOOTH_DEVICE_INFO device, out Guid[] services)
    {
        services = [];
        uint count = 0;
        uint rc = BluetoothEnumerateInstalledServices(0, &device, &count, null);
        if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA)
        {
            return rc;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new Guid[Math.Max(count, 1u)];
            uint inout = (uint)buffer.Length;
            fixed (Guid* pointer = buffer)
            {
                rc = BluetoothEnumerateInstalledServices(0, &device, &inout, pointer);
            }

            if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA)
            {
                return rc;
            }

            services = buffer.AsSpan(0, (int)Math.Min(inout, (uint)buffer.Length)).ToArray();
            if (rc == ERROR_SUCCESS || inout <= (uint)buffer.Length)
            {
                return rc;
            }

            count = inout;
        }

        return rc;
    }

    // A BLUETOOTH_DEVICE_INFO with dwSize set, ready for the find and get functions.
    internal static BLUETOOTH_DEVICE_INFO NewDeviceInfo() =>
        new() { dwSize = (uint)sizeof(BLUETOOTH_DEVICE_INFO) };

    // The device name from szName, up to the first NUL.
    internal static string GetName(in BLUETOOTH_DEVICE_INFO device)
    {
        fixed (char* name = device.szName)
        {
            var span = new ReadOnlySpan<char>(name, BLUETOOTH_MAX_NAME_SIZE);
            int end = span.IndexOf('\0');
            return (end < 0 ? span : span[..end]).ToString();
        }
    }

    // The 48-bit address as 12 uppercase hex digits, the form used in BTHENUM instance ids
    // (for example 5A6B7C8D9EAF).
    internal static string FormatAddress12(ulong address) =>
        (address & 0xFFFF_FFFF_FFFFUL).ToString("X12", CultureInfo.InvariantCulture);
}

// SYSTEMTIME, 16 bytes.
// https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-systemtime
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEMTIME
{
    public ushort wYear;
    public ushort wMonth;
    public ushort wDayOfWeek;
    public ushort wDay;
    public ushort wHour;
    public ushort wMinute;
    public ushort wSecond;
    public ushort wMilliseconds;
}

// BLUETOOTH_DEVICE_INFO, 560 bytes on x64. Address is the BLUETOOTH_ADDRESS union read as its ULONGLONG
// member (8 bytes, 8-byte aligned). The BOOL fields are not 0/1 on this machine (connected read 32):
// test them with != 0, never == 1.
// Offsets: dwSize 0, Address 8, ulClassofDevice 16, fConnected 20, fRemembered 24, fAuthenticated 28,
// stLastSeen 32, stLastUsed 48, szName 64.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_info_struct
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct BLUETOOTH_DEVICE_INFO
{
    public uint dwSize;
    public ulong Address;
    public uint ulClassofDevice;
    public int fConnected;
    public int fRemembered;
    public int fAuthenticated;
    public SYSTEMTIME stLastSeen;
    public SYSTEMTIME stLastUsed;
    public fixed char szName[BluetoothApis.BLUETOOTH_MAX_NAME_SIZE];
}

// BLUETOOTH_DEVICE_SEARCH_PARAMS, 40 bytes on x64: cTimeoutMultiplier at 24, seven bytes of padding,
// hRadio at 32. hRadio 0 searches all radios.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_search_params
[StructLayout(LayoutKind.Sequential)]
internal struct BLUETOOTH_DEVICE_SEARCH_PARAMS
{
    public uint dwSize;
    public int fReturnAuthenticated;
    public int fReturnRemembered;
    public int fReturnUnknown;
    public int fReturnConnected;
    public int fIssueInquiry;
    public byte cTimeoutMultiplier;
    public nint hRadio;
}

// BLUETOOTH_FIND_RADIO_PARAMS, 4 bytes.
// https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_find_radio_params
[StructLayout(LayoutKind.Sequential)]
internal struct BLUETOOTH_FIND_RADIO_PARAMS
{
    public uint dwSize;
}
