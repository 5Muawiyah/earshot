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

    // The statuses that say the person or Earshot stopped the pairing. A failure that is none of these, on a device whose name
    // could not be read, is the one PC-side signal of the pairing debug run (see PairingSession).
    internal static bool IsStoppedOnPurpose(PairingCallResult result) => StatusOf(result) is Paired or AlreadyPaired or PairingCanceled or RejectedByHandler;

    internal static string LineFor(PairingCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int? status = StatusOf(result);
        if (status is not int value)
        {
            return "Windows couldn't pair the AirPods (code 0x" + unchecked((uint)result.Code).ToString("X8", CultureInfo.InvariantCulture) + ").";
        }

        string number = value.ToString(CultureInfo.InvariantCulture);
        return value switch
        {
            Paired or AlreadyPaired => PairedLine,
            NotReadyToPair => "Windows says the AirPods aren't ready to pair. Put the case in pairing mode and try again.",
            NotPaired => "Windows didn't pair the AirPods.",
            ConnectionRejected => "The AirPods turned down the connection.",
            TooManyConnections => "The AirPods can't take another connection right now.",
            HardwareFailure => "Windows reports a hardware failure.",
            AuthenticationTimeout => "Pairing took too long and stopped.",
            AuthenticationNotAllowed or ProtectionLevelCouldNotBeMet or InvalidCeremonyData or RequiredHandlerNotRegistered =>
                "Windows couldn't complete the pairing (code " + number + ").",
            AuthenticationFailure => "Pairing was turned down.",
            NoSupportedProfiles => "Windows found nothing it can use on this device.",
            AccessDenied => "Windows didn't allow Earshot to pair.",
            PairingCanceled or RejectedByHandler => "Pairing was cancelled.",
            OperationAlreadyInProgress => "Windows is already pairing a device. Try again in a moment.",
            RemoteDeviceHasAssociation => "Windows says the AirPods are already set up with something else.",
            _ => "Windows couldn't pair the AirPods (code " + number + ").",
        };
    }
}

// The words of the Pair AirPods steps, one line each (British English, no em dashes). Drafts for the prose pass.
// Apple's page for putting AirPods in pairing mode: AirPods 4, AirPods 5 and AirPods Pro 3 by a double-tap on the front of
// the case, every other model by holding the setup button on the back, both until the status light flashes white. The two
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

    public const string SetsItselfUp = "Paired. Earshot sets itself up next, and Windows asks for permission once.";

    public const string NothingFound = "No AirPods turned up. Check the lid is open and the light flashes white, then try again.";

    public const string NotAirPods = "That was not AirPods, so Earshot adopted nothing. It is still paired in Windows; remove it there if you don't want it.";

    public const string Cancelled = "Pairing was cancelled.";

    public const string TimedOut = "Pairing took too long and was stopped.";

    public static string DoesItShow(string? pin) => "Does your device show " + pin + "?";
}
