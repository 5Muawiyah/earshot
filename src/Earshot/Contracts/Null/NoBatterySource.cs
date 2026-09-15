namespace Earshot.Contracts.Null;

// The shipping battery provider for v1. Phase 0 found no battery property on this hardware, so
// there is no source and never a value. Percent stays 0 only because the struct needs a value;
// HasValue false means there is no figure, and the UI omits the battery element entirely.
public sealed class NoBatterySource : IBatteryProvider
{
    public bool HasSource => false;

    public BatteryReading Read(Guid containerId) => new(HasValue: false, Percent: 0);
}
