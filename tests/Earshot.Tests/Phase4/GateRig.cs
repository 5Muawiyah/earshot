using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The gate over a fake node table in a temp folder, for the tests of the allow and the verbs built on it. Every run gets
// its own nonce, so a status file is never overwritten by the next run. The clock is the table's: pausing the table
// moves it, so a wait in the gate ends without sleeping.
internal sealed class GateRig : IDisposable
{
    private readonly TempFolder _temp = new();
    private int _runs;

    public GateRig(FakeNodeApi? nodes = null)
    {
        Time = new ManualTime();
        Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(Machine);
        Store = new GateStore(Machine);
        Nodes = nodes ?? RecordedNodes.Table();
        Assert.IsTrue(Store.WriteConfig(new GateConfig()).Ok);
    }

    public ManualTime Time { get; }

    public string Machine { get; }

    public GateStore Store { get; }

    public FakeNodeApi Nodes { get; }

    public FakeFolderSecurity Folders { get; } = new();

    public CapturingLog Log { get; } = new();

    public string LastNonce { get; private set; } = "";

    public void Pin(DeviceIdentity identity) => Assert.IsTrue(Store.WriteDevice(identity).Ok);

    public string NextNonce() => (++_runs).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    public GateExitCode Run(string verb, string? address = null, string? allowNonce = null, string? nonce = null)
    {
        LastNonce = nonce ?? NextNonce();
        var actions = new GateActions(Nodes, Store, Folders, Log, Time);
        return actions.Run(new GateRequest(verb, LastNonce, address, GateMode.Gate, allowNonce));
    }

    public GateStatusFile Status(string? nonce = null)
    {
        GateRead<GateStatusFile> read = Store.ReadStatus(nonce ?? LastNonce);
        Assert.IsTrue(read.IsOk, read.Step.Detail);
        return read.Value!;
    }

    public IEnumerable<NodeCall> Calls(string kind) => Nodes.Calls.Where(c => c.Kind == kind);

    // Every node of the AirPods set disabled with the persistent flag, as the boot block leaves them.
    public void BlockAirPodsAtRest()
    {
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            Nodes[id].MarkDisabled(persistent: true);
        }
    }

    public void Dispose() => _temp.Dispose();
}
