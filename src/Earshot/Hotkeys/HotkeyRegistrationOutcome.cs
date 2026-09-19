namespace Earshot.Hotkeys;

// One action's result from HotkeyManager.Apply. This is a separate family from
// Earshot.Contracts.ControllerResult: that type carries one outcome for one operation, and Apply always
// produces exactly four, one per HotkeyAction, so folding them together would force a fake list of
// steps onto a shape that already has its own per-action state. ErrorCode is 0 unless Windows gave a
// code; the code itself is decoded through Earshot.Contracts.NativeCodes.Win32 wherever it is logged as
// a native-call line, so there is still only one table that turns a Win32 error into a name.
public sealed record HotkeyRegistrationOutcome(
    HotkeyAction Action,
    string RequestedText,
    HotkeyRegistrationState State,
    int ErrorCode,
    string Message);
