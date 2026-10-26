using System.Globalization;
using Earshot.Repair;

namespace Earshot.Pairing;

// The card line for what Windows answered to a pairing request. The meanings are Microsoft's; this build never saw most of
// these statuses on a real pairing (the faulty radio of the pairing debug run failed below this call), so a line claims only
// the documented meaning and says nothing about why. No line advises an Apple reset.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicepairingresultstatus
internal static class PairReasons
{
    // The statuses by value, as the page above lists them.
    internal const int Paired = 0;
    internal const int NotReadyToPair = 1;
    internal const int NotPaired = 2;
    internal const int AlreadyPaired = 3;
    internal const int ConnectionRejected = 4;
    internal const int TooManyConnections = 5;
    internal const int HardwareFailure = 6;
    internal const int AuthenticationTimeout = 7;
    internal const int AuthenticationNotAllowed = 8;
    internal const int AuthenticationFailure = 9;
    internal const int NoSupportedProfiles = 10;
    internal const int ProtectionLevelCouldNotBeMet = 11;
    internal const int AccessDenied = 12;
    internal const int InvalidCeremonyData = 13;
    internal const int PairingCanceled = 14;
    internal const int OperationAlreadyInProgress = 15;
    internal const int RequiredHandlerNotRegistered = 16;
    internal const int RejectedByHandler = 17;
    internal const int RemoteDeviceHasAssociation = 18;
    internal const int Failed = 19;

    internal const string PairedLine = "Paired.";

    // The status of a pairing call, or null for a call that threw (its code is an HRESULT, not a status).
    internal static int? StatusOf(PairingCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Threw || result.Code is < Paired or > Failed ? null : result.Code;
    }

    // Done: now paired, or already paired.
    internal static bool IsSuccess(PairingCallResult result) => StatusOf(result) is Paired or AlreadyPaired;

    // The failure statuses whose documented meaning does not say the device answered: not paired, the authentication timing
    // out, or a plain failure. On a device whose name could not be read, one of these is read as the PC not reaching it (see
    // PairingSession). That set is Earshot's reading of the documented meanings, not an observation: the pairing debug run of
    // 25 October saw a device with no name read and pairings that failed, and which status Windows returned was not recorded.
    // Every other failure status is worded as the device's answer ("the device object indicated" a hardware failure or too
    // many connections, turned the connection down, failed the check, refused, or is set up with something else) or a step
    // Windows itself refused, so it says nothing about this PC reaching it.
    internal static bool SaysNoAnswer(PairingCallResult result) => StatusOf(result) is NotPaired or AuthenticationTimeout or Failed;

    internal static string LineFor(PairingCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int? status = StatusOf(result);
        if (status is not int value)
        {
            return "Windows couldn't pair the device (code 0x" + unchecked((uint)result.Code).ToString("X8", CultureInfo.InvariantCulture) + ").";
        }

        string number = value.ToString(CultureInfo.InvariantCulture);
        return value switch
        {
            Paired or AlreadyPaired => PairedLine,
            NotReadyToPair => "The device isn't in a state where it can be paired.",
            NotPaired => "Windows didn't pair the device.",
            ConnectionRejected => "The device turned down the connection.",
            TooManyConnections => "The device can't accept any more connections.",
            HardwareFailure => "The device reported a hardware failure.",
            AuthenticationTimeout => "Pairing took too long and stopped.",
            AuthenticationNotAllowed or ProtectionLevelCouldNotBeMet or InvalidCeremonyData or RequiredHandlerNotRegistered =>
                "Windows couldn't complete the pairing (code " + number + ").",
            AuthenticationFailure => "Pairing failed because the authentication was turned down.",
            NoSupportedProfiles => "Windows found no profiles this device can use.",
            AccessDenied => "Windows didn't allow Earshot to pair.",
            PairingCanceled or RejectedByHandler => "Pairing was cancelled.",
            OperationAlreadyInProgress => "The device is already pairing or unpairing. Try again in a moment.",
            RemoteDeviceHasAssociation => "The device already has an association.",
            _ => "Windows couldn't pair the device (code " + number + ").",
        };
    }
}

// The words of the Pair AirPods steps, one line each (British English, no em dashes). Drafts for the prose pass.
// Apple's page for putting AirPods in pairing mode: AirPods 4 and AirPods Pro 3 by a double-tap on the front of the case, every other model by holding the setup button on the back, both until the status light flashes white. The two
// lines are Earshot's words, not Apple's.
// https://support.apple.com/en-us/108786
internal static class PairingText
{
    public const string TurnOffIPhoneBluetooth = "Turn off Bluetooth on your iPhone for now.";

    public const string CaseFront = "AirPods Pro 3 or AirPods 4: open the lid, then double-tap the front of the case until the light flashes white.";

    public const string CaseBack = "Other AirPods: open the lid, then hold the button on the back until the light flashes white.";

    // What the person reads while the scan runs: both lines, because the model is not known before pairing.
    public static readonly string Instruction = CaseFront + Environment.NewLine + CaseBack;

    public const string Looking = "Looking for AirPods";

    public const string Choose = "Choose your AirPods";

    public const string NoName = "Headphones with no name";

    public const string PairButton = "Pair";

    public const string Pairing = "Pairing";

    public const string SetsItselfUp = "Paired. Earshot sets itself up next, and Windows asks for permission.";

    public const string NothingFound = "No AirPods turned up. Check the lid is open and the light flashes white, then try again.";

    public const string ScanStopped = "Windows stopped looking for Bluetooth devices. Check Bluetooth is on, then try again.";

    public const string NotAirPods = "That device didn't pass Earshot's AirPods check, so Earshot adopted nothing. It is still paired in Windows; remove it there if you don't want it.";

    public const string Unverified = "Paired, but Earshot couldn't check that these are AirPods. If they are yours, choose them.";

    public const string Cancelled = "Pairing was cancelled.";

    public const string TimedOut = "Pairing took too long and was stopped.";

    public static string DoesItShow(string? pin) => "Does your device show " + pin + "?";
}
