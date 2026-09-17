namespace Earshot.Contracts.Null;

// The shipping battery provider for v1. The battery check (README, Battery) found no battery property on this hardware, so
// there is no source and never a value: Percent is null, and the UI omits the battery element entirely.
public sealed class NoBatterySource : IBatteryProvider
{
    public bool HasSource => false;

    public BatteryReading Read(Guid containerId) => BatteryReading.None;
}
