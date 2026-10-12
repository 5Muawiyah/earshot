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
}
