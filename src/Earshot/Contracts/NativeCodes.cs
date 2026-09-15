namespace Earshot.Contracts;

// Single decode table for HRESULT / CONFIGRET / Win32, including raw SetupAPI codes that are
// NOT HRESULT_FROM_WIN32. Used to build every StepOutcome.CodeName. Never throw on these.
//
// Three decoders, one per family, because small values mean different things in each:
//   Name(int)         an HRESULT (or a code of unknown family). Small values keep one name each,
//                     for the table below, so it cannot tell a CONFIGRET from a Win32 code.
//   ConfigRet(uint)   a CONFIGRET returned by a CM_* function. 0x5 is CR_INVALID_DEVNODE here.
//   Win32(uint)       a Win32 error returned directly or through GetLastError, including the
//                     Bluetooth APIs. 0x5 is ERROR_ACCESS_DENIED and 0xD is ERROR_INVALID_DATA here.
// Build a StepOutcome for a CM_* call with ConfigRet and for a Win32 or Bluetooth call with Win32
// (StepOutcomes does both), never with Name.
// Sources:
// HRESULTs     https://learn.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values
// HRESULT bits https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/0642cb2f-2075-4469-918c-4441e69c548a
// CONFIGRET    https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
// Win32        https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
// Scheduler    https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-error-and-success-constants
// CoCreate     https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstance
public static class NativeCodes
{
    // Earshot's own codes for a step that made no native call. The C bit (0x20000000) marks an HRESULT
    // as customer-defined, so no Windows code has these values, and the severity bit makes them failures:
    // a check for Code == 0 or Code >= 0 never reads a refusal as a success.
    public const int NotAttempted = unchecked((int)0xA0000001);   // refused on purpose, for example safe mode
    public const int NotAvailable = unchecked((int)0xA0000002);   // this build has no implementation

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

        // Earshot's own no-native-call codes.
        0xA0000001 => "NOT_ATTEMPTED",
        0xA0000002 => "NOT_AVAILABLE",

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
        0x80040110 => "CLASS_E_NOAGGREGATION",  // CoCreateInstance
        0x80040154 => "REGDB_E_CLASSNOTREG",    // CoCreateInstance: class not registered
        0x800401F0 => "CO_E_NOTINITIALIZED",    // COM not initialised on the calling thread

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

        _ => Hex(unchecked((uint)code))
    };

    // A CONFIGRET from a CM_* function: the whole CR_* range of cfgmgr32.h (0x00 to 0x3B). Aliases
    // (CR_INVALID_DEVINST, CR_NO_SUCH_DEVINST, CR_DEVINST_HAS_REQS, CR_ALREADY_SUCH_DEVINST) share a
    // value with the name listed. Anything else is shown as hex.
    // https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
    public static string ConfigRet(uint cr) => cr switch
    {
        0x00 => "CR_SUCCESS",
        0x01 => "CR_DEFAULT",
        0x02 => "CR_OUT_OF_MEMORY",
        0x03 => "CR_INVALID_POINTER",
        0x04 => "CR_INVALID_FLAG",
        0x05 => "CR_INVALID_DEVNODE",
        0x06 => "CR_INVALID_RES_DES",
        0x07 => "CR_INVALID_LOG_CONF",
        0x08 => "CR_INVALID_ARBITRATOR",
        0x09 => "CR_INVALID_NODELIST",
        0x0A => "CR_DEVNODE_HAS_REQS",
        0x0B => "CR_INVALID_RESOURCEID",
        0x0C => "CR_DLVXD_NOT_FOUND",
        0x0D => "CR_NO_SUCH_DEVNODE",
        0x0E => "CR_NO_MORE_LOG_CONF",
        0x0F => "CR_NO_MORE_RES_DES",
        0x10 => "CR_ALREADY_SUCH_DEVNODE",
        0x11 => "CR_INVALID_RANGE_LIST",
        0x12 => "CR_INVALID_RANGE",
        0x13 => "CR_FAILURE",
        0x14 => "CR_NO_SUCH_LOGICAL_DEV",
        0x15 => "CR_CREATE_BLOCKED",
        0x16 => "CR_NOT_SYSTEM_VM",
        0x17 => "CR_REMOVE_VETOED",
        0x18 => "CR_APM_VETOED",
        0x19 => "CR_INVALID_LOAD_TYPE",
        0x1A => "CR_BUFFER_SMALL",
        0x1B => "CR_NO_ARBITRATOR",
        0x1C => "CR_NO_REGISTRY_HANDLE",
        0x1D => "CR_REGISTRY_ERROR",
        0x1E => "CR_INVALID_DEVICE_ID",
        0x1F => "CR_INVALID_DATA",
        0x20 => "CR_INVALID_API",
        0x21 => "CR_DEVLOADER_NOT_READY",
        0x22 => "CR_NEED_RESTART",
        0x23 => "CR_NO_MORE_HW_PROFILES",
        0x24 => "CR_DEVICE_NOT_THERE",
        0x25 => "CR_NO_SUCH_VALUE",
        0x26 => "CR_WRONG_TYPE",
        0x27 => "CR_INVALID_PRIORITY",
        0x28 => "CR_NOT_DISABLEABLE",
        0x29 => "CR_FREE_RESOURCES",
        0x2A => "CR_QUERY_VETOED",
        0x2B => "CR_CANT_SHARE_IRQ",
        0x2C => "CR_NO_DEPENDENT",
        0x2D => "CR_SAME_RESOURCES",
        0x2E => "CR_NO_SUCH_REGISTRY_KEY",
        0x2F => "CR_INVALID_MACHINENAME",
        0x30 => "CR_REMOTE_COMM_FAILURE",
        0x31 => "CR_MACHINE_UNAVAILABLE",
        0x32 => "CR_NO_CM_SERVICES",
        0x33 => "CR_ACCESS_DENIED",
        0x34 => "CR_CALL_NOT_IMPLEMENTED",
        0x35 => "CR_INVALID_PROPERTY",
        0x36 => "CR_DEVICE_INTERFACE_ACTIVE",
        0x37 => "CR_NO_SUCH_DEVICE_INTERFACE",
        0x38 => "CR_INVALID_REFERENCE_STRING",
        0x39 => "CR_INVALID_CONFLICT_LIST",
        0x3A => "CR_INVALID_INDEX",
        0x3B => "CR_INVALID_STRUCTURE_SIZE",
        _ => Hex(cr)
    };

    // A Win32 error code (winerror.h) returned directly or read with GetLastError. A value with the
    // severity bit set is an HRESULT carried in a DWORD (BluetoothSetServiceState returns E_INVALIDARG
    // that way), so it is named by Name. Anything else is shown as hex.
    // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
    public static string Win32(uint error) => error switch
    {
        0 => "ERROR_SUCCESS",
        1 => "ERROR_INVALID_FUNCTION",
        2 => "ERROR_FILE_NOT_FOUND",
        3 => "ERROR_PATH_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED",
        6 => "ERROR_INVALID_HANDLE",
        8 => "ERROR_NOT_ENOUGH_MEMORY",
        13 => "ERROR_INVALID_DATA",
        21 => "ERROR_NOT_READY",
        24 => "ERROR_BAD_LENGTH",
        31 => "ERROR_GEN_FAILURE",
        32 => "ERROR_SHARING_VIOLATION",
        50 => "ERROR_NOT_SUPPORTED",
        87 => "ERROR_INVALID_PARAMETER",
        122 => "ERROR_INSUFFICIENT_BUFFER",
        170 => "ERROR_BUSY",
        183 => "ERROR_ALREADY_EXISTS",
        234 => "ERROR_MORE_DATA",
        259 => "ERROR_NO_MORE_ITEMS",
        740 => "ERROR_ELEVATION_REQUIRED",
        1004 => "ERROR_INVALID_FLAGS",
        1060 => "ERROR_SERVICE_DOES_NOT_EXIST",
        1167 => "ERROR_DEVICE_NOT_CONNECTED",
        1168 => "ERROR_NOT_FOUND",
        1223 => "ERROR_CANCELLED",
        1306 => "ERROR_REVISION_MISMATCH",
        1460 => "ERROR_TIMEOUT",
        >= 0x80000000 => Name(unchecked((int)error)),
        _ => Hex(error)
    };

    private static string Hex(uint code) => "0x" + code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
