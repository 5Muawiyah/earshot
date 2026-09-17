using Earshot.App;
using Earshot.Boot;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The Block at boot menu toggle. With the setting off the nodes are enabled at rest, as Windows has them, so
// turning it off allows nodes that are still blocked in the same operation, in ProtectionPolicy's Allow order.
[TestClass]
public sealed class BlockAtBootToggleTests
{
    private static readonly string[] SetBootOffThenAllow = ["setboot-off", "allow"];
    private static readonly string[] AllowThenProtectOn = ["allow", "protect-on"];
    private static readonly string[] SetBootOffOnly = ["setboot-off"];
    private static readonly string[] SetBootOnOnly = ["setboot-on"];

    private static ControllerResult Toggle(CoordinatorHarness h, bool blockAtBoot)
    {
        Task<ControllerResult> task = h.Coordinator.SetBlockAtBootAsync(blockAtBoot, CardPlace.NearCursor);
        h.Pump();
        Assert.IsTrue(task.IsCompleted, "The Block at boot change did not finish.");
        return task.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TurningItOffAllowsBlockedNodesThenProtectsAgainIfHandsfreeCameBack()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Protection.State = AudioProtectionState.Protected;
        h.Start();
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = h.Block.Status with { State = BlockState.Allowed };
            h.Protection.State = AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(2));
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };

        ControllerResult result = Toggle(h, blockAtBoot: false);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        Assert.AreEqual(BlockCoordinator.BlockAtBootOffMessage, result.UserMessage);
        CollectionAssert.AreEqual(SetBootOffThenAllow, h.Block.Calls);
        CollectionAssert.AreEqual(AllowThenProtectOn, h.Trace, "Protection changes only once the nodes are enabled.");
        Assert.IsFalse(h.Coordinator.IdleWaitRunning, "With Block at boot off nothing blocks them again.");
    }

    [TestMethod]
    public void TurningItOffWithTheNodesAllowedChangesNoNode()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        ControllerResult result = Toggle(h, blockAtBoot: false);

        Assert.IsTrue(result.IsSuccess, result.UserMessage);
        CollectionAssert.AreEqual(SetBootOffOnly, h.Block.Calls);
    }

    [TestMethod]
    public void AnAllowThatDoesNotTakeSaysTheAirPodsAreStillBlocked()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Block.OnAllow = _ => Task.FromResult(ControllerResult.Fail(BlockController.AllowFailedMessage, []));

        ControllerResult result = Toggle(h, blockAtBoot: false);

        Assert.AreEqual(OpStatus.Partial, result.Status);
        Assert.AreEqual(BlockCoordinator.BlockAtBootOffStillBlockedMessage, result.UserMessage);
        CollectionAssert.AreEqual(SetBootOffThenAllow, h.Block.Calls);
    }

    // Turning it on changes no node itself: the idle rule blocks enabled nodes that are not in use.
    [TestMethod]
    public void TurningItOnLeavesTheBlockToTheIdleRule()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        ControllerResult result = Toggle(h, blockAtBoot: true);

        Assert.IsTrue(result.IsSuccess, result.UserMessage);
        CollectionAssert.AreEqual(SetBootOnOnly, h.Block.Calls);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The change reads the new setting itself, so the idle rule does not wait for another read.");
    }
}
