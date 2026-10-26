namespace Earshot.App;

// The three things the tray does about a Fix connection run it did not start itself. Each default is inert, which is how the
// coordinator behaved before any of them existed.

// Run once when the coordinator starts, before anything else it does about a run of Fix connection that may have stopped half
// way: a marker left by a run that has gone is answered with the normal block.
internal interface IStartRecovery
{
    // True when it changed the nodes, so the status in hand is out of date.
    Task<bool> RunAsync(CancellationToken ct);
}

internal sealed class NoStartRecovery : IStartRecovery
{
    public static NoStartRecovery Instance { get; } = new();

    public Task<bool> RunAsync(CancellationToken ct) => Task.FromResult(false);
}

// Whether a Fix connection run is in the middle of pairing the AirPods again. While it is, the link the new pairing makes is not
// cut by an automatic block: the run ends in the normal block itself, and a window that is never closed ends by itself.
// Set by the tray while its own Pair AirPods runs; read by the coordinator's checks for unprotected AirPods.
internal sealed class PairRunFlag
{
    private volatile bool _active;

    public bool Active
    {
        get => _active;
        set => _active = value;
    }
}

internal interface IRePairWindow
{
    bool IsOpen { get; }
}

internal sealed class NoRePairWindow : IRePairWindow
{
    public static NoRePairWindow Instance { get; } = new();

    public bool IsOpen => false;
}

// What the nodes of this PC show about AirPods that are paired but are not the ones Earshot protects.
//   Read        the list of devices could be read at all
//   Pinned      Earshot holds a record of the AirPods it protects, so "not the ones it protects" means something
//   Unprotected how many paired sets of AirPods are not the pinned one
internal sealed record ProtectionReading(bool Read, bool Pinned, int Unprotected)
{
    public static ProtectionReading Nothing { get; } = new(false, false, 0);

    public bool AnyUnprotected => Read && Pinned && Unprotected > 0;
}

internal interface IProtectionCheck
{
    Task<ProtectionReading> ReadAsync(CancellationToken ct);
}

internal sealed class NoProtectionCheck : IProtectionCheck
{
    public static NoProtectionCheck Instance { get; } = new();

    public Task<ProtectionReading> ReadAsync(CancellationToken ct) => Task.FromResult(ProtectionReading.Nothing);
}

// Whether any AirPods are paired with this PC now, by the identity rule and with their device node in the tree. Connect in Windows
// asks it before Earshot is set up: with none paired there is nothing to connect in Windows, and the person is taken to Pair AirPods.
//   Read   the node list could be read at all
//   Count  how many sets of AirPods are paired now
internal sealed record PairedAirPodsReading(bool Read, int Count)
{
    public static PairedAirPodsReading Unknown { get; } = new(false, 0);

    public bool NonePaired => Read && Count == 0;
}

internal interface IPairedAirPodsCheck
{
    Task<PairedAirPodsReading> ReadAsync(CancellationToken ct);
}

internal sealed class NoPairedAirPodsCheck : IPairedAirPodsCheck
{
    public static NoPairedAirPodsCheck Instance { get; } = new();

    public Task<PairedAirPodsReading> ReadAsync(CancellationToken ct) => Task.FromResult(PairedAirPodsReading.Unknown);
}
