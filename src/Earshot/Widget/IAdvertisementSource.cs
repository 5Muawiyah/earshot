using Earshot.Contracts;

namespace Earshot.Widget;

public readonly record struct AdvertisementSample(
    ushort CompanyId, byte[] Data, sbyte Rssi, DateTimeOffset Timestamp, uint SenderTag);

public enum AdvertisementSourceState { Created, Started, Stopped, Aborted }

public sealed record AdvertisementSourceStopped(int ErrorCode, string ErrorName, StepOutcome Step);

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
