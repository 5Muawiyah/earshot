using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The at-rest invariant over every connect path: an allow issued for a connect ends in render ACTIVE or in the
// nodes blocked again (with Block at boot on), whatever the connect returned and however it ended.
[TestClass]
public sealed class ConnectSequenceTests
{
    private static readonly string[] Allow = ["allow"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] ConnectingThenConnected = ["Connecting", "Connected"];
    private static readonly string[] ConnectingAllowingConnected = ["Connecting", BlockCoordinator.AllowingStatus, "Connected"];
    private static readonly string[] SetDevice = ["set-device:" + Devices.Address];
    private static readonly string[] SafeModeRefusal = ["Safe mode: no device actions."];

    [TestMethod]
    public void AConnectThatReachesActiveLeavesTheNodesAlone()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.AreEqual(ConnectMessages.Connected, report.UserMessage);
        Assert.HasCount(1, h.Connection.Calls);
        Assert.IsEmpty(h.Block.Calls, "Nothing was blocked or allowed for a connect that worked.");
        CollectionAssert.AreEqual(ConnectingThenConnected, h.Cards.Statuses);
    }

    [TestMethod]
    public void AConnectWhileTheNodesAreBlockedAllowsWaitsForTheEndpointsAndConnects()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(Allow, h.Block.Calls);
        Assert.HasCount(2, h.Connection.Calls);
        CollectionAssert.AreEqual(ConnectingAllowingConnected, h.Cards.Statuses);
        Assert.AreEqual(BlockState.Allowed, h.Coordinator.BlockStatus?.State);
    }

    [TestMethod]
    public void AConnectThatTimesOutAfterAnAllowBlocksAgainAndDoesNotSayItIsStillConnecting()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);

        // The nodes are disabled again, so nothing can still be connecting.
        Assert.AreEqual(BlockCoordinator.DidNotConnectMessage, report.UserMessage);
        Assert.AreEqual(BlockCoordinator.DidNotConnectMessage, h.Cards.Statuses[^1]);
        CollectionAssert.DoesNotContain(h.Cards.Statuses, ConnectMessages.StillConnecting);
    }

    [TestMethod]
    public void AConnectThatTimesOutWithNothingPutBackMayStillConnect()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Publish(Devices.Idle(2));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport report = h.Toggle(connect: true);

        // Nothing was allowed, so the connect blocks nothing, and the request may still be taken.
        Assert.AreEqual(ConnectMessages.StillConnecting, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The idle rule still blocks the nodes if it never connects.");
    }

    [TestMethod]
    public void AConnectFoundInUseByItsCleanUpIsReportedConnected()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // The confirmation wait runs out just before the AirPods reach ACTIVE, with no notification yet.
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Monitor.Set(Devices.Active(60));
            return Task.FromResult(Results.TimedOut());
        });

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.AreEqual(ConnectMessages.Connected, h.Cards.Statuses[^1]);
        CollectionAssert.AreEqual(Allow, h.Block.Calls, "The AirPods are in use, so the nodes stay enabled.");
    }

    [TestMethod]
    public void TheAllowIsWaitedForWhenTheConnectIsCancelledAndThenBlockedAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // The gate takes a while to enable the nodes, and has already been started when the click is cancelled.
        var allowing = new TaskCompletionSource<ControllerResult>();
        h.Block.OnAllow = _ => allowing.Task;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        cancel.Cancel();
        h.Pump();

        Assert.IsFalse(toggle.IsCompleted, "The connect ended before the allow it sent.");
        CollectionAssert.AreEqual(Allow, h.Block.Calls, "A node read before the allow ends still says Blocked, so nothing is blocked yet.");
        Assert.IsEmpty(h.Block.CancellableChanges, "A gate change was given a token that stops the wait for it.");

        // The allow lands.
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Publish(Devices.Idle(5));
        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void AnAllowWhoseEndWasNotSeenIsReadAgainAndBlockedOnceItLands()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Block.OnAllow = _ => Task.FromResult(Results.GateWaitRanOut("Gate", "The boot block did not finish in time. Try again."));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(Allow, h.Block.Calls, "The nodes read Blocked after the allow, so there is nothing to block yet.");
        Assert.IsTrue(h.Coordinator.AllowMayLand);
        Assert.IsTrue(h.Coordinator.RecheckRunning, "Nothing reads the nodes again for an allow that may still land.");

        // The pin does not move under an allow that may still enable the nodes of the device pinned now.
        Task<ControllerResult> pin = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Pump();
        Assert.AreEqual(BlockCoordinator.ChangeStillRunningMessage, pin.GetAwaiter().GetResult().UserMessage);
        CollectionAssert.AreEqual(Allow, h.Block.Calls);

        // The gate enables the nodes a little later, with no notification for the tray.
        h.Advance(TimeSpan.FromSeconds(20));
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(7));
        h.Advance(BlockCoordinator.RecheckDelay);
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The read after the allow landed did not start the idle rule.");
        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);

        // Once the window has passed, the device can be changed again and nothing is read again.
        h.Advance(BlockCoordinator.UnsettledGateWindow);
        Assert.IsFalse(h.Coordinator.RecheckRunning);
        pin = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Pump();
        Assert.AreEqual(OpStatus.Success, pin.GetAwaiter().GetResult().Status);
    }

    [TestMethod]
    public void TheConnectingCardIsShownOnlyOnceTheConnectRuns()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        // An idle block is running through the gate when the click comes.
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        Assert.IsEmpty(h.Cards.Shown, "A Connecting card went up for a connect that is still waiting.");

        // Another device is chosen before the connect's turn: it never runs, and no card claimed it would.
        cancel.Cancel();
        h.Pump();
        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        Assert.IsEmpty(h.Connection.Calls);
        Assert.IsEmpty(h.Cards.Shown);

        h.Block.Status = Statuses.Blocked();
        h.Monitor.Publish(Devices.NotPresent(3));
        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();
    }

    [TestMethod]
    public void AConnectWhoseFiltersAllFailAfterAnAllowBlocksAgainAndNeverPromisesAnotherWay()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // The A2DP filter turned it down, but protection is off, so there is no other way to try.
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.CouldNotReachDriverMessage, report.UserMessage);
        CollectionAssert.DoesNotContain(h.Cards.Statuses, ConnectMessages.CouldNotReachDriver, "A card promised another way that was not taken.");
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.IsEmpty(h.Protection.Applies);
    }

    [TestMethod]
    public void AnAllowThatFailsBlocksAgainAndReportsTheAllowMessage()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // Only part of the device came back, so the nodes must go back to blocked.
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = h.Block.Status with { State = BlockState.Mixed };
            return Task.FromResult(ControllerResult.Fail("Only part of the AirPods could be allowed. Try again.",
                [StepOutcomes.FromConfigRet("cm-enable", 0x33)]));
        };

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual("Only part of the AirPods could be allowed. Try again.", report.UserMessage);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.HasCount(1, h.Connection.Calls, "Nothing was sent to a device whose nodes never came back.");
    }

    [TestMethod]
    public void ACancelledConnectBlocksAgainBeforeItsTaskCompletes()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetCanceled(token));
            return waiting.Task;
        });

        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);
        CollectionAssert.AreEqual(Allow, h.Block.Calls);

        cancel.Cancel();
        h.Pump();

        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The cancelled connect did not block again.");
        Assert.IsFalse(toggle.IsCompleted, "The task completed before its clean-up had finished.");

        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.IsTrue(report.Cancelled);
    }

    // The connection controller throws ConnectCancelledException once the walk to the filters has begun, carrying
    // what went to which filter. The report keeps those steps, and the allow is still undone.
    [TestMethod]
    public void AConnectCancelledDuringTheWalkKeepsItsFilterStepsAndBlocksAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        StepOutcome sent = StepOutcomes.FromHResult("ks-reconnect:src", 0, "a2dp: adapter");
        StepOutcome notSent = StepOutcomes.NotAttempted("ks-reconnect:wave", "hands-free: adapter: no request was sent because the request was cancelled before this filter.");
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetException(new ConnectCancelledException([sent, notSent], null, token)));
            return waiting.Task;
        });

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        cancel.Cancel();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.IsTrue(report.Cancelled);
        CollectionAssert.Contains(report.Steps.ToList(), sent, "The filter steps of the cancelled walk were lost.");
        CollectionAssert.Contains(report.Steps.ToList(), notSent);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The cancelled connect did not block again.");
    }

    [TestMethod]
    public void AConnectThatSucceedsAfterItWasCancelledLeavesTheNodesEnabled()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() =>
            {
                // The AirPods connected after all, just as the click was cancelled.
                h.Monitor.Publish(Devices.Active(50));
                waiting.TrySetCanceled(token);
            });
            return waiting.Task;
        });

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        cancel.Cancel();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        CollectionAssert.AreEqual(Allow, h.Block.Calls, "The AirPods are in use, so the nodes stay enabled.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "in use after all"));
    }

    [TestMethod]
    public void EndpointsThatNeverComeBackAfterAnAllowBlockAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // The allow works, but the endpoints stay NOTPRESENT.
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = h.Block.Status with { State = BlockState.Allowed };
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted, "The connect did not wait for the endpoints.");

        h.Advance(BlockCoordinator.EndpointWait);

        Assert.IsTrue(toggle.IsCompleted);
        ToggleReport timedOut = toggle.GetAwaiter().GetResult();
        Assert.AreEqual(OpStatus.Failed, timedOut.Status);
        Assert.AreEqual(BlockCoordinator.DidNotComeBackMessage, timedOut.UserMessage);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.HasCount(1, h.Connection.Calls);
    }

    [TestMethod]
    public void NothingIsAllowedWhenTheNodesAreNotTheReasonTheEndpointsAreMissing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Unknown();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.NotAvailableMessage, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls);
        CollectionAssert.DoesNotContain(h.Cards.Statuses, ConnectMessages.AllowingFirst, "A card promised an allow that did not happen.");
    }

    [TestMethod]
    public void NothingIsAllowedWhenTheGateStillPinsAnotherDevice()
    {
        using var h = new CoordinatorHarness();

        // The gate blocks another device (a device change it did not take); allowing would enable that one.
        var other = new Guid("0B8E5A51-7F0C-5F5E-9C6B-2D7C1E0F4A11");
        h.Block.Status = Statuses.Blocked() with { TargetContainerId = other };
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.OtherDeviceMessage, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls);
        CollectionAssert.DoesNotContain(h.Cards.Statuses, BlockCoordinator.AllowingStatus);
    }

    [TestMethod]
    public void NothingIsAllowedBeforeTheBootBlockIsSetUp()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.NotSetUp();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(ConnectMessages.BootBlockNotSetUp, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void NothingIsAllowedWhenTheNodeStateCannotBeRead()
    {
        using var h = new CoordinatorHarness();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(BlockCoordinator.BlockStatusUnreadableMessage, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "block-status failed ERROR_FILE_NOT_FOUND (0x80070002)"));
    }

    [TestMethod]
    public void AFailedConnectLeavesTheNodesAloneWhenBlockAtBootIsOff()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked(blockAtBoot: false);
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(Allow, h.Block.Calls, "Block at boot is off, so the nodes are left as the user asked.");
    }

    [TestMethod]
    public void TheCardsOfAClickAreAnchoredAtTheClick()
    {
        using var h = new CoordinatorHarness();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();

        h.Toggle(connect: true, click: new System.Drawing.Point(1830, 1040));

        Assert.IsTrue(h.Cards.Shown.All(c => c.Anchor == CardAnchor.NearCursor && c.ClickPoint == new System.Drawing.Point(1830, 1040)));
        Assert.IsTrue(h.Cards.Shown.All(c => c.Content.Title == Devices.Name));
    }

    [TestMethod]
    public void AConnectThatThrowsAfterAnAllowBlocksAgainBeforeTheErrorIsReported()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ => throw new InvalidOperationException("The walk to the filters failed."));

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsTrue(toggle.IsFaulted, "The error was swallowed.");
        Assert.IsInstanceOfType<InvalidOperationException>(toggle.Exception!.InnerException);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The nodes were left enabled after an error.");
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "connect: unexpected error; cleaning up"));
    }

    [TestMethod]
    public void InSafeModeNoCardPromisesSomethingThatCannotHappen()
    {
        using var h = new CoordinatorHarness(safeMode: true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(new ConnectResult(
            ConnectOutcome.Failed, "Safe mode: no device actions.",
            [StepOutcomes.NotAttempted("safe-mode:connect", "Safe mode: no device actions.")])));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        CollectionAssert.AreEqual(SafeModeRefusal, h.Cards.Statuses, "Safe mode shows the refusal only, not a Connecting card.");
    }

    [TestMethod]
    public void OneOperationRunsAtATime()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        var waiting = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => waiting.Task);

        Task<ToggleReport> first = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Task<ControllerResult> second = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Pump();

        Assert.IsFalse(second.IsCompleted);
        Assert.IsEmpty(h.Block.Calls, "The device change waited for the connect.");

        waiting.SetResult(Results.Connected());
        h.Monitor.Publish(Devices.Active(9));
        h.Pump();

        Assert.IsTrue(first.IsCompleted);
        Assert.IsTrue(second.IsCompleted);
        CollectionAssert.AreEqual(SetDevice, h.Block.Calls);
    }
}
