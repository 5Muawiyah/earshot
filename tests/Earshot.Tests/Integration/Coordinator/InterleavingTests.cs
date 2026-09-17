using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Device notifications and status changes that arrive while a gate call is still pending, and Exit in the middle
// of a sequence. The other suites complete most controller calls at once; here the call is held open, the world
// changes under it, and the harness checks the at-rest invariant after every pump.
[TestClass]
public sealed class InterleavingTests
{
    private static readonly string[] ProtectOnOnly = ["protect-on"];
    private static readonly string[] ProtectOnThenBlock = ["protect-on", "block"];
    private static readonly string[] AllowOnly = ["allow"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] DisconnectProtectOn = ["disconnect", "protect-on"];
    private static readonly string[] ConnectProtectOffConnectProtectOn = ["connect", "protect-off", "connect", "protect-on"];
    private static readonly string[] DisconnectProtectOnBlock = ["disconnect", "protect-on", "block"];
    private static readonly string[] ConnectProtectOn = ["connect", "protect-on"];
    private static readonly string[] ConnectProtectOffConnect = ["connect", "protect-off", "connect"];
    private static readonly string[] SetDeviceThenBlock = ["set-device:" + Devices.Address, "block"];
    private static readonly string[] AllowBlockSetDevice = ["allow", "block", "set-device:A1B2C3D4E5F6"];

    // Block at boot on, the nodes enabled and the AirPods in use, protection on and already noticed. Windows then
    // turns Handsfree back on, so the next Block sequence runs protect-on first, which the test holds open.
    private static CoordinatorHarness InUseWithProtectOnPending(TaskCompletionSource<ControllerResult> protecting)
    {
        var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        Assert.IsEmpty(h.Trace);

        h.Protection.State = AudioProtectionState.NotProtected;
        h.Protection.OnApply = (_, ct) =>
        {
            ct.Register(() => protecting.TrySetCanceled(ct));
            return protecting.Task;
        };
        return h;
    }

    [TestMethod]
    public void AirPodsInUseAgainWhileTheIdleProtectOnRunsAreNotDisconnected()
    {
        var protecting = new TaskCompletionSource<ControllerResult>();
        using CoordinatorHarness h = InUseWithProtectOnPending(protecting);

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace);
        Assert.IsTrue(h.Coordinator.IsBusy);

        // The user reconnects from Windows while protection is being applied.
        h.Publish(Devices.Active(3));
        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace, "The block disconnected a user who had reconnected.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "no block sent, because the AirPods are in use"));
        Assert.AreEqual(0, h.Coordinator.IdleFailures, "A block that was not needed is not a failure.");
        Assert.IsEmpty(h.Cards.Shown);

        // Out of use again: the rule starts from the grace period, and protection needs nothing this time.
        h.Protection.OnApply = null;
        h.Publish(Devices.Idle(4));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(ProtectOnThenBlock, h.Trace);
        h.AssertAtRest();
    }

    [TestMethod]
    public void BlockAtBootTurnedOffWhileTheIdleProtectOnRunsStopsTheBlock()
    {
        var protecting = new TaskCompletionSource<ControllerResult>();
        using CoordinatorHarness h = InUseWithProtectOnPending(protecting);

        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace);

        // Block at boot goes off elsewhere (the gate's config) while protect-on runs.
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsEmpty(h.Block.Calls, "The block ran although Block at boot was now off.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "no block sent, because Block at boot is now off"));
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
    }

    [TestMethod]
    public void AirPodsInUseWhileTheStartUpProtectOnRunsAreNotDisconnected()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => protecting.Task;

        h.Start();
        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace, "The start-up block turns Handsfree off first.");

        h.Publish(Devices.Active(2));
        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsFalse(h.Coordinator.IsBusy);
        h.AssertAtRest();
    }

    [TestMethod]
    public void NotificationsWhileAnAllowIsPendingStartNoIdleBlock()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        var allowing = new TaskCompletionSource<ControllerResult>();
        h.Block.OnAllow = _ => allowing.Task;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls);

        // The gate enables the nodes before it returns: the endpoints come back and a status read says Allowed,
        // all while the allow is still pending. That is the idle rule's condition, but Earshot is mid-connect.
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Idle(5));
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls, "An idle block ran in the middle of a connect.");
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);

        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.AreEqual(OpStatus.Success, toggle.GetAwaiter().GetResult().Status);
        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls);
        h.AssertAtRest();
    }

    [TestMethod]
    public void NotificationsWhileABlockIsPendingStartNoSecondBlock()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        h.Publish(Devices.Idle(150));
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "A second block was sent while the first was pending.");

        h.Block.Status = Statuses.Blocked();
        h.Monitor.Publish(Devices.NotPresent(151));
        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.AreEqual(OpStatus.Success, toggle.GetAwaiter().GetResult().Status);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        h.AssertAtRest();
    }

    [TestMethod]
    public void ExitWhileADisconnectRunsProtectOnBlocksOnlyOnceItHasEnded()
    {
        var protecting = new TaskCompletionSource<ControllerResult>();
        using CoordinatorHarness h = InUseWithProtectOnPending(protecting);
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        CollectionAssert.AreEqual(DisconnectProtectOn, h.Trace);

        // Exit: the gate is still turning Handsfree off, and a block now would disable the nodes under it.
        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        CollectionAssert.AreEqual(DisconnectProtectOn, h.Trace, "The block was sent while protect-on was still running.");
        Assert.IsFalse(toggle.IsCompleted);
        Assert.IsFalse(idle.IsCompleted);
        Assert.AreEqual(0, h.Protection.CancellableApplies, "The wait for a protect verb could be stopped.");

        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        CollectionAssert.AreEqual(DisconnectProtectOnBlock, h.Trace);
        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(idle.IsCompleted, "Something was still in flight after the block.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        h.AssertAtRest();
    }

    [TestMethod]
    public void ExitWhileAConnectWaitsForTheEndpointsBlocksOnce()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        // The allow works but the endpoints have not come back yet.
        h.Block.OnAllow = _ =>
        {
            h.Block.Status = Statuses.Allowed();
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted);

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The clean-up blocks, and the block before closing finds nothing left to do.");
        Assert.IsEmpty(h.Cards.Shown.Where(c => c.Content.Status != "Connecting" && c.Content.Status != BlockCoordinator.AllowingStatus),
            "No card is shown once Earshot is closing.");
        h.AssertAtRest();
    }

    [TestMethod]
    public void ExitWhileTheHandsFreeCleanUpPutsProtectionBackBlocksOnceThePutBackHasEnded()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1, capture: false));
        h.Start();
        h.Block.Calls.Clear();
        h.Trace.Clear();
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Idle(2, capture: false));

        // The A2DP filter refuses both times, so the Hands-Free assisted way runs and then cleans up.
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (protect, ct) =>
        {
            if (!protect)
            {
                h.Protection.State = AudioProtectionState.NotProtected;
                h.Monitor.Publish(Devices.Idle(3));
                return Task.FromResult(ControllerResult.Ok("Audio quality protection is off"));
            }

            ct.Register(() => protecting.TrySetCanceled(ct));
            return protecting.Task;
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        CollectionAssert.AreEqual(ConnectProtectOffConnectProtectOn, h.Trace);
        Assert.IsEmpty(h.Block.Calls);

        h.Coordinator.BeginShutdown();
        h.Pump();

        Assert.IsFalse(toggle.IsCompleted);
        Assert.IsEmpty(h.Block.Calls, "The block was sent while the put-back was still running.");

        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.AreEqual(OpStatus.Failed, toggle.GetAwaiter().GetResult().Status);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "Exit during the clean-up skipped the re-block.");
        Assert.IsNull(h.Coordinator.PendingProtect, "Protection was put back before the block.");
        h.AssertAtRest();
    }

    [TestMethod]
    public void ExitWhileTheGateAllowRunsBlocksOnceTheAllowHasLanded()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        var allowing = new TaskCompletionSource<ControllerResult>();
        h.Block.OnAllow = _ => allowing.Task;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls);

        // Exit while the SYSTEM task is still enabling the nodes. A node read now would still say Blocked.
        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsFalse(toggle.IsCompleted, "The connect ended before the allow it sent.");
        Assert.IsFalse(idle.IsCompleted);
        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls);

        h.Block.Status = Statuses.Allowed();
        h.Monitor.Publish(Devices.Idle(5));
        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The allow landed after Exit with nothing left to block it.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsTrue(h.Coordinator.BlockingBeforeClosing);
        h.AssertAtRest();
    }

    [TestMethod]
    public void ADeviceChangeDuringTheGateAllowWaitsForItAndForTheBlockAfterIt()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        var allowing = new TaskCompletionSource<ControllerResult>();
        h.Block.OnAllow = _ => allowing.Task;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();

        // Another device is chosen while the gate is enabling the current one's nodes.
        cancel.Cancel();
        h.Pump();
        Task<ControllerResult> pin = h.Coordinator.ChangeDeviceAsync("A1B2C3D4E5F6");
        h.Pump();
        Assert.IsFalse(pin.IsCompleted, "The pin moved while the allow was still running.");
        CollectionAssert.AreEqual(AllowOnly, h.Block.Calls);

        h.Block.Status = Statuses.Allowed();
        h.Monitor.Publish(Devices.Idle(5));
        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(pin.IsCompleted);
        CollectionAssert.AreEqual(AllowBlockSetDevice, h.Block.Calls, "The old device's nodes were left enabled when the pin moved.");
        Assert.IsEmpty(h.Block.CancellableChanges);
    }

    [TestMethod]
    public void ADeviceChangeDuringTheCheckAfterAConnectWaitsForTheProtectVerbAndNothingIsBlockedUnderIt()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: true);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        Assert.IsEmpty(h.Trace);

        // Connecting brings Handsfree back, so the check after it runs protect-on, which the gate takes minutes over.
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Protection.State = AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Active(2));
            return Task.FromResult(Results.Connected());
        });
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => protecting.Task;

        using var cancel = new CancellationTokenSource();
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cancel.Token);
        h.Pump();
        CollectionAssert.AreEqual(ConnectProtectOn, h.Trace);

        cancel.Cancel();
        Task<ControllerResult> pin = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Publish(Devices.Idle(3));
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsFalse(toggle.IsCompleted);
        Assert.IsFalse(pin.IsCompleted);
        Assert.IsEmpty(h.Block.Calls, "A block or the pin went out while protect-on was still running.");

        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        Assert.AreEqual(OpStatus.Success, pin.GetAwaiter().GetResult().Status);
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(SetDeviceThenBlock, h.Block.Calls);
    }

    [TestMethod]
    public void AProtectVerbWhoseEndWasNotSeenHoldsTheBlockBackUntilItCanNoLongerRun()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        // Windows turned Handsfree back on; the gate accepts protect-on, but its end is never seen.
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Protection.OnApply = (_, _) => Task.FromResult(Results.GateWaitRanOut("Protect", "Protection did not finish in time. Try again."));
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace, "A block was sent while protect-on may still be running.");
        Assert.IsTrue(h.Coordinator.ProtectMayRun);
        Assert.IsTrue(h.Coordinator.RecheckRunning);

        // The session end sends none either.
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because an audio quality change this tray started may still be running"));
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Advance(BlockCoordinator.UnsettledGateWindow - TimeSpan.FromSeconds(1));
        Assert.IsEmpty(h.Block.Calls);

        // Once it can no longer run, the rule blocks, without starting another protect-on that could run as long.
        h.Advance(TimeSpan.FromSeconds(1));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(ProtectOnThenBlock, h.Trace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void ASessionEndWhileAProtectionChangeRunsIssuesNoBlock()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => protecting.Task;
        Task<ControllerResult> change = h.Coordinator.SetProtectionAsync(true, CardPlace.NearCursor);
        h.Pump();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));

        CollectionAssert.AreEqual(ProtectOnOnly, h.Trace, "The session-end block would disable the nodes under a service change.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because an audio quality change this tray started may still be running"));

        // The shutdown was cancelled; the change ends, and the next session end blocks.
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();
        Assert.IsTrue(change.IsCompleted);

        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        CollectionAssert.AreEqual(ProtectOnThenBlock, h.Trace);
    }

    [TestMethod]
    public void ASessionEndDuringTheHandsFreeWayStartsNoProtectVerbBesideItsBlock()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1, capture: false));
        h.Start();
        h.Block.Calls.Clear();
        h.Trace.Clear();
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Idle(2, capture: false));

        // The A2DP filter refuses, protection comes off, and the second connect is still waiting when Windows ends
        // the session.
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetCanceled(token));
            return waiting.Task;
        });
        h.Protection.Effect = protect =>
        {
            h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(protect ? 30 : 20, capture: !protect));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        CollectionAssert.AreEqual(ConnectProtectOffConnect, h.Trace);

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsFalse(h.Trace.Contains("protect-on"), "A protect verb was started beside the session-end block.");
        CollectionAssert.AreEqual(ConnectProtectOffConnect, h.Trace.Take(3).ToList());
        Assert.IsTrue(h.Trace.Skip(3).All(call => call == "block"));
        Assert.AreEqual(true, h.Coordinator.PendingProtect, "Protection is put back at the next connect.");
        h.AssertAtRest();
    }

    [TestMethod]
    public void AnEnumerationThatFinishesAfterANewerOneIsIgnored()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Publish(Devices.Active(2));
        long newest = h.Monitor.Current.Sequence;

        // An older enumeration that still showed the AirPods idle arrives late.
        h.Monitor.PublishLate(Devices.Idle(newest - 1));
        h.Pump();

        Assert.IsFalse(h.Coordinator.IdleWaitRunning, "An older snapshot replaced a newer one.");
    }

    [TestMethod]
    public void AStatusChangeWhileTheBlockBeforeClosingWaitsIsReadAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => protecting.Task;
        Task<ControllerResult> change = h.Coordinator.SetProtectionAsync(true, CardPlace.NearCursor);
        h.Pump();

        // The AirPods go back to the phone and Exit is chosen while the protection change runs.
        h.Publish(Devices.Idle(2));
        h.Coordinator.BeginShutdown();
        h.Pump();
        Assert.IsEmpty(h.Block.Calls, "The block before closing must wait for the change in flight.");

        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsTrue(change.IsCompleted);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        h.AssertAtRest();
    }
}
