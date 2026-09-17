using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Disconnect with Block at boot on is the KS request and then the block, whatever the request returned: the
// block is the at-rest action, and whether the request reached the driver is not what the invariant rests on.
[TestClass]
public sealed class DisconnectSequenceTests
{
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] Disconnected = ["Disconnected"];
    private static readonly string[] DidNotDisconnect = [ConnectMessages.DidNotDisconnect];

    private static CoordinatorHarness Connected(bool blockAtBoot = true)
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        return h;
    }

    [TestMethod]
    public void ADisconnectBlocksTheNodesAfterTheRequest()
    {
        using CoordinatorHarness h = Connected();

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.AreEqual("Disconnected", report.UserMessage);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        CollectionAssert.AreEqual(Disconnected, h.Cards.Statuses);
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
    }

    [TestMethod]
    public void ADisconnectBlocksTheNodesEvenWhenNoFilterTookTheRequest()
    {
        using CoordinatorHarness h = Connected();
        h.Connection.OnDisconnect = _ => Task.FromResult(NoFilterTookIt());

        // Assumed here, not verified on the device: disabling the nodes drops the active link.
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;

        ToggleReport report = h.Toggle(connect: false);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(OpStatus.Success, report.Status, "The block dropped the link, which the endpoints showed.");
        CollectionAssert.AreEqual(Disconnected, h.Cards.Statuses);
        h.AssertAtRest();
    }

    [TestMethod]
    public void ALinkThatStaysUpAfterTheBlockIsReportedAsNotDisconnected()
    {
        using CoordinatorHarness h = Connected();
        h.Connection.OnDisconnect = _ => Task.FromResult(NoFilterTookIt());

        // The other case: the disable is kept for the next start, but the link stays up now.
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;

        ToggleReport report = h.Toggle(connect: false);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(OpStatus.Failed, report.Status, "Nothing showed the AirPods disconnected.");
        Assert.AreEqual(BlockCoordinator.CouldNotReachDriverMessage, report.UserMessage);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State, "The block still holds for the next start.");
        h.AssertAtRest();
    }

    [TestMethod]
    public void ABlockThatThrowsAfterADisconnectIsReportedAsABlockFailure()
    {
        using CoordinatorHarness h = Connected();
        h.Block.OnBlock = _ => throw new InvalidOperationException("The gate could not be started.");

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Partial, report.Status);
        Assert.AreEqual(BlockCoordinator.CouldNotBlockMessage, report.UserMessage, "The block failed; the status read did not.");
        Assert.IsTrue(report.Steps.Any(s => s.Step == "disconnect-block" && !s.Ok));
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "disconnect: the block failed"));
        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "The idle rule takes over the nodes that are still enabled.");
    }

    [TestMethod]
    public void ADisconnectWhoseBlockCountsANodeThatIsNotPresentIsAStraightSuccess()
    {
        using CoordinatorHarness h = Connected();
        h.Block.OnBlock = _ =>
        {
            h.Block.Status = Statuses.Blocked();
            h.Monitor.Publish(Devices.NotPresent(3));
            return Task.FromResult(new ControllerResult(OpStatus.Partial,
                "Connect the AirPods to this PC once from Windows Bluetooth settings, so Earshot can block them.", []));
        };

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(Disconnected, h.Cards.Statuses);
    }

    [TestMethod]
    public void ADisconnectThatIsNotObservedIsReportedHonestly()
    {
        using CoordinatorHarness h = Connected();

        // Nothing changes: the request is not taken and the block leaves the nodes as they were.
        h.Connection.OnDisconnect = _ => Task.FromResult(Results.DidNotDisconnect());
        h.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail("Could not block the AirPods. Try again.",
            [StepOutcomes.FromConfigRet("cm-disable", 0x28)]));

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(ConnectMessages.DidNotDisconnect, report.UserMessage);
        CollectionAssert.AreEqual(DidNotDisconnect, h.Cards.Statuses);
    }

    [TestMethod]
    public void ADisconnectWhoseBlockFailsIsPartial()
    {
        using CoordinatorHarness h = Connected();
        h.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail("Could not block the AirPods. Try again.",
            [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Partial, report.Status);
        Assert.AreEqual("Could not block the AirPods. Try again.", report.UserMessage);
        Assert.AreEqual("Could not block the AirPods. Try again.", h.Cards.Statuses[^1]);
    }

    [TestMethod]
    public void WithBlockAtBootOffAFailedDisconnectIsReportedAndNothingIsBlocked()
    {
        using CoordinatorHarness h = Connected(blockAtBoot: false);
        h.Connection.OnDisconnect = _ => Task.FromResult(new ConnectResult(
            ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.FromHResult("ks-disconnect:src", unchecked((int)0x80004005), "a2dp: adapter")]));

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.CouldNotReachDriverMessage, report.UserMessage);
        Assert.IsEmpty(h.Block.Calls, "Block at boot is off, so there is no other way to try.");
        CollectionAssert.DoesNotContain(h.Cards.Statuses, ConnectMessages.CouldNotReachDriver, "A card promised another way that was not taken.");
    }

    [TestMethod]
    public void WithBlockAtBootOffADisconnectThatWorksIsReported()
    {
        using CoordinatorHarness h = Connected(blockAtBoot: false);

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsEmpty(h.Block.Calls);
        CollectionAssert.AreEqual(Disconnected, h.Cards.Statuses);
    }

    [TestMethod]
    public void NothingIsBlockedWhenTheNodeStateCannotBeRead()
    {
        using CoordinatorHarness h = Connected();
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));

        ToggleReport report = h.Toggle(connect: false);

        Assert.IsEmpty(h.Block.Calls);
        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "the boot block status could not be read"));
    }

    [TestMethod]
    public void ACancelledDisconnectStillBlocks()
    {
        using CoordinatorHarness h = Connected();
        h.Connection.OnDisconnect = token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetCanceled(token));
            return waiting.Task;
        };

        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false), cancel.Token);
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);

        cancel.Cancel();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The disconnect was cancelled, but the at-rest block still ran.");
        h.AssertAtRest();
    }

    // The connection controller throws ConnectCancelledException once the walk to the filters has begun, carrying
    // what went to which filter. The report keeps those steps ahead of the block that follows.
    [TestMethod]
    public void ADisconnectCancelledDuringTheWalkKeepsItsFilterStepsAndStillBlocks()
    {
        using CoordinatorHarness h = Connected();
        StepOutcome sent = StepOutcomes.FromHResult("ks-disconnect:src", 0, "a2dp: adapter");
        StepOutcome notSent = StepOutcomes.NotAttempted("ks-disconnect:wave", "hands-free: adapter: no request was sent because the request was cancelled before this filter.");
        h.Connection.OnDisconnect = token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetException(new ConnectCancelledException([sent, notSent], null, token)));
            return waiting.Task;
        };

        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false), cancel.Token);
        h.Pump();
        cancel.Cancel();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.IsTrue(report.Cancelled);
        Assert.AreSame(sent, report.Steps[0], "The filter steps of the cancelled walk were lost.");
        Assert.AreSame(notSent, report.Steps[1]);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        h.AssertAtRest();
    }

    private static ConnectResult NoFilterTookIt() => new(
        ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
        [StepOutcomes.FromHResult("ks-disconnect:src", unchecked((int)0x80004005), "a2dp: adapter")]);
}
