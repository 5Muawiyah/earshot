using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Interop;

// A handle from OpenSCManagerW, OpenServiceW or CreateServiceW. Only CloseServiceHandle closes it.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-closeservicehandle
internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => AdvApi32.CloseServiceHandle(handle);
}

// The service control manager calls behind the hand-back service: the process side (the dispatcher, the control
// handler and the status report) and the administrator side (create, configure, query, start, stop and delete).
// Declared here, with the rest of the native surface, and reached only through IServiceHost and IServiceControl so
// every caller can be tested against a fake.
internal static unsafe partial class AdvApi32
{
    private const string Advapi32 = "advapi32.dll";

    // Service types, start types and error control.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicew
    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    internal const uint SERVICE_AUTO_START = 0x00000002;
    internal const uint SERVICE_DEMAND_START = 0x00000003;
    internal const uint SERVICE_ERROR_NORMAL = 0x00000001;

    // Left as it is by ChangeServiceConfigW.
    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    // Service control manager access rights. Only an administrator can create a service.
    // https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights
    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;

    // Service access rights, and the standard rights.
    // https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights
    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint SERVICE_PAUSE_CONTINUE = 0x0040;
    internal const uint SERVICE_INTERROGATE = 0x0080;
    internal const uint SERVICE_USER_DEFINED_CONTROL = 0x0100;
    internal const uint SERVICE_ALL_ACCESS = 0x000F01FF;
    internal const uint DELETE = 0x00010000;
    internal const uint READ_CONTROL = 0x00020000;
    internal const uint WRITE_DAC = 0x00040000;
    internal const uint WRITE_OWNER = 0x00080000;

    // What an authenticated user holds on the service: READ_CONTROL, SERVICE_QUERY_CONFIG, SERVICE_QUERY_STATUS and
    // SERVICE_ENUMERATE_DEPENDENTS.
    internal const uint SERVICE_QUERY_ONLY = READ_CONTROL | SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS;

    // Control codes. Only stop, interrogate and preshutdown are ever acted on or answered.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nc-winsvc-lphandler_function_ex
    internal const uint SERVICE_CONTROL_STOP = 0x00000001;
    internal const uint SERVICE_CONTROL_PAUSE = 0x00000002;
    internal const uint SERVICE_CONTROL_CONTINUE = 0x00000003;
    internal const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    internal const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    internal const uint SERVICE_CONTROL_POWEREVENT = 0x0000000D;
    internal const uint SERVICE_CONTROL_SESSIONCHANGE = 0x0000000E;
    internal const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000000F;

    // Service states.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status
    internal const uint SERVICE_STOPPED = 1;
    internal const uint SERVICE_START_PENDING = 2;
    internal const uint SERVICE_STOP_PENDING = 3;
    internal const uint SERVICE_RUNNING = 4;

    // Controls a service says it accepts. Only the system can send SERVICE_ACCEPT_PRESHUTDOWN.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status
    internal const uint SERVICE_ACCEPT_STOP = 0x00000001;
    internal const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    internal const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x00000100;

    // ChangeServiceConfig2W and QueryServiceConfig2W levels.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w
    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;
    internal const uint SERVICE_CONFIG_PRESHUTDOWN_INFO = 7;

    // QueryServiceStatusEx level.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex
    internal const uint SC_STATUS_PROCESS_INFO = 0;

    // The part of the security descriptor the service's access list lives in.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setserviceobjectsecurity
    internal const uint DACL_SECURITY_INFORMATION = 0x00000004;

    // Win32 errors these calls report. Values from the system error codes pages.
    // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-
    // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-
    internal const uint ERROR_CALL_NOT_IMPLEMENTED = 120;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;
    internal const uint ERROR_MORE_DATA = 234;
    internal const uint ERROR_SERVICE_REQUEST_TIMEOUT = 1053;
    internal const uint ERROR_SERVICE_ALREADY_RUNNING = 1056;
    internal const uint ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const uint ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    internal const uint ERROR_SERVICE_NOT_ACTIVE = 1062;
    internal const uint ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;
    internal const uint ERROR_SERVICE_SPECIFIC_ERROR = 1066;
    internal const uint ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    internal const uint ERROR_SERVICE_EXISTS = 1073;

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openscmanagerw
    [LibraryImport(Advapi32, EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeServiceHandle OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openservicew
    [LibraryImport(Advapi32, EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeServiceHandle OpenService(SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicew
    [LibraryImport(Advapi32, EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeServiceHandle CreateService(
        SafeServiceHandle hSCManager, string lpServiceName, string? lpDisplayName, uint dwDesiredAccess, uint dwServiceType,
        uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string? lpLoadOrderGroup, nint lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw
    [LibraryImport(Advapi32, EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig(
        SafeServiceHandle hService, uint dwServiceType, uint dwStartType, uint dwErrorControl, string? lpBinaryPathName,
        string? lpLoadOrderGroup, nint lpdwTagId, string? lpDependencies, string? lpServiceStartName, string? lpPassword,
        string? lpDisplayName);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w
    [LibraryImport(Advapi32, EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2(SafeServiceHandle hService, uint dwInfoLevel, void* lpInfo);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw
    [LibraryImport(Advapi32, EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig(SafeServiceHandle hService, byte* lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfig2w
    [LibraryImport(Advapi32, EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig2(SafeServiceHandle hService, uint dwInfoLevel, byte* lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex
    [LibraryImport(Advapi32, EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatusEx(SafeServiceHandle hService, uint infoLevel, byte* lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceobjectsecurity
    [LibraryImport(Advapi32, EntryPoint = "QueryServiceObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceObjectSecurity(
        SafeServiceHandle hService, uint dwSecurityInformation, byte* lpSecurityDescriptor, uint cbBufSize, out uint pcbBytesNeeded);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setserviceobjectsecurity
    [LibraryImport(Advapi32, EntryPoint = "SetServiceObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetServiceObjectSecurity(SafeServiceHandle hService, uint dwSecurityInformation, byte* lpSecurityDescriptor);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew
    [LibraryImport(Advapi32, EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartService(SafeServiceHandle hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice
    [LibraryImport(Advapi32, EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(SafeServiceHandle hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-deleteservice
    [LibraryImport(Advapi32, EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(SafeServiceHandle hService);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-closeservicehandle
    [LibraryImport(Advapi32, EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint hSCObject);

    // Does not return until every service in the process has stopped. Fails with
    // ERROR_FAILED_SERVICE_CONTROLLER_CONNECT when the process was not started by the service control manager.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw
    [LibraryImport(Advapi32, EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRYW* lpServiceStartTable);

    // handler is a DWORD WINAPI (DWORD dwControl, DWORD dwEventType, LPVOID lpEventData, LPVOID lpContext).
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-registerservicectrlhandlerexw
    [LibraryImport(Advapi32, EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint RegisterServiceCtrlHandlerEx(string lpServiceName, delegate* unmanaged<uint, uint, nint, nint, uint> lpHandlerProc, nint lpContext);

    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setservicestatus
    [LibraryImport(Advapi32, EntryPoint = "SetServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetServiceStatus(nint hServiceStatus, in SERVICE_STATUS lpServiceStatus);
}

// SERVICE_STATUS, seven DWORDs.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

// SERVICE_STATUS_PROCESS: SERVICE_STATUS, then the process id and the service flags.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS_PROCESS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
    public uint dwProcessId;
    public uint dwServiceFlags;
}

// SERVICE_TABLE_ENTRYW: the name and the ServiceMain function pointer. A table ends with a zeroed entry.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_table_entryw
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SERVICE_TABLE_ENTRYW
{
    public char* lpServiceName;
    public delegate* unmanaged<uint, char**, void> lpServiceProc;
}

// QUERY_SERVICE_CONFIGW: three DWORDs, then pointers into the same buffer, with the tag id between them.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-query_service_configw
[StructLayout(LayoutKind.Sequential)]
internal struct QUERY_SERVICE_CONFIGW
{
    public uint dwServiceType;
    public uint dwStartType;
    public uint dwErrorControl;
    public nint lpBinaryPathName;
    public nint lpLoadOrderGroup;
    public uint dwTagId;
    public nint lpDependencies;
    public nint lpServiceStartName;
    public nint lpDisplayName;
}

// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_descriptionw
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_DESCRIPTIONW
{
    public nint lpDescription;
}

// Milliseconds.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_preshutdown_info
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_PRESHUTDOWN_INFO
{
    public uint dwPreshutdownTimeout;
}
