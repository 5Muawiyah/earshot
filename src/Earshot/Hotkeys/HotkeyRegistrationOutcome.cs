namespace Earshot.Hotkeys;

// One action's result from HotkeyManager.Apply. This is a separate family from
// Earshot.Contracts.ControllerResult: that type carries one outcome for one operation, and Apply always
// produces exactly four, one per HotkeyAction, so folding them together would force a fake list of
// steps onto a shape that already has its own per-action state. ErrorCode is 0 unless Windows gave a
// code, and is the raw code, not a name: the fixed "RegisterHotKey ... error=<code>" / "UnregisterHotKey
// ... error=<code>" log lines this feature writes (HotkeyManager.LogRegister/LogUnregister) are a spec-
// defined shape a test pins exactly, so the code is not run through Earshot.Contracts.NativeCodes there.
// Only 1409 is ever named specially, as AlreadyHeld, because it is the one code the platform documents as
// meaning "something else holds this combination" (2.4 in the spec); every other code is a bare number in
// both the log line and the Message text.
public sealed record HotkeyRegistrationOutcome(
    HotkeyAction Action,
    string RequestedText,
    HotkeyRegistrationState State,
    int ErrorCode,
    string Message);
