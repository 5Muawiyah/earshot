using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// A driver call that never returns keeps the audio worker busy, and every refresh queued behind it waits too. The
// coordinator's own waits on a refresh are bounded, so a stalled driver can never hold an operation in flight for
// good: the re-block after the coordinator's own allow is still sent, and Earshot can still close.
[TestClass]
public sealed class StalledDriverTests
{
    private static readonly string[] Allow = ["allow"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];
    private static readonly string[] BlockOnly = ["block"];

    // What ConnectionController returns once its pass budget has run out with the driver call still running.
    private static ConnectResult DriverStalled() =>
        new(ConnectOutcome.AttemptedTimedOut, ConnectMessages.StillConnecting,
            [StepOutcomes.NotAttempted(ConnectionController.StalledStep, "The audio driver did not answer within 20 s, so whether the request went out is not known.")]);

    // Blocked at rest, then a connect whose allow works and whose connect after it stalls in the driver.
    private static CoordinatorHarness BlockedWithAStallAfterTheAllow()
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Monitor.RefreshStalls = true;
            return Task.FromResult(DriverStalled());
        });
        return h;
    }

    [TestMethod]
    public void AConnectWhoseDriverCallStallsAfterAnAllowStillBlocksAgainAndEnds()
    {
        using CoordinatorHarness h = BlockedWithAStallAfterTheAllow();

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsFalse(toggle.IsCompleted, "The clean-up did not wait for its refresh at all.");
        CollectionAssert.AreEqual(Allow, h.Block.Calls);

        h.Advance(BlockCoordinator.RefreshBudget);

        Assert.IsTrue(toggle.IsCompleted, "The connect never ended while the audio worker was stuck.");
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The coordinator's own allow was not undone.");
        Assert.AreEqual(BlockCoordinator.DidNotConnectMessage, report.UserMessage);
        Assert.IsFalse(h.Coordinator.IsBusy);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "was not refreshed within"));
        h.AssertAtRest();
    }

    [TestMethod]
    public void AClickAfterAStalledConnectIsNotHeldBehindIt()
    {
        using CoordinatorHarness h = BlockedWithAStallAfterTheAllow();
        Task<ToggleReport> first = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        // The next click waits for the first, which ends once its refresh budget has run out.
        h.Monitor.RefreshStalls = false;
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        Task<ToggleReport> second = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(second.IsCompleted);

        h.Advance(BlockCoordinator.RefreshBudget);

        Assert.IsTrue(first.IsCompleted);
        Assert.IsTrue(second.IsCompleted, "A later click waited for good behind a stalled driver call.");
        Assert.AreEqual(OpStatus.Success, second.GetAwaiter().GetResult().Status);
    }

    [TestMethod]
    public void ExitWhileTheCleanUpWaitsOnAStalledRefreshBlocksAndCloses()
    {
        using CoordinatorHarness h = BlockedWithAStallAfterTheAllow();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();
        Assert.IsFalse(idle.IsCompleted);

        h.Advance(BlockCoordinator.RefreshBudget);

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(idle.IsCompleted, "Earshot could not close while the audio worker was stuck.");
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The clean-up blocks, and the block before closing finds nothing left to do.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ExitWithAStalledRefreshClosesWithoutBlockingOnAnUnknownState()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        // Out of use with no notification, and the audio worker is stuck: nothing can read that.
        h.CheckInvariantOnPump = false;
        h.Monitor.Set(Devices.Idle(2));
        h.Monitor.RefreshStalls = true;

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();
        Assert.IsFalse(idle.IsCompleted);

        h.Advance(BlockCoordinator.RefreshBudget);

        Assert.IsTrue(idle.IsCompleted, "Earshot could not close while the audio worker was stuck.");
        Assert.IsEmpty(h.Block.Calls, "A device state that could not be read never leads to a block.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Closing: the nodes are not blocked, because the device state could not be read."));
    }

    // A boot block status read calls Task Scheduler COM and CfgMgr32 on the thread pool. One that never returns is a
    // read that failed once its budget has run out: the disconnect ends, nothing is blocked on a state nobody read,
    // and the state is read again later.
    [TestMethod]
    public void AStatusReadThatNeverReturnsEndsTheDisconnectAsAReadThatFailed()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Block.StatusStalls = true;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted, "The disconnect did not wait for its status read at all.");

        h.Advance(BlockCoordinator.StatusReadBudget);

        Assert.IsTrue(toggle.IsCompleted, "The disconnect never ended while the status read did not return.");
        Assert.AreEqual(OpStatus.Success, toggle.GetAwaiter().GetResult().Status);
        Assert.IsEmpty(h.Block.Calls, "A status that was never read led to a block.");
        Assert.IsFalse(h.Coordinator.IsBusy);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "The boot block status was not read within"));

        // The read works again: the nodes are read again and blocked.
        h.Block.StatusStalls = false;
        h.Advance(BlockCoordinator.IdleRetryLimit + BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void ExitWithAStatusReadThatNeverReturnsClosesWithinTheReadBudgets()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Monitor.Set(Devices.Idle(2));
        h.Block.StatusStalls = true;

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();
        Assert.IsFalse(idle.IsCompleted);

        h.Advance(BlockCoordinator.StatusReadBudget);

        Assert.IsTrue(idle.IsCompleted, "Earshot could not close while a status read did not return.");
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Closing: the nodes are not blocked, because the boot block status could not be read."));
    }

    [TestMethod]
    public void AServiceReadThatNeverReturnsDoesNotHoldTheBlockAfterADisconnect()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Protection.StatusStalls = true;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);

        h.Advance(BlockCoordinator.StatusReadBudget);

        Assert.IsTrue(toggle.IsCompleted, "The block after the disconnect waited for good on a service read.");
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "A protection read that failed never holds up the block.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "The audio protection status was not read within"));
    }

    [TestMethod]
    public void TheWaitForEndpointsAfterAnAllowEndsAtItsTimeoutWhenTheRefreshStalls()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = Statuses.Allowed();
            h.Monitor.RefreshStalls = true;
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        h.Advance(BlockCoordinator.EndpointWait);

        Assert.IsFalse(toggle.IsCompleted, "The clean-up refresh is bounded too, but has not run out yet.");
        Assert.HasCount(1, h.Connection.Calls, "Nothing is sent without a render endpoint.");

        h.Advance(BlockCoordinator.RefreshBudget);

        Assert.IsTrue(toggle.IsCompleted, "The wait for the endpoints outlived its own timeout.");
        Assert.AreEqual(BlockCoordinator.DidNotComeBackMessage, toggle.GetAwaiter().GetResult().UserMessage);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
    }

    [TestMethod]
    public void ANotificationEndsTheWaitForEndpointsWhileItsRefreshStalls()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = Statuses.Allowed();
            h.Monitor.RefreshStalls = true;
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);

        h.Monitor.RefreshStalls = false;
        h.Publish(Devices.Idle(5));

        Assert.IsTrue(toggle.IsCompleted, "The wait held on to its stalled refresh after the endpoints came back.");
        Assert.AreEqual(OpStatus.Success, toggle.GetAwaiter().GetResult().Status);
        Assert.HasCount(2, h.Connection.Calls);
        CollectionAssert.AreEqual(Allow, h.Block.Calls);
    }
}
