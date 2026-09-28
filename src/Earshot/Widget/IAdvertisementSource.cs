using Earshot.Contracts;

namespace Earshot.Widget;

public readonly record struct AdvertisementSample(
    ushort CompanyId, byte[] Data, sbyte Rssi, DateTimeOffset Timestamp, uint SenderTag);

public enum AdvertisementSourceState { Created, Started, Stopped, Aborted }

// Generation is the source's own count of Start() calls, whatever each one returns: it lets a caller tell
// a Stopped event that belongs to the run it just started apart from a late one that still belongs to a
// run already superseded by a later Start (Stop then an immediate Start, most often, where the native
// watcher's own Stopped(Success) for the old run can arrive after the new one is already under way).
public sealed record AdvertisementSourceStopped(int ErrorCode, string ErrorName, StepOutcome Step, int Generation);

// The watcher, seen from the rest of the widget: passive, unfiltered, never connects, pairs or touches a
// device node. The real implementation is WinRtAdvertisementSource; tests use a fake.
internal interface IAdvertisementSource : IDisposable
{
    AdvertisementSourceState State { get; }

    StepOutcome Start();   // never throws for anything Windows did; the step carries the raw code

    StepOutcome Stop();

    event EventHandler<AdvertisementSample>? Received;   // whatever thread Windows calls back on

    event EventHandler<AdvertisementSourceStopped>? Stopped;
}
