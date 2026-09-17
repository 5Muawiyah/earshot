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
    private static readonly string[] SetBootOnThenBlock = ["setboot-on", "block"];

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

    // The gate run for turning it on was accepted but not seen to end (queued behind another run past the wait), so
    // it may still write the setting with no notification to say so. A read that still shows it off does not settle
    // the idle rule until the run could have ended, and once the setting lands the nodes are blocked.
    [TestMethod]
    public void ATurnOnWhoseRunDidNotEndIsReadAgainAndItsBlockFollowsWhenItLands()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Block.OnSetBlockAtBoot = (_, _) => Task.FromResult(Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));

        ControllerResult result = Toggle(h, blockAtBoot: true);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.TimedOutMessage, result.UserMessage);
        Assert.IsTrue(h.Coordinator.RecheckRunning, "A read that shows Block at boot off was taken as settled while the change may still land.");

        // The queued run writes config.json two minutes later.
        h.Advance(TimeSpan.FromMinutes(2));
        h.Block.Status = Statuses.Allowed();
        h.Advance(TimeSpan.FromMinutes(3));

        CollectionAssert.AreEqual(SetBootOnThenBlock, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ATurnOnWhoseRunDidNotEndAndNeverLandsStopsBeingReadOnceItCouldHaveEnded()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Block.OnSetBlockAtBoot = (_, _) => Task.FromResult(Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));
        Toggle(h, blockAtBoot: true);
        int reads = h.Block.StatusReads;

        h.Advance(BlockCoordinator.UnsettledGateWindow + BlockCoordinator.RecheckDelay);

        Assert.IsFalse(h.Coordinator.RecheckRunning, "Block at boot read off after the run could have ended settles the rule.");
        Assert.IsGreaterThan(reads, h.Block.StatusReads);
        CollectionAssert.AreEqual(SetBootOnOnly, h.Block.Calls);
    }

    // Exit before the next read: the setting has landed, but the last read still showed it off.
    [TestMethod]
    public void ExitAfterATurnOnWhoseRunDidNotEndReadsTheSettingAgainAndBlocks()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Block.OnSetBlockAtBoot = (_, _) => Task.FromResult(Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));
        Toggle(h, blockAtBoot: true);
        h.Block.Status = Statuses.Allowed();

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(SetBootOnThenBlock, h.Block.Calls, "Exit trusted a Block at boot read taken before the change could have landed.");
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
