using System.Globalization;

namespace Earshot.Streaming;

// Every sentence the owner reads about playing from a phone, whether it lands on a card, in the tooltip or in
// a disabled menu item. British English, plain, short. Each one ends with a full stop and none carries a
// figure: a code Windows reported goes to the log, and the card says to look there. The words on an item the
// owner can click are labels, not sentences, and live in StreamingLabels.
//
// What these sentences must never claim, because no Microsoft page says it: which speaker the audio comes out
// of, that it will reach the AirPods, anything about what the PC's Bluetooth adapter supports, or that the
// phone has to be made discoverable. Pairing is done in Windows Settings; a desktop app cannot pair
// (DeviceInformationPairing.PairAsync is listed as unsupported).
// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support
internal static class StreamingCopy
{
    public const string NotCheckedYet = "Not checked yet.";
    public const string NeedsNewerWindows = "Needs a newer version of Windows.";
    public const string CannotReceive = "This copy of Windows cannot receive Bluetooth audio.";

    public const string LookingForDevices = "Looking for devices.";
    public const string CouldNotReadList = "Could not read the list of paired devices.";
    public const string NoPairedDevice = "No paired device can send audio to this PC.";
    public const string PairInSettings = "Pair the phone in Windows Settings first.";

    public const string BusyJustNow = "Busy just now. Try again in a moment.";
    public const string CannotSendAudioFormat = "{0} cannot send audio to this PC.";
    public const string CouldNotGetReadyFormat = "Could not get ready for {0}.";
    public const string WaitingFormat = "Waiting for {0}. Start playing something on it.";
    public const string DidNotAnswerFormat = "{0} did not answer in time. Try again.";
    public const string RefusedFormat = "Windows refused the connection. Reconnect {0} in Bluetooth settings, then try again.";
    public const string CouldNotConnectSeeLogFormat = "Could not connect to {0}. See the log.";
    public const string CouldNotConnectFormat = "Could not connect to {0}.";

    // Opened and Closed are "The connection is open." and "The connection is closed.", and Microsoft's own sample
    // words them "Connected" and "Disconnected". Neither says anything is playing, so neither sentence does.
    // https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionstate
    public const string ConnectedFormat = "{0} is connected and can play through this PC.";
    public const string DisconnectedFormat = "{0} has disconnected.";
    public const string NoLongerAcceptingFormat = "This PC is no longer accepting audio from {0}.";

    // Said once when Windows does not confirm it let go of a connection, in place of the sentence above: Earshot
    // asked, Windows did not say yes, and the code it gave instead is in the log.
    public const string MayStillBeConnectedFormat = "{0} may still be connected to this PC. See the log.";

    // Stands in for a device Windows gave no name for. Part of a sentence, not one, so it is not among the
    // public sentences above.
    private const string UnnamedDevice = "The device";

    // A device name is whatever its owner typed into the phone, so it is cleaned before it reaches a card, the
    // tooltip or a menu: no control characters, and no more than this many characters.
    internal const int MaxNameLength = 40;

    public static string For(string format, string deviceName) =>
        string.Format(CultureInfo.InvariantCulture, format, SafeName(deviceName));

    // The sentence for a build of Windows that cannot do this, or "" when it can.
    public static string ForSupport(StreamingSupport support) => support switch
    {
        StreamingSupport.Supported => "",
        StreamingSupport.BuildTooOld => NeedsNewerWindows,
        StreamingSupport.TypeMissing => CannotReceive,
        _ => NotCheckedYet,
    };

    public static string SafeName(string? name)
    {
        string clean = string.Concat((name ?? "").Where(c => !char.IsControl(c))).Trim();
        if (clean.Length == 0)
        {
            return UnnamedDevice;
        }

        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd() + "...";
    }

    // The tray tooltip with the streaming line under it, cut so the whole never passes the limit Windows puts
    // on a tooltip. The streaming line is the one that gives way: the first line is what Earshot is for.
    public static string TooltipWith(string tooltip, string line, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        if (string.IsNullOrEmpty(line))
        {
            return tooltip;
        }

        const string Ellipsis = "...";
        const int FewestUsefulCharacters = 12;
        int room = maxLength - tooltip.Length - 1;
        if (room >= line.Length)
        {
            return tooltip + "\n" + line;
        }

        return room < FewestUsefulCharacters + Ellipsis.Length
            ? tooltip
            : tooltip + "\n" + line[..(room - Ellipsis.Length)].TrimEnd() + Ellipsis;
    }
}
