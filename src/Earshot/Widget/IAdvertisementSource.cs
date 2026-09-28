using Earshot.Contracts;

namespace Earshot.Widget;

public readonly record struct AdvertisementSample(
    ushort CompanyId, byte[] Data, sbyte Rssi, DateTimeOffset Timestamp, uint SenderTag);

public enum AdvertisementSourceState { Created, Started, Stopped, Aborted }

// Generation is whatever the caller last passed into Start(), for whichever run is currently live: it lets
// a caller tell a Stopped event that belongs to the run it currently believes is live apart from a late one
// that still belongs to a run already superseded by a later Start (Stop then an immediate Start, most
// often, where the native watcher's own Stopped(Success) for the old run can arrive after the new one is
// already under way). The source never invents this number itself, so a fresh instance (built after the
// setting goes off then on) never has to coordinate its own counting with any earlier instance's.
public sealed record AdvertisementSourceStopped(int ErrorCode, string ErrorName, StepOutcome Step, int Generation);

// The watcher, seen from the rest of the widget: passive, unfiltered, never connects, pairs or touches a
// device node. The real implementation is WinRtAdvertisementSource; tests use a fake.
internal interface IAdvertisementSource : IDisposable
{
    AdvertisementSourceState State { get; }

    // generation is the caller's own label for the run this call is starting, echoed back on Stopped: see
    // AdvertisementSourceStopped's comment. Never throws for anything Windows did; the step carries the raw
    // code.
    StepOutcome Start(int generation);

    StepOutcome Stop();

    event EventHandler<AdvertisementSample>? Received;   // whatever thread Windows calls back on

    event EventHandler<AdvertisementSourceStopped>? Stopped;
}
