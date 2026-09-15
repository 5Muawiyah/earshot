namespace Earshot.Contracts;

// Builds StepOutcomes so each native code is decoded by the table for its own family. A CONFIGRET
// and a Win32 code can share a value (0x5 is CR_INVALID_DEVNODE and ERROR_ACCESS_DENIED), so the
// caller states which API the code came from. See NativeCodes.
public static class StepOutcomes
{
    // An HRESULT. Ok defaults to SUCCEEDED(hr), that is hr >= 0.
    public static StepOutcome FromHResult(string step, int hr, string? detail = null, bool? ok = null) =>
        new(step, ok ?? hr >= 0, hr, NativeCodes.Name(hr), detail);

    // A CONFIGRET from a CM_* function. Ok defaults to CR_SUCCESS.
    public static StepOutcome FromConfigRet(string step, uint cr, string? detail = null, bool? ok = null) =>
        new(step, ok ?? cr == 0, unchecked((int)cr), NativeCodes.ConfigRet(cr), detail);

    // A Win32 error returned directly (the Bluetooth APIs) or read with GetLastError. Ok defaults to
    // ERROR_SUCCESS; pass ok for codes an API documents as success, such as ERROR_MORE_DATA from
    // BluetoothEnumerateInstalledServices.
    public static StepOutcome FromWin32(string step, uint error, string? detail = null, bool? ok = null) =>
        new(step, ok ?? error == 0, unchecked((int)error), NativeCodes.Win32(error), detail);

    // A step that was refused on purpose and made no native call.
    public static StepOutcome NotAttempted(string step, string detail) =>
        new(step, Ok: false, NativeCodes.NotAttempted, NativeCodes.Name(NativeCodes.NotAttempted), detail);

    // A step this build cannot perform and that made no native call.
    public static StepOutcome NotAvailable(string step, string detail) =>
        new(step, Ok: false, NativeCodes.NotAvailable, NativeCodes.Name(NativeCodes.NotAvailable), detail);
}
