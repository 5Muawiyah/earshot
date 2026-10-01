using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// A paired model source that answers from a field, so a test chooses the model (or no model) without a device node
// behind it. Counts the reads, so a test can prove when the service asked again.
internal sealed class FakePairedModelSource : IPairedModelSource
{
    public FakePairedModelSource(ushort? model = BroadcastFixtures.PairedModel)
    {
        Model = model;
    }

    public ushort? Model { get; set; }

    public IReadOnlyList<StepOutcome> Steps { get; set; } = [];

    public int Reads { get; private set; }

    public Guid LastContainer { get; private set; }

    public string LastAddress { get; private set; } = "";

    public PairedModelRead Read(Guid container, string address12)
    {
        Reads++;
        LastContainer = container;
        LastAddress = address12;
        return new PairedModelRead(Model, Steps);
    }
}
