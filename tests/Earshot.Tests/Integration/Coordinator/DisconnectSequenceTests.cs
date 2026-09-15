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
        h.Connection.OnDisconnect = _ => Task.FromResult(new ConnectResult(
            ConnectOutcome.NoFiltersResponded, ConnectMessages.CouldNotReachDriver,
            [StepOutcomes.FromHResult("ks-disconnect:src", unchecked((int)0x80004005), "a2dp: adapter")]));

        ToggleReport report = h.Toggle(connect: false);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(OpStatus.Success, report.Status, "The block dropped the link, which the endpoints showed.");
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

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false), cancel.Token);
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);

        cancel.Cancel();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The disconnect was cancelled, but the at-rest block still ran.");
    }
}
