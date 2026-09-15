using Earshot.Contracts;
using Earshot.Icons;

namespace Earshot.Tray;

// What the left click would do: connect or disconnect one device container.
internal sealed record ToggleIntent(bool Connect, Guid Container, string DeviceName);

// Pure decisions behind the tray icon, tooltip, cards, left click and first-sighting pin.
internal static class TrayStatus
{
    // NOTIFYICONDATA.szTip holds 128 characters including the terminating NUL.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    public const int MaxTooltipLength = 127;

    public const string AppName = "Earshot";
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
    public const string Blocked = "blocked";
    public const string NotFound = "not found";

    public const string CardConnecting = "Connecting";
    public const string CardConnected = "Connected";
    public const string CardDisconnected = "Disconnected";
    public const string CardBlockedAtBoot = "Blocked at boot";
    public const string MicrophoneNotice = "This turns off the AirPods microphone.";

    // The target the tray acts on and shows. When a container is pinned, only a target in that container
    // counts; a target in any other container is not the pinned device, so it is treated as not found.
    // With nothing pinned, the resolved target counts. The left click, menu, tooltip, icon and cards all
    // go through this, so the menu can never say "Disconnect" while the click would connect.
    public static DeviceModel? ActiveTarget(DeviceSnapshot snapshot, EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        DeviceModel? target = snapshot.Target;
        return target is not null &&
               (settings.PinnedContainerId == Guid.Empty || target.ContainerId == settings.PinnedContainerId)
            ? target
            : null;
    }

    // True when the boot block status says the gate task is missing. Unknown (null) is not "not set up".
    public static bool NeedsSetUp(BootBlockStatus? block) => block?.State == BlockState.NotSetUp;

    // The device name to show: the active target's name, or the match string when nothing is found.
    public static string DeviceName(DeviceSnapshot snapshot, EarshotSettings settings)
    {
        string? name = ActiveTarget(snapshot, settings)?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? settings.DeviceMatch : name;
    }

    // "Earshot: <name> - connected|disconnected|blocked|not found", with the name shortened so the
    // whole text fits in 127 characters.
    public static string Tooltip(DeviceSnapshot snapshot, BootBlockStatus? block, EarshotSettings settings)
    {
        string state = StateWord(snapshot, block, settings);
        string prefix = AppName + ": ";
        string suffix = " - " + state;
        string name = Truncate(DeviceName(snapshot, settings), MaxTooltipLength - prefix.Length - suffix.Length);
        return prefix + name + suffix;
    }

    public static string StateWord(DeviceSnapshot snapshot, BootBlockStatus? block, EarshotSettings settings)
    {
        DeviceModel? target = ActiveTarget(snapshot, settings);
        if (target?.Connection is ConnectionState.Connected or ConnectionState.Disconnecting)
        {
            return Connected;
        }

        if (block?.State == BlockState.Blocked)
        {
            return Blocked;
        }

        return target is null ? NotFound : Disconnected;
    }

    public static GlyphState Glyph(DeviceSnapshot snapshot, BootBlockStatus? block, EarshotSettings settings, bool toggleInFlight)
    {
        ConnectionState? connection = ActiveTarget(snapshot, settings)?.Connection;
        if (toggleInFlight || connection is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            return GlyphState.Busy;
        }

        if (connection == ConnectionState.Connected)
        {
            return GlyphState.Connected;
        }

        return block?.State == BlockState.Blocked ? GlyphState.Blocked : GlyphState.Disconnected;
    }

    // The status line for a card that reports the current state (for example when a second copy of
    // Earshot is started).
    public static string CardStatus(DeviceSnapshot snapshot, BootBlockStatus? block, EarshotSettings settings) =>
        StateWord(snapshot, block, settings) switch
        {
            Connected => CardConnected,
            Blocked => CardBlockedAtBoot,
            NotFound => NotFoundMessage(settings),
            _ => CardDisconnected,
        };

    public static string NotFoundMessage(EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.DeviceMatch + " not found. Connect them once, or choose your device from the menu.";
    }

    // The container a left click acts on: the pinned container when there is one, otherwise the
    // resolved target's. Null when neither is a valid device container.
    public static ToggleIntent? Intent(DeviceSnapshot snapshot, EarshotSettings settings)
    {
        DeviceModel? target = ActiveTarget(snapshot, settings);
        Guid container = settings.PinnedContainerId != Guid.Empty
            ? settings.PinnedContainerId
            : target?.ContainerId ?? Guid.Empty;
        if (!NodeMatch.IsValidTargetContainer(container))
        {
            return null;
        }

        bool connected = target is not null &&
                         target.ContainerId == container &&
                         target.Connection is ConnectionState.Connected or ConnectionState.Disconnecting;
        return new ToggleIntent(Connect: !connected, container, DeviceName(snapshot, settings));
    }

    // The target to pin on its first sighting: it matches the name, has a valid container, and either
    // nothing is pinned yet or its container is pinned without an address. Null otherwise.
    public static DeviceModel? PinCandidate(DeviceSnapshot snapshot, EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        if (snapshot.Target is not { } target ||
            !NodeMatch.IsValidTargetContainer(target.ContainerId) ||
            !NodeMatch.NameMatches(target.DisplayName, settings.DeviceMatch))
        {
            return null;
        }

        bool nothingPinned = settings.PinnedContainerId == Guid.Empty;
        bool addressMissing = settings.PinnedContainerId == target.ContainerId && settings.PinnedAddress.Length == 0;
        return nothingPinned || addressMissing ? target : null;
    }

    // Shortens text to at most maxLength UTF-16 units without splitting a surrogate pair.
    internal static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (text.Length <= maxLength)
        {
            return text;
        }

        int length = maxLength;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}
