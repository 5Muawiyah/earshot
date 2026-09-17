using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The rule that keeps the at-rest invariant without any click: once the AirPods have not been in use for the
// grace period, with nothing in flight and a good read of both the endpoints and the nodes, the nodes go back to
// blocked. Anything unknown, and anything Earshot is doing, holds it off. A block that does not take is tried
// again after a longer wait, and Exit blocks what the rule would have.
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
    public void OneNodeReadThatFailsAsTheAirPodsLeaveIsReadAgainAndTheNodesAreBlocked()
    {
        using CoordinatorHarness h = InUse();
        h.Block.StatusFailuresLeft = 1;

        h.Publish(Devices.Idle(2));
        Assert.IsFalse(h.Coordinator.IdleWaitRunning, "The idle rule acted on a read that failed.");
        Assert.IsTrue(h.Coordinator.RecheckRunning, "Nothing reads the nodes again.");

        // No notification arrives: the AirPods are on the phone.
        h.Advance(BlockCoordinator.RecheckDelay);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);
        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.IsFalse(h.Coordinator.RecheckRunning);
    }

    [TestMethod]
    public void OneUnknownNodeReadAsTheAirPodsLeaveIsReadAgainAndTheNodesAreBlocked()
    {
        using CoordinatorHarness h = InUse();
        h.Block.NextReadStates.Enqueue(BlockState.Unknown);

        h.Publish(Devices.Idle(2));
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);

        h.Advance(BlockCoordinator.RecheckDelay + BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void OneDeviceReadThatFailsIsReadAgainWithoutANotification()
    {
        using CoordinatorHarness h = InUse();
        h.Publish(Devices.Idle(2));
        h.Publish(Devices.Unreadable(3));
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);

        // The next enumeration works, but nothing has changed, so the monitor raises nothing.
        h.Monitor.Set(Devices.Idle(4));
        h.Advance(BlockCoordinator.RecheckDelay + BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void AReadThatKeepsFailingIsTriedLessOftenAndNothingIsBlockedUntilItWorks()
    {
        using CoordinatorHarness h = InUse();
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));
        h.Publish(Devices.Idle(2));
        int before = h.Block.StatusReads;

        h.Advance(TimeSpan.FromHours(2));

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Coordinator.RecheckRunning);
        int reads = h.Block.StatusReads - before;
        Assert.IsGreaterThanOrEqualTo(6, reads, "The state was not read again.");
        Assert.IsLessThan(20, reads, "A read that keeps failing was retried every " + BlockCoordinator.RecheckDelay + ".");

        h.Block.StatusFailure = null;
        h.Advance(BlockCoordinator.IdleRetryLimit + BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void ABlockWhoseResultCountsANodeThatIsNotPresentHasTakenWhenTheNodesReadBlocked()
    {
        using CoordinatorHarness h = InUse();

        // A phantom node of a service protection removed makes the result Partial, though every present node is blocked.
        h.Block.OnBlock = _ =>
        {
            h.Block.Status = Statuses.Blocked();
            h.Monitor.Publish(Devices.NotPresent(3));
            return Task.FromResult(new ControllerResult(OpStatus.Partial, NotPresentBlockMessage, []));
        };

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(0, h.Coordinator.IdleFailures);
        Assert.IsEmpty(h.Cards.Shown, "A block that held was shown as a failure.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "but the nodes read Blocked, so it took"));
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

    // Nothing can block before setup, so the state is not read again every RecheckDelay for as long as the tray
    // runs, even while the endpoints cannot be read and the start-up check has not run. The read after setup
    // evaluates the rule afresh.
    [TestMethod]
    public void BeforeSetUpTheStateIsNotReadAgainAndAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.NotSetUp();
        h.Monitor.Set(Devices.Unreadable(1));
        h.Start();
        int reads = h.Block.StatusReads;

        Assert.IsFalse(h.Coordinator.RecheckRunning);
        h.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual(reads, h.Block.StatusReads, "The state was read again although nothing could block.");
        Assert.IsEmpty(h.Block.Calls);

        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(2));
        h.Publish(Devices.Idle(3));

        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "Once set up, enabled nodes that are not in use are blocked after the grace period.");
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
    public void AnAutomaticBlockThatDoesNotTakeIsShownOnceAndTriedAgainAfterALongerWait()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => Task.FromResult(Busy());

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        Assert.HasCount(1, h.Block.Calls);
        Assert.HasCount(1, h.Cards.Shown);
        Assert.AreEqual(BusyMessage, h.Cards.Shown[0].Content.Status);
        Assert.AreEqual(CardAnchor.NearTray, h.Cards.Shown[0].Anchor);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The rule must keep trying while the nodes are enabled.");

        // The second try waits twice the grace period.
        h.Advance(BlockCoordinator.IdleGrace + JustUnderGrace);
        Assert.HasCount(1, h.Block.Calls);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.HasCount(2, h.Block.Calls);
        Assert.HasCount(1, h.Cards.Shown, "The failure is shown once, not at every try.");

        // The gate works again: the next try blocks, with nothing else happening in between.
        h.Block.OnBlock = null;
        h.Advance(BlockCoordinator.IdleRetryLimit);

        Assert.HasCount(3, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.AreEqual(0, h.Coordinator.IdleFailures);
        h.AssertAtRest();
    }

    [TestMethod]
    public void AGateThatKeepsFailingIsTriedAgainOnceInEveryRetryLimit()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => Task.FromResult(Busy());
        h.Publish(Devices.Idle(2));

        // The waits double from the grace period (30, 60, 120, 240, 480 s) and then stay at the limit.
        h.Advance(TimeSpan.FromHours(2));
        int tries = h.Block.Calls.Count;
        Assert.IsTrue(tries >= 6, "Only " + tries + " tries in two hours.");

        for (int round = 1; round <= 6; round++)
        {
            h.Advance(BlockCoordinator.IdleRetryLimit);
            Assert.HasCount(tries + round, h.Block.Calls, "Round " + round + " did not try again.");
            Assert.IsTrue(h.Coordinator.IdleWaitRunning);
        }

        Assert.HasCount(1, h.Cards.Shown, "The failure is shown once.");
    }

    [TestMethod]
    public void UsingTheAirPodsOrAChangedNodeReadStartsTheRetryAtTheGracePeriodAgain()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => Task.FromResult(Busy());
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.AreEqual(1, h.Coordinator.IdleFailures);

        // In use and out of use again: the grace period, not the longer wait, and the failure may be shown again.
        h.Publish(Devices.Active(3));
        h.Publish(Devices.Idle(4));
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.HasCount(2, h.Block.Calls);
        Assert.HasCount(2, h.Cards.Shown);

        // Block at boot goes off and on again: the rule starts from the grace period.
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
        h.Block.Status = Statuses.Allowed(blockAtBoot: true);
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();
        Assert.AreEqual(0, h.Coordinator.IdleFailures);
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.HasCount(3, h.Block.Calls);
    }

    [TestMethod]
    public void AnIdleBlockThatThrowsIsRecordedShownAndTriedAgain()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => throw new IOException("The task scheduler is not running.", unchecked((int)0x80041315));

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        Assert.IsTrue(h.Log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("idle-block failed", StringComparison.Ordinal) &&
                                             e.Message.Contains("(0x80041315)", StringComparison.Ordinal)),
            "The exception was not recorded as a step with its code.");
        Assert.HasCount(1, h.Cards.Shown);
        Assert.AreEqual(BlockCoordinator.CouldNotBlockMessage, h.Cards.Shown[0].Content.Status);
        Assert.AreEqual(CardAnchor.NearTray, h.Cards.Shown[0].Anchor);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);

        h.Block.OnBlock = null;
        h.Advance(BlockCoordinator.IdleRetryLimit);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void AFailureCardHeldBackByWindowsIsTriedAgainAtTheNextFailure()
    {
        using CoordinatorHarness h = InUse();
        h.Block.OnBlock = _ => Task.FromResult(Busy());
        h.Cards.HoldBackNearTray = true;

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.HasCount(1, h.Cards.HeldBack);
        Assert.IsEmpty(h.Cards.Shown);

        h.Cards.HoldBackNearTray = false;
        h.Advance(BlockCoordinator.IdleRetryLimit);

        Assert.HasCount(1, h.Cards.Shown, "The failure was never shown.");
        Assert.AreEqual(BusyMessage, h.Cards.Shown[0].Content.Status);
    }

    [TestMethod]
    public void ExitDuringTheGracePeriodBlocksBeforeClosing()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Idle(2));
        h.Advance(TimeSpan.FromSeconds(5));
        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "Exit left enabled nodes that were not in use.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
        Assert.IsNull(h.Coordinator.ClosingNotice);
        Assert.IsEmpty(h.Cards.Shown);

        h.Advance(BlockCoordinator.IdleRetryLimit);
        Assert.HasCount(1, h.Block.Calls, "Nothing runs after closing.");
    }

    [TestMethod]
    public void ExitWhileTheAirPodsAreInUseLeavesTheNodesEnabledAndSaysSo()
    {
        using CoordinatorHarness h = InUse();

        h.Coordinator.BeginShutdown();
        h.Pump();

        Assert.IsEmpty(h.Block.Calls, "The AirPods are in use, so the nodes stay enabled.");
        Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, h.Coordinator.ClosingNotice);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Closing while the AirPods are in use"));
    }

    [TestMethod]
    public void ExitWithABlockThatDoesNotTakeSaysSo()
    {
        using CoordinatorHarness h = InUse();
        h.Publish(Devices.Idle(2));
        h.Block.OnBlock = _ => Task.FromResult(Busy());

        // Earshot ends here with the nodes enabled; what is left is to say so.
        h.CheckInvariantOnPump = false;
        h.Coordinator.BeginShutdown();
        h.Pump();

        Assert.HasCount(1, h.Block.Calls);
        Assert.AreEqual(BusyMessage, h.Coordinator.ClosingNotice);
    }

    [TestMethod]
    public void ExitWithNothingToBlockQueuesNothing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();

        h.Coordinator.BeginShutdown();

        Assert.IsFalse(h.Coordinator.IsBusy);
        h.Pump();
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "no block before closing, because the nodes are already blocked"));
    }

    [TestMethod]
    public void AnIdleBlockReadsTheDeviceFirstAndHoldsOffForUseNoNotificationHasReportedYet()
    {
        using CoordinatorHarness h = InUse();
        h.Publish(Devices.Idle(2));
        h.Advance(JustUnderGrace);

        // Back in use, but the notification is still on its way.
        h.Monitor.Set(Devices.Active(3));
        h.Advance(TimeSpan.FromSeconds(1));

        Assert.IsEmpty(h.Block.Calls, "The idle block acted on a snapshot the device no longer showed.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "no block sent, because the AirPods are in use"));
    }

    [TestMethod]
    public void WithChangesNotWatchedTheAirPodsAreReadOnATimerAndBlockedOnceOutOfUse()
    {
        using var h = new CoordinatorHarness();
        h.Monitor.WatchFailed = true;
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        Assert.IsTrue(h.Coordinator.RecheckRunning, "In use settles nothing while no notification can say when it ends.");
        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == BlockCoordinator.ChangesNotWatchedMessage));

        // Out of use, and no notification says so.
        h.Monitor.Set(Devices.Idle(2));
        h.Advance(BlockCoordinator.RecheckDelay);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The read on the timer did not see the AirPods leave.");

        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(1, h.Cards.Statuses.Count(s => s == BlockCoordinator.ChangesNotWatchedMessage), "The card is shown once.");
    }

    [TestMethod]
    public void WithChangesNotWatchedTheReadsInUseNeverWaitLongerThanTheirLimit()
    {
        using var h = new CoordinatorHarness();
        h.Monitor.WatchFailed = true;
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        // In use for a long listen: the reads go on, however long it lasts.
        for (int i = 0; i < 10; i++)
        {
            h.Advance(BlockCoordinator.UnwatchedRecheckLimit);
            Assert.IsTrue(h.Coordinator.RecheckRunning);
        }

        Assert.IsEmpty(h.Block.Calls);
        h.Monitor.Set(Devices.Idle(2));
        h.Advance(BlockCoordinator.UnwatchedRecheckLimit + BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The AirPods leaving was not acted on within the read limit and the grace period.");
    }

    [TestMethod]
    public void ABlockAtBootSettingThatCouldNotBeReadBlocksNothingAndIsReadAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false) with { BlockAtBootKnown = false };
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        Assert.IsEmpty(h.Block.Calls, "The start-up check acted on a setting it never read.");
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
        Assert.IsTrue(h.Coordinator.RecheckRunning, "Nothing reads the setting again.");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because Block at boot could not be read."));

        // The setting reads again, and the idle rule acts on it.
        h.Block.Status = Statuses.Allowed();
        h.Advance(BlockCoordinator.RecheckDelay);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);

        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void ClosingWithoutExitIssuesNoBlock()
    {
        using CoordinatorHarness h = InUse();

        h.Publish(Devices.Idle(2));
        h.Coordinator.Dispose();
        h.CheckInvariantOnPump = false;
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }

    private const string BusyMessage = "Another change to the AirPods is still running. Try again.";
    private const string NotPresentBlockMessage = "Connect the AirPods to this PC once from Windows Bluetooth settings, then try Block again.";

    private static ControllerResult Busy() =>
        ControllerResult.Fail(BusyMessage, [StepOutcomes.FromWin32("device-change-lock", 32)]);
}
