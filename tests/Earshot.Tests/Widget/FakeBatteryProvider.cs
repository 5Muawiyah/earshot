using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// A Windows Hands-Free figure source that answers from a field, so a test chooses the figure (or none, or a failure)
// without a device behind it. Counts the reads and keeps the container and address each was asked for.
internal sealed class FakeHandsFreeBatterySource : IHandsFreeBatterySource
{
    public int? Percent { get; set; }

    public string? Origin { get; set; } = "device node";

    public string? Note { get; set; }

    public IReadOnlyList<StepOutcome> Steps { get; set; } = [];

    public Exception? Throws { get; set; }

    public int Reads { get; private set; }

    public Guid LastContainer { get; private set; }

    public string LastAddress { get; private set; } = "";

    public HandsFreeBatteryRead Read(Guid container, string address12)
    {
        Reads++;
        LastContainer = container;
        LastAddress = address12;
        if (Throws is { } ex)
        {
            throw ex;
        }

        return new HandsFreeBatteryRead(Percent, Percent is null ? null : Origin, Percent is null ? Note ?? "No figure." : null, Steps);
    }
}

// The same figure through the battery contract every provider shares, for the places that take an IBatteryProvider,
// and through Windows' figure source for the places that want the detail. It keeps the container and address it was
// asked for.
internal sealed class FakeBatteryProvider(int? percent = null, string? note = null, IReadOnlyList<StepOutcome>? steps = null)
    : IBatteryProvider, IHandsFreeBatterySource
{
    public bool HasSource => true;

    public int Reads { get; private set; }

    public Guid LastContainer { get; private set; }

    public string LastAddress { get; private set; } = "";

    public BatteryReading Read(Guid containerId)
    {
        Reads++;
        return new BatteryReading(percent);
    }

    public HandsFreeBatteryRead Read(Guid container, string address12)
    {
        Reads++;
        LastContainer = container;
        LastAddress = address12;
        return new HandsFreeBatteryRead(percent, percent is null ? null : "device node", percent is null ? note ?? "No figure." : null, steps ?? []);
    }
}
