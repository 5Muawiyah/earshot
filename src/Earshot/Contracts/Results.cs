namespace Earshot.Contracts;

// One native step and how it went. Every HRESULT / CONFIGRET / Win32 code is recorded
// with a decoded name, so no failure is silent and Marshal.ThrowExceptionForHR is never
// used on raw SetupAPI codes (0xE0000225, 0xE000020B are not HRESULT_FROM_WIN32).
public sealed record StepOutcome(
    string Step,        // "activate-topology", "ks-reconnect:src", "cm-disable:BTHENUM\\..."
    bool Ok,
    int Code,           // raw HRESULT/CONFIGRET/Win32; 0 on success; NativeCodes.NotAttempted or
                        // NativeCodes.NotAvailable when no native call was made
    string CodeName,    // "S_OK", "E_NOTFOUND", "CR_ACCESS_DENIED", "ERROR_SERVICE_DOES_NOT_EXIST"; build
                        // with StepOutcomes so the name comes from the code's own family
    string? Detail);

public sealed record ControllerResult(
    OpStatus Status,
    string UserMessage,                       // British English, short, no em-dash
    IReadOnlyList<StepOutcome> Steps)
{
    public static ControllerResult Ok(string msg, IReadOnlyList<StepOutcome>? steps = null) =>
        new(OpStatus.Success, msg, steps ?? Array.Empty<StepOutcome>());
    public static ControllerResult Already(string msg) =>
        new(OpStatus.AlreadyInState, msg, Array.Empty<StepOutcome>());
    public static ControllerResult Fail(string msg, IReadOnlyList<StepOutcome> steps) =>
        new(OpStatus.Failed, msg, steps);
    public bool IsSuccess => Status is OpStatus.Success or OpStatus.AlreadyInState;

    // The nonce of the gate run that produced this result, for a node change sent through the gate; null otherwise. An allow's
    // nonce names its status file, which lists what it changed, so the undo can put back exactly that.
    public string? GateNonce { get; init; }
}

public sealed record ConnectResult(
    ConnectOutcome Outcome,
    string UserMessage,
    IReadOnlyList<StepOutcome> Steps)
{
    public bool Confirmed => Outcome == ConnectOutcome.Confirmed;
}
