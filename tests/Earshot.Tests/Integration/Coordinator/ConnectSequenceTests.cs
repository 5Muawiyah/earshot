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
    public void AConnectThatTimesOutAfterAnAllowBlocksAgainBeforeItReports()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(ConnectMessages.StillConnecting, report.UserMessage);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
        Assert.AreEqual(ConnectMessages.StillConnecting, h.Cards.Statuses[^1]);
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
