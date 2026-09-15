namespace Earshot.Contracts;

// Single decode table for HRESULT / CONFIGRET / Win32, including raw SetupAPI codes that are
// NOT HRESULT_FROM_WIN32. Used to build every StepOutcome.CodeName. Never throw on these.
//
// Small values are shared between CONFIGRET and raw Win32 codes, so each value has one name,
// chosen for the API that returns it raw in Earshot (CfgMgr32 for the CR_ values, Bluetooth and
// kernel32 for the ERROR_ values). Sources:
// HRESULTs     https://learn.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values
// CONFIGRET    https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
// Win32        https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
// Scheduler    https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-error-and-success-constants
public static class NativeCodes
{
    public static string Name(int code) => unchecked((uint)code) switch
    {
        0x00000000 => "S_OK",
        0x80070490 => "E_NOTFOUND",
        0x80004002 => "E_NOINTERFACE",
        0x88890004 => "AUDCLNT_E_DEVICE_INVALIDATED",
        0x80070002 => "ERROR_FILE_NOT_FOUND",
        0x80070003 => "ERROR_PATH_NOT_FOUND",
        0x80070057 => "E_INVALIDARG",            // BluetoothSetServiceState: already-in-state
        0xE0000225 => "ERROR_NO_SUCH_DEVICE_INTERFACE",  // GetDevice(adapterId): KS filter gone
        0xE000020B => "ERROR_NO_SUCH_DEVINST",           // IPropertyStore FriendlyName on NOTPRESENT
        0x0000000D => "CR_NO_SUCH_DEVNODE",
        0x00000017 => "CR_REMOVE_VETOED",
        0x00000028 => "CR_NOT_DISABLEABLE",
        0x00000033 => "CR_ACCESS_DENIED",
        0x00000025 => "CR_NO_SUCH_VALUE",
        0x00000005 => "ERROR_ACCESS_DENIED",
        0x00000424 => "ERROR_SERVICE_DOES_NOT_EXIST", // 1060

        // Common COM HRESULTs.
        0x80004001 => "E_NOTIMPL",
        0x80004003 => "E_POINTER",
        0x80004004 => "E_ABORT",
        0x80004005 => "E_FAIL",
        0x8000FFFF => "E_UNEXPECTED",
        0x80070005 => "E_ACCESSDENIED",
        0x80070006 => "E_HANDLE",
        0x8007000E => "E_OUTOFMEMORY",
        0x800700B7 => "ERROR_ALREADY_EXISTS",   // ITaskFolder.CreateFolder: folder exists

        // CONFIGRET returned raw by CfgMgr32.
        0x00000013 => "CR_FAILURE",
        0x0000001A => "CR_BUFFER_SMALL",        // list or property changed size between calls
        0x0000001E => "CR_INVALID_DEVICE_ID",
        0x00000022 => "CR_NEED_RESTART",
        0x00000024 => "CR_DEVICE_NOT_THERE",
        0x00000035 => "CR_INVALID_PROPERTY",

        // Win32 codes returned raw by the Bluetooth APIs.
        0x00000057 => "ERROR_INVALID_PARAMETER",  // 87
        0x000000EA => "ERROR_MORE_DATA",          // 234
        0x00000103 => "ERROR_NO_MORE_ITEMS",      // 259
        0x00000490 => "ERROR_NOT_FOUND",          // 1168
        0x0000051A => "ERROR_REVISION_MISMATCH",  // 1306

        // Task Scheduler.
        0x00041300 => "SCHED_S_TASK_READY",
        0x00041301 => "SCHED_S_TASK_RUNNING",
        0x00041303 => "SCHED_S_TASK_HAS_NOT_RUN",
        0x00041306 => "SCHED_S_TASK_TERMINATED",
        0x0004131C => "SCHED_S_BATCH_LOGON_PROBLEM",
        0x00041325 => "SCHED_S_TASK_QUEUED",
        0x80041315 => "SCHED_E_SERVICE_NOT_RUNNING",
        0x8004131F => "SCHED_E_ALREADY_RUNNING",
        0x80041320 => "SCHED_E_USER_NOT_LOGGED_ON",
        0x80041324 => "SCHED_E_TASK_ATTEMPTED",
        0x80041326 => "SCHED_E_TASK_DISABLED",
        0x80041328 => "SCHED_E_START_ON_DEMAND",

        _ => "0x" + unchecked((uint)code).ToString("X8")
    };
}
