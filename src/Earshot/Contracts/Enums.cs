namespace Earshot.Contracts;

public enum EndpointFlow { Render, Capture }

// Mirrors DEVICE_STATE_*: https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
[Flags]
public enum EndpointState { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8 }

public enum ConnectionState { Unknown, Disconnected, Connecting, Connected, Disconnecting }

// Task requirement: allowed/blocked/mixed/unknown/not-found/not-set-up.
public enum BlockState
{
    Allowed,     // every present target BTHENUM node enabled
    Blocked,     // every present target node persistently disabled (problem 22 / CONFIGFLAG_DISABLED)
    Mixed,       // some disabled, some enabled
    Unknown,     // nodes not present, status unreadable (radio off, or never connected to this PC)
    NotFound,    // no node matched the pinned device (renamed + wrong match string, or unpaired)
    NotSetUp     // the elevated \Earshot\Gate task is missing; blocking cannot be requested
}

public enum NodeBlockStatus { Enabled, Disabled, Unknown }

public enum AudioProtectionState { Unknown, Protected, NotProtected, Partial }

public enum OpStatus { Success, AlreadyInState, Partial, Failed, NotAttempted }

public enum ConnectOutcome
{
    Confirmed,          // render endpoint reached the target state within the timeout
    AttemptedTimedOut,  // property/enable issued, no confirming state change in time
    NoFiltersResponded, // every KS filter returned a failure code
    NodesBlocked,       // nodes NOTPRESENT because blocked; handled by Allow-first
    NotFound,           // no container matched the device
    Failed
}

public enum LogLevel { Debug, Info, Warn, Error }
