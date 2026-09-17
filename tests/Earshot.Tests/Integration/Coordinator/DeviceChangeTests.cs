using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Choosing another device while the gate still pins one that is blocked, or whose services Earshot turned off.
// The gate refuses to move the pin then, and with Block at boot on that is the state at rest, so the coordinator
// works through the refusal in ProtectionPolicy's order and puts back what it changed when the pin still does
// not move.
[TestClass]
public sealed class DeviceChangeTests
{
    private const string OtherAddress = "AABBCCDDEEFF";
    private const string FailedMessage = "Could not choose that device. Try again.";

    private static readonly string[] PinOnly = ["set-device"];
    private static readonly string[] AllowThenBlock = ["set-device", "allow", "block"];
    private static readonly string[] AllowThenPin = ["set-device", "allow", "set-device"];
    private static readonly string[] AllowProtectOffThenPin = ["set-device", "allow", "set-device", "protect-off", "set-device"];
    private static readonly string[] PutBackInOrder = ["set-device", "allow", "set-device", "protect-off", "set-device", "protect-on", "block"];

    private static ControllerResult Refused(GateExitCode code, string message) =>
        ControllerResult.Fail(message, [new StepOutcome(BlockController.SetDeviceExitStep, false, (int)code, GateExitCodes.NameOf((int)code)!, null)]);

    // At rest with Block at boot on: the nodes of the device pinned now are blocked.
    private static CoordinatorHarness AtRest(bool protect = false)
    {
        var h = new CoordinatorHarness(protectAudio: protect);
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
        h.Start();
        return h;
    }

    // The gate as it decides: a disabled node first, then services listed in protection.json.
    private static void GateRefusesLikeTheGate(CoordinatorHarness h, Func<ControllerResult>? whenClear = null) =>
        h.Block.OnSetDevice = _ => Task.FromResult(
            h.Block.Status.State is BlockState.Blocked or BlockState.Mixed ? Refused(GateExitCode.OtherDeviceBlocked, BlockController.OtherDeviceBlockedMessage)
            : h.Protection.State == AudioProtectionState.Protected ? Refused(GateExitCode.OtherDeviceProtected, BlockController.OtherDeviceProtectedMessage)
            : whenClear?.Invoke() ?? ControllerResult.Ok(BlockController.DeviceChosenMessage));

    private static ControllerResult Change(CoordinatorHarness h)
    {
        Task<ControllerResult> change = h.Coordinator.ChangeDeviceAsync(OtherAddress);
        h.Pump();
        Assert.IsTrue(change.IsCompleted, "The device change did not finish.");
        return change.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TheBlockedDeviceIsAllowedBeforeThePinMoves()
    {
        using CoordinatorHarness h = AtRest();
        GateRefusesLikeTheGate(h);

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        CollectionAssert.AreEqual(AllowThenPin, h.Trace);
        Assert.IsTrue(result.Steps.Any(s => s.Step == BlockController.SetDeviceExitStep), "The refusal stays on record.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the previous device is left allowed"));
    }

    [TestMethod]
    public void ServicesTurnedOffOnTheOldDeviceAreTurnedBackOnWhileItsNodesAreEnabled()
    {
        using CoordinatorHarness h = AtRest(protect: true);
        GateRefusesLikeTheGate(h);

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Success, result.Status, result.UserMessage);
        CollectionAssert.AreEqual(AllowProtectOffThenPin, h.Trace, "Protection only changes once the nodes are enabled.");
        Assert.AreEqual(AudioProtectionState.NotProtected, h.Protection.State);
    }

    // The pin still does not move (the gate was busy, say): protection goes back on while the nodes are enabled,
    // then the nodes are blocked again, so the device pinned now is back at rest.
    [TestMethod]
    public void APinThatStillDoesNotMovePutsProtectionBackThenBlocks()
    {
        using CoordinatorHarness h = AtRest(protect: true);
        GateRefusesLikeTheGate(h, () => ControllerResult.Fail(FailedMessage, []));

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(FailedMessage, result.UserMessage);
        CollectionAssert.AreEqual(PutBackInOrder, h.Trace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.AreEqual(AudioProtectionState.Protected, h.Protection.State);
    }

    [TestMethod]
    public void WithBlockAtBootOffTheOldDeviceStaysAllowedWhenThePinDoesNotMove()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked(blockAtBoot: false);
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        GateRefusesLikeTheGate(h, () => ControllerResult.Fail(FailedMessage, []));

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        CollectionAssert.AreEqual(AllowThenPin, h.Trace);
    }

    // Nothing is allowed on a read that failed.
    [TestMethod]
    public void ANodeReadThatFailsAllowsNothing()
    {
        using CoordinatorHarness h = AtRest();
        GateRefusesLikeTheGate(h);
        h.Block.StatusFailure = new IOException("The device is not ready.", unchecked((int)0x80070015));

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockCoordinator.BlockStatusUnreadableMessage, result.UserMessage);
        CollectionAssert.AreEqual(PinOnly, h.Trace);
    }

    // A disabled node the gate counts that the read does not show as blocked (not present, still marked disabled)
    // cannot be allowed from here, so the refusal is reported with what works.
    [TestMethod]
    public void ARefusalTheNodeReadDoesNotBearOutAllowsNothing()
    {
        using CoordinatorHarness h = AtRest();
        h.Block.OnSetDevice = _ => Task.FromResult(Refused(GateExitCode.OtherDeviceBlocked, BlockController.OtherDeviceBlockedMessage));
        h.Block.Status = Statuses.Unknown();

        ControllerResult result = Change(h);

        Assert.AreEqual(BlockController.OtherDeviceBlockedMessage, result.UserMessage);
        CollectionAssert.AreEqual(PinOnly, h.Trace);
    }

    [TestMethod]
    public void AnAllowThatFailsIsReportedAndBlockedAgain()
    {
        using CoordinatorHarness h = AtRest();
        GateRefusesLikeTheGate(h);
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = Statuses.Mixed();
            return Task.FromResult(new ControllerResult(OpStatus.Partial, BlockController.PartialAllowMessage, []));
        };

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        Assert.AreEqual(BlockController.PartialAllowMessage, result.UserMessage);
        CollectionAssert.AreEqual(AllowThenBlock, h.Trace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ARefusalNothingHereCanChangeIsReportedAsItIs()
    {
        using CoordinatorHarness h = AtRest();
        h.Block.OnSetDevice = _ => Task.FromResult(Refused(GateExitCode.NotAudioSink, BlockController.NotAudioSinkMessage));

        ControllerResult result = Change(h);

        Assert.AreEqual(BlockController.NotAudioSinkMessage, result.UserMessage);
        CollectionAssert.AreEqual(PinOnly, h.Trace);
    }

    // Exit cancels the change while its allow runs: the allow is waited for, no second pin is asked for, and the
    // nodes are blocked again before the task ends.
    [TestMethod]
    public void ACancelledChangeBlocksAgainBeforeItEnds()
    {
        using CoordinatorHarness h = AtRest();
        GateRefusesLikeTheGate(h);
        var allowing = new TaskCompletionSource<ControllerResult>();
        h.Block.OnAllow = _ => allowing.Task;

        using var cancel = new CancellationTokenSource();
        Task<ControllerResult> change = h.Coordinator.ChangeDeviceAsync(OtherAddress, cancel.Token);
        h.Pump();
        cancel.Cancel();
        h.Pump();
        Assert.IsFalse(change.IsCompleted, "The allow already sent must be waited for.");

        h.Block.Status = Statuses.Allowed();
        h.Monitor.Publish(Devices.Idle(2));
        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(change.IsCanceled || change.IsFaulted);
        CollectionAssert.AreEqual(AllowThenBlock, h.Trace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }
}
