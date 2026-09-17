using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
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
    public void ExitWhileADisconnectRunsProtectOnBlocksWithoutWaitingForIt()
    {
        var protecting = new TaskCompletionSource<ControllerResult>();
        using CoordinatorHarness h = InUseWithProtectOnPending(protecting);
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        CollectionAssert.AreEqual(DisconnectProtectOn, h.Trace);

        // Exit: the protect-on may take minutes, longer than the tray waits, so the block goes out now.
        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        CollectionAssert.AreEqual(DisconnectProtectOnBlock, h.Trace);
        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(idle.IsCompleted, "Something was still in flight after the block.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "stopped waiting for ProtectOn because Earshot is closing"));
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
    public void ExitWhileTheHandsFreeCleanUpPutsProtectionBackStillBlocks()
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

        Assert.IsTrue(toggle.IsCompleted);
        Assert.AreEqual(OpStatus.Failed, toggle.GetAwaiter().GetResult().Status);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "Exit during the clean-up skipped the re-block.");
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
