using Earshot.Boot;

namespace Earshot.App;

// Whether any AirPods are paired with this PC now, from the identity rule over the device nodes. Only a set whose device node is
// in the tree counts: a removed pairing leaves its nodes behind, and they are not a pairing. A list that could not be read says
// nothing (Read false), so Connect in Windows never offers to pair on a failed read. Reads only; nothing is changed.
internal sealed class NodePairedAirPodsCheck : IPairedAirPodsCheck
{
    private readonly INodeReader _nodes;

    public NodePairedAirPodsCheck(INodeReader nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
    }

    public Task<PairedAirPodsReading> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    public PairedAirPodsReading Read()
    {
        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(_nodes);
        return found.Listed
            ? new PairedAirPodsReading(true, found.Candidates.Count(c => c.DevicePresent))
            : PairedAirPodsReading.Unknown with { Steps = found.Steps.Where(s => !s.Ok).ToList() };
    }
}
