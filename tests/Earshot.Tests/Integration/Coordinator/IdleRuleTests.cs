using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The rule that keeps the at-rest invariant without any click: once the AirPods have not been in use for the
// grace period, with nothing in flight and a good read of both the endpoints and the nodes, the nodes go back to
// blocked. Anything unknown, and anything Earshot is doing, holds it off.
[TestClass]
public sealed class IdleRuleTests
{
    private static readonly TimeSpan JustUnderGrace = BlockCoordinator.IdleGrace - TimeSpan.FromSeconds(1);
    private static readonly string[] BlockOnly = ["block"];

    // In use on this PC with the nodes enabled: the one state in which the nodes are meant to be enabled.
    private static CoordinatorHarness InUse(bool blockAtBoot = true)
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        Assert.IsEmpty(h.Block.Calls, "Nothing is blocked while the AirPods are in use.");
        return h;
    }

    [TestMethod]
    public void TheNodesAreBlockedOnceTheAirPodsHaveNotBeenInUseForTheGracePeriod()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Idle(2));
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);

        h.Advance(JustUnderGrace);
        Assert.IsEmpty(h.Block.Calls, "The grace period had not run out.");

        h.Advance(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }

    [TestMethod]
    public void AirPodsThatComeBackIntoUseStopTheWait()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Idle(2));
        h.Advance(JustUnderGrace);
        h.Publish(Devices.Active(3));

        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);
        Assert.IsEmpty(h.Block.Calls);

        // Out of use again: the whole grace period has to pass afresh.
        h.Publish(Devices.Idle(4));
        h.Advance(JustUnderGrace);
        Assert.IsEmpty(h.Block.Calls);
        h.Advance(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void AnOperationInFlightHoldsTheRuleOffAndTheWaitStartsAgainAfterIt()
    {
        using CoordinatorHarness h = InUse();
        h.Publish(Devices.Idle(2));
        h.Advance(TimeSpan.FromSeconds(10));

        // A protection change through the gate: the endpoints churn while it runs.
        var applying = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => applying.Task;
        Task<ControllerResult> change = h.Coordinator.SetProtectionAsync(true, CardPlace.NearCursor);
        h.Pump();

        Assert.IsFalse(h.Coordinator.IdleWaitRunning, "The wait must stop while Earshot is changing the device.");
        h.Publish(Devices.NotPresent(3));
        h.Publish(Devices.Idle(4));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);
        Assert.IsEmpty(h.Block.Calls, "A block was issued while a protection change was in flight.");

        applying.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();
        Assert.IsTrue(change.IsCompleted);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The wait did not start again after the change.");

        h.Advance(JustUnderGrace);
        Assert.IsEmpty(h.Block.Calls);
        h.Advance(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void NothingIsBlockedWhenTheEndpointsCouldNotBeRead()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Unreadable(2));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }

    [TestMethod]
    public void NothingIsBlockedWhenTheNodeStateCouldNotBeRead()
    {
        using CoordinatorHarness h = InUse();
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "block-status failed ERROR_FILE_NOT_FOUND (0x80070002)"));
    }

    [TestMethod]
    public void NothingIsBlockedWhenTheNodesAreNotKnownToBeEnabled()
    {
        using CoordinatorHarness h = InUse();
        h.Block.Status = Statuses.Unknown();

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void NothingIsBlockedWithBlockAtBootOff()
    {
        using CoordinatorHarness h = InUse(blockAtBoot: false);

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }

    [TestMethod]
    public void AnAutomaticBlockThatDoesNotTakeIsShownOnceAndNotRunAgainUntilTheAirPodsAreUsed()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail("Could not block the AirPods. Try again.",
            [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        Assert.HasCount(1, h.Block.Calls);
        Assert.HasCount(1, h.Cards.Shown);
        Assert.AreEqual("Could not block the AirPods. Try again.", h.Cards.Shown[0].Content.Status);
        Assert.AreEqual(CardAnchor.NearTray, h.Cards.Shown[0].Anchor);

        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);
        Assert.HasCount(1, h.Block.Calls, "The failing block was run again.");

        // Once they are in use and idle again, the rule tries once more.
        h.Publish(Devices.Active(3));
        h.Publish(Devices.Idle(4));
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.HasCount(2, h.Block.Calls);
    }

    [TestMethod]
    public void TheRuleIsOffWhileEarshotIsClosing()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Idle(2));
        h.Coordinator.BeginShutdown();
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }
}
