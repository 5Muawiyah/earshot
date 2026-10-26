using Earshot.Contracts;
using Earshot.Diagnostics;

namespace Earshot.App;

// Whether the AirPods' device node reports a live Bluetooth link. The bit behind it (DN_DEVICE_DISCONNECTED) is a header
// comment in cfg.h that has been seen to follow the link on this PC, not a documented contract, so it is never the only
// evidence: the coordinator weighs it against the render endpoint and caps what it can hold back.
internal enum LinkState
{
    Unknown,
    Connected,
    Disconnected,
}

internal interface ILinkProbe
{
    // A read of the device node's status; no request to the device. Unknown when it could not be read.
    Task<LinkState> ReadAsync(CancellationToken ct);
}

internal sealed class NoLinkProbe : ILinkProbe
{
    public static NoLinkProbe Instance { get; } = new();

    public Task<LinkState> ReadAsync(CancellationToken ct) => Task.FromResult(LinkState.Unknown);
}

// Whether the interactive session is locked. A lock state that cannot be read counts as locked wherever it matters, because
// a link that forms in a locked session was not made by the person at the keyboard.
internal enum SessionLockState
{
    Unknown,
    Unlocked,
    Locked,
}

internal interface ISessionState
{
    SessionLockState Current { get; }
}

internal sealed class UnlockedSession : ISessionState
{
    public static UnlockedSession Instance { get; } = new();

    public SessionLockState Current => SessionLockState.Unlocked;
}

// Opens Windows' Bluetooth settings page for Connect in Windows.
internal interface IWindowsSettingsLauncher
{
    // https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
    StepOutcome OpenBluetoothSettings();
}

internal sealed class NoSettingsLauncher : IWindowsSettingsLauncher
{
    public static NoSettingsLauncher Instance { get; } = new();

    public StepOutcome OpenBluetoothSettings() => StepOutcomes.NotAvailable("open-settings", "No settings launcher in this build.");
}

// What the coordinator needs beyond the controllers it is built over. Every default is inert: no link evidence, an unlocked
// session, no history, no hints and no launcher, which is how the coordinator behaved before any of them existed.
internal sealed class CoordinatorServices
{
    public ILinkProbe Link { get; init; } = NoLinkProbe.Instance;

    public ISessionState Session { get; init; } = UnlockedSession.Instance;

    public IDeviceHints Hints { get; init; } = NoDeviceHints.Instance;

    public IWindowsSettingsLauncher Launcher { get; init; } = NoSettingsLauncher.Instance;

    public ConnectHistory? History { get; init; }

    public FixOfferTracker FixOffer { get; init; } = new();

    // A run of Fix connection that stopped half way is answered with the block when the coordinator starts.
    public IStartRecovery Recovery { get; init; } = NoStartRecovery.Instance;

    // While a Fix connection run is pairing the AirPods again, the new link is not cut.
    public IRePairWindow RePair { get; init; } = NoRePairWindow.Instance;

    // True while the tray's own Pair AirPods runs, or its start-time look at a pairing that did not finish is under way. That run
    // answers for the new pair itself, so the checks for AirPods Earshot does not protect wait it out rather than race it.
    public PairRunFlag TrayPairing { get; init; } = new();

    // AirPods that are paired and are not the ones Earshot protects, found when the coordinator starts. Only reported, never adopted.
    public IProtectionCheck Protection { get; init; } = NoProtectionCheck.Instance;

    // Whether any AirPods are paired, for Connect in Windows before Earshot is set up. Unknown by default, which opens Windows' settings.
    public IPairedAirPodsCheck PairedAirPods { get; init; } = NoPairedAirPodsCheck.Instance;

    // What the card for unprotected AirPods does on its one button. Null shows the card without one.
    public IFixLauncher? FixLauncher { get; init; }
}
