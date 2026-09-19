using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tray;
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
    private static readonly string[] AllowThenPinThenBlock = ["set-device", "allow", "set-device", "block"];
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

    // The second pin was accepted by the Task Scheduler but its end was not seen, so it may still move the pin, and a
    // block sent now would land on whichever device the gate pins when it runs. Nothing is blocked or allowed until
    // the run could have ended; the state is read again meanwhile, and a pin that never moved is then blocked again.
    [TestMethod]
    public void APinWhoseRunDidNotEndIsNotFollowedByABlockUntilItCouldHaveEnded()
    {
        using CoordinatorHarness h = AtRest();
        int pins = 0;
        h.Block.OnSetDevice = _ => Task.FromResult(++pins == 1
            ? Refused(GateExitCode.OtherDeviceBlocked, BlockController.OtherDeviceBlockedMessage)
            : Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));

        ControllerResult result = Change(h);

        Assert.AreEqual(OpStatus.Failed, result.Status);
        CollectionAssert.AreEqual(AllowThenPin, h.Trace, "A block went out while the pin may still move.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "set-device: the gate run did not end while it was waited for, so it may still move the pin"));
        Assert.IsTrue(h.Coordinator.RecheckRunning, "Nothing reads the state again while the nodes of the device pinned before are enabled.");

        // No other device change goes to the gate meanwhile, and no idle block either.
        Assert.AreEqual(BlockCoordinator.ChangeStillRunningMessage, Change(h).UserMessage);
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(AllowThenPin, h.Trace);

        // The run never moved the pin: once it could have ended, the device pinned now is blocked again.
        h.Advance(BlockCoordinator.UnsettledGateWindow + BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(AllowThenPinThenBlock, h.Trace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // A connect that has to allow first would enable whichever device the gate pins when the allow runs, so it is not
    // sent while a pin may still move.
    [TestMethod]
    public void AConnectDoesNotAllowWhileAPinMayStillMove()
    {
        using CoordinatorHarness h = AtRest();
        h.Block.OnSetDevice = _ => Task.FromResult(Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));
        Change(h);
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        ToggleReport report = h.Toggle(connect: true);

        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.AreEqual(BlockCoordinator.ChangeStillRunningMessage, report.UserMessage);
        CollectionAssert.AreEqual(PinOnly, h.Trace.Where(t => t != "connect").ToArray(), "An allow went out while the pin may still move.");
    }

    // No block is sent for a session end while a pin may still move, and a connect asked for then is still refused
    // at once: the refusal does not depend on a block having been sent.
    [TestMethod]
    public void AConnectIsRefusedWhileTheSessionEndsAndAPinMayStillMove()
    {
        using CoordinatorHarness h = AtRest();
        h.Block.OnSetDevice = _ => Task.FromResult(Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));
        Change(h);
        Assert.IsTrue(h.Coordinator.PinMayMove);
        string[] before = h.Trace.ToArray();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));

        Assert.IsTrue(toggle.IsCompleted, "The connect was left waiting instead of refused at once.");
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, toggle.GetAwaiter().GetResult().UserMessage);
        h.Pump();
        CollectionAssert.AreEqual(before, h.Trace, "Something was sent while the session was ending.");
    }

    // A device change was reading the node state, on its way to allowing the blocked device, when Windows started
    // to end the session. No allow goes out after that: the nodes stay blocked and the pin does not move.
    [TestMethod]
    public void ADeviceChangeSendsNoAllowOnceTheSessionStartsToEndUnderItsStatusRead()
    {
        using CoordinatorHarness h = AtRest();
        GateRefusesLikeTheGate(h);
        h.Block.BeforeReads.Enqueue(() => h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0)));

        Task<ControllerResult> change = h.Coordinator.ChangeDeviceAsync(OtherAddress);
        h.Pump();

        Assert.IsFalse(h.Block.Calls.Contains("allow"), "An allow went out after the session started to end.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsTrue(change.IsCompleted, "The device change did not finish.");
        ControllerResult result = change.GetAwaiter().GetResult();
        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, result.UserMessage);
    }

    // With Block at boot off nothing stops a device change in flight when the session starts to end, and its next
    // step would turn the old device's services back on. No protect verb starts after that point either.
    [TestMethod]
    public void ADeviceChangeStartsNoProtectVerbOnceTheSessionStartsToEnd()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Protection.State = AudioProtectionState.Protected;
        h.Start();
        h.Trace.Clear();
        GateRefusesLikeTheGate(h);
        h.Block.BeforeReads.Clear();
        h.Block.BeforeReads.Enqueue(() => h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0)));

        Task<ControllerResult> change = h.Coordinator.ChangeDeviceAsync(OtherAddress);
        h.Pump();

        CollectionAssert.AreEqual(PinOnly, h.Trace, "A protect verb or a second pin went out after the session started to end.");
        Assert.IsTrue(change.IsCompleted);
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, change.GetAwaiter().GetResult().UserMessage);
        Assert.AreEqual(AudioProtectionState.Protected, h.Protection.State);
    }

    // The session-end block would go to whichever device the gate pins when it runs, so it is not queued while a pin
    // may still move; the BootBlock task blocks the device pinned then.
    [TestMethod]
    public void TheSessionEndBlockIsNotQueuedWhileAPinMayStillMove()
    {
        using CoordinatorHarness h = AtRest();
        int pins = 0;
        h.Block.OnSetDevice = _ => Task.FromResult(++pins == 1
            ? Refused(GateExitCode.OtherDeviceBlocked, BlockController.OtherDeviceBlockedMessage)
            : Results.GateWaitRanOut("Gate", BlockController.TimedOutMessage));
        Change(h);

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        CollectionAssert.AreEqual(AllowThenPin, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because a device change this tray sent may still move the pin"));
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
