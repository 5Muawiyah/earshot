using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Exit when the closing block is refused because the AirPods are connected.
[TestClass]
public sealed class ExitTests
{
    // A connect's allow may still land when the person chooses Exit. The nodes read Blocked, so the closing block is sent to run
    // behind the allow; by the time the gate looks, the allow has landed and the AirPods are linked, so the gate refuses and
    // changes nothing. Earshot then closes with the nodes enabled, and the person has to be told, as for Exit while in use.
    [TestMethod]
    public void ExitBehindALandedAllowThatTheGateRefusesInUseShowsTheNotice()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Block.OnAllow = _ => Task.FromResult(Results.GateWaitRanOut("Gate", "The boot block did not finish in time. Try again."));
        h.Toggle(connect: true);
        Assert.IsTrue(h.Coordinator.AllowMayLand);
        h.Link.State = LinkState.Connected;

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.Contains(h.Block.Calls, "block-refused", "The closing block did not reach the gate: " + string.Join(", ", h.Block.Calls));
        Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, h.Coordinator.ClosingNotice, "Earshot closed with the nodes enabled and said nothing.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "the gate found the AirPods connected"), "The log does not name the gate's refusal.");
    }
}
