using System.Globalization;
using Earshot.Audio.Connect;
using Earshot.Contracts;

namespace Earshot.App;

// Whose side a failed connect is on.
//   None        nothing failed, or it was cancelled
//   Temporary   a code that usually clears on its own: tried once more when the time allows
//   PcSide      this PC, its driver, its pairing or Earshot's set-up; the only class that can lead to Fix connection
//   DeviceSide  the AirPods are away, in their case or in use on the phone; Fix connection never helps
internal enum FailureClass
{
    None,
    Temporary,
    PcSide,
    DeviceSide,
}

// What the radio side of the PC could tell about the AirPods when the connect timed out, from the broadcast it hears.
// Unknown says nothing, and is never taken as a reason to offer a repair.
internal enum DeviceHint
{
    Unknown,
    NotHeard,
    LidClosed,
    InUseElsewhere,
    HeardNearby,
}

// Where the hints come from. The default knows nothing.
internal interface IDeviceHints
{
    DeviceHint Read();
}

internal sealed class NoDeviceHints : IDeviceHints
{
    public static NoDeviceHints Instance { get; } = new();

    public DeviceHint Read() => DeviceHint.Unknown;
}

// A classified failure. Reason is the plain sentence for the card. Code is the raw code text that decided it, kept for the
// steps and the log, never shown on the card.
internal sealed record ConnectFailure(FailureClass Class, string Reason, string Code)
{
    public static ConnectFailure None { get; } = new(FailureClass.None, "", "");

    public bool IsTemporary => Class == FailureClass.Temporary;
}

// Pure. The table of codes is the one in Windows' own headers (winerror.h, audioclient.h, cfgmgr32.h); a code that is not in
// it is this PC's side, with the raw value kept.
// https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-
// https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize
internal static class ConnectFailureClassifier
{
    public const string WindowsDidNotBringThemBack = "Windows did not bring the AirPods back.";
    public const string DidNotAnswer = "The AirPods did not answer this PC. They may need pairing again.";
    public const string NeedPairingAgain = "These AirPods need pairing again.";
    public const string WindowsRefused = "Windows refused the change.";
    public const string DriverDidNotAccept = "The audio driver did not accept the request.";
    public const string PartOfThemStayedOff = "Part of the AirPods stayed off.";
    public const string OpenTheCase = "Open the case near this PC.";
    public const string InUseOnYourIPhone = "In use on your iPhone.";
    public const string NotHeardNearby = "Not heard nearby.";

    // HRESULT_FROM_WIN32 values and audioclient.h.
    internal const int DeviceInvalidated = unchecked((int)0x88890004);      // AUDCLNT_E_DEVICE_INVALIDATED
    internal const int ErrorNotFound = unchecked((int)0x80070490);          // ERROR_NOT_FOUND
    internal const int DeviceNotConnected = unchecked((int)0x8007048F);     // ERROR_DEVICE_NOT_CONNECTED
    internal const int NotReady = unchecked((int)0x80070015);               // ERROR_NOT_READY
    internal const int Busy = unchecked((int)0x800700AA);                   // ERROR_BUSY
    internal const int SemTimeout = unchecked((int)0x80070079);             // ERROR_SEM_TIMEOUT
    internal const int Timeout = unchecked((int)0x800705B4);                // ERROR_TIMEOUT
    internal const int GenFailure = unchecked((int)0x8007001F);             // ERROR_GEN_FAILURE
    internal const int AccessDenied = unchecked((int)0x80070005);           // E_ACCESSDENIED
    internal const int NotSupported = unchecked((int)0x80070032);           // ERROR_NOT_SUPPORTED
    internal const int NotImplemented = unchecked((int)0x80004001);         // E_NOTIMPL
    internal const int SetNotFound = unchecked((int)0x80070492);            // ERROR_SET_NOT_FOUND
    private const uint CrAccessDenied = 0x33;                               // CR_ACCESS_DENIED

    private static readonly HashSet<int> TemporaryCodes =
    [
        DeviceInvalidated, ErrorNotFound, DeviceNotConnected, NotReady, Busy, SemTimeout, Timeout, GenFailure,
    ];

    private static readonly HashSet<int> NotSupportedCodes = [NotSupported, NotImplemented, SetNotFound];

    // A connect or disconnect request that came back without reaching ACTIVE, from what the controller returned. retried says
    // the temporary class has had its one retry already, so it is this PC's side now.
    public static ConnectFailure Classify(ConnectResult result, DeviceHint hint, bool retried)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case ConnectOutcome.Confirmed:
                return ConnectFailure.None;

            case ConnectOutcome.NotFound:
                return new ConnectFailure(FailureClass.PcSide, NeedPairingAgain, "not-found");

            case ConnectOutcome.NodesBlocked:
                return Escalate(FailureClass.Temporary, WindowsDidNotBringThemBack, "nodes-blocked", retried);

            case ConnectOutcome.AttemptedTimedOut:
                return AfterRequestTimedOut(hint, retried);

            case ConnectOutcome.NoFiltersResponded:
                return FromFilterCodes(result.Steps, retried);

            default:
                return FromFailedRead(result, retried);
        }
    }

    // The render endpoint did not come back after the allow.
    public static ConnectFailure EndpointNotBack(bool retried) =>
        Escalate(FailureClass.Temporary, WindowsDidNotBringThemBack, "endpoint-not-back", retried);

    // An allow that did not finish: a gate exit that says the set-up is not usable, or part of the nodes stayed off.
    public static ConnectFailure AllowFailed(ControllerResult allow)
    {
        ArgumentNullException.ThrowIfNull(allow);
        StepOutcome? exit = allow.Steps.LastOrDefault(s => s.Step == "gate-exit");
        if (exit is not null)
        {
            return new ConnectFailure(FailureClass.PcSide, allow.UserMessage, exit.CodeName);
        }

        if (allow.Status == OpStatus.Partial)
        {
            return new ConnectFailure(FailureClass.PcSide, PartOfThemStayedOff, "allow-partial");
        }

        return new ConnectFailure(FailureClass.PcSide, allow.UserMessage, "allow-" + allow.Status);
    }

    private static ConnectFailure Escalate(FailureClass first, string reason, string code, bool retried) =>
        retried ? new ConnectFailure(FailureClass.PcSide, reason, code) : new ConnectFailure(first, reason, code);

    // The request was accepted (S_OK) and no ACTIVE endpoint came in time.
    private static ConnectFailure AfterRequestTimedOut(DeviceHint hint, bool retried) => hint switch
    {
        DeviceHint.LidClosed => new ConnectFailure(FailureClass.DeviceSide, OpenTheCase, "timed-out:lid-closed"),
        DeviceHint.InUseElsewhere => new ConnectFailure(FailureClass.DeviceSide, InUseOnYourIPhone, "timed-out:in-use-elsewhere"),
        DeviceHint.NotHeard => new ConnectFailure(FailureClass.DeviceSide, NotHeardNearby, "timed-out:not-heard"),

        // Nothing is known about the AirPods from the air. A repair is never offered on a guess, so this is the
        // device's side, and the sentence asks for the one thing that costs nothing.
        DeviceHint.Unknown => new ConnectFailure(FailureClass.DeviceSide, OpenTheCase, "timed-out:no-hint"),
        _ => Escalate(FailureClass.Temporary, DidNotAnswer, "timed-out:heard-nearby", retried),
    };

    private static ConnectFailure FromFilterCodes(IReadOnlyList<StepOutcome> steps, bool retried)
    {
        List<StepOutcome> sent = steps
            .Where(s => s.Step.StartsWith(KsConnectPath.ReconnectStep + ":", StringComparison.Ordinal) && s.Code != NativeCodes.NotAttempted)
            .ToList();
        string codes = sent.Count == 0 ? "no-filter-sent" : string.Join(",", sent.Select(s => "0x" + s.Code.ToString("X8", CultureInfo.InvariantCulture)));

        if (sent.Any(s => s.Code == AccessDenied))
        {
            return new ConnectFailure(FailureClass.PcSide, WindowsRefused, codes);
        }

        if (sent.Count > 0 && sent.All(s => NotSupportedCodes.Contains(s.Code)))
        {
            return new ConnectFailure(FailureClass.PcSide, DriverDidNotAccept, codes);
        }

        if (sent.Any(s => TemporaryCodes.Contains(s.Code)))
        {
            return Escalate(FailureClass.Temporary, BlockCoordinator.CouldNotReachDriverMessage, codes, retried);
        }

        // Unknown codes are this PC's side, and the raw value stays in the code text.
        return new ConnectFailure(FailureClass.PcSide, BlockCoordinator.CouldNotReachDriverMessage, codes);
    }

    private static ConnectFailure FromFailedRead(ConnectResult result, bool retried)
    {
        if (result.UserMessage == ConnectMessages.OutputTurnedOff)
        {
            return new ConnectFailure(FailureClass.PcSide, ConnectMessages.OutputTurnedOff, "output-disabled");
        }

        if (result.UserMessage == ConnectMessages.WentAway)
        {
            return Escalate(FailureClass.Temporary, ConnectMessages.WentAway, "device-invalidated", retried);
        }

        if (result.UserMessage is ConnectMessages.DriverBusy or ConnectMessages.CouldNotReadDevices)
        {
            return Escalate(FailureClass.Temporary, result.UserMessage, "read-failed", retried);
        }

        return new ConnectFailure(FailureClass.PcSide, result.UserMessage, "failed");
    }

    // True when a CONFIGRET from the gate or a node step is an access refusal.
    internal static bool IsAccessDenied(uint configRet) => configRet == CrAccessDenied;
}
