using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// What happens at the two ends of a session: the check when Earshot starts (did the boot block hold?) and the
// best-effort block when Windows says the session is ending.
[TestClass]
public sealed class StartUpAndSessionTests
{
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];
    private static readonly string[] AllowOnly = ["allow"];
    private static readonly string[] AllowAfterBlock = ["block", "allow"];
    private static readonly string[] BlockTwice = ["block", "block"];
    private static readonly string[] DisconnectThenBlock = ["disconnect", "block"];
    private static readonly string[] ConnectProtectOffConnect = ["connect", "protect-off", "connect"];
    private static readonly string[] AllowThenBlockTwice = ["allow", "block", "block"];

    private static SessionEndingEventArgs Query() => new(isQuery: true, ending: true, flags: 0);

    [TestMethod]
    public void AtStartUpNodesThatAreEnabledAndNotInUseAreBlockedAtOnce()
    {
        using var h = new CoordinatorHarness(startedAtLogon: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));

        h.Start();

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The nodes were left enabled at start-up.");
        Assert.AreEqual(BlockState.Blocked, h.Coordinator.BlockStatus?.State);
        Assert.IsEmpty(h.Cards.Shown);
    }

    [TestMethod]
    public void AtSignInAirPodsThatAreAlreadyInUseAreEvidenceTheBootBlockDidNotHold()
    {
        using var h = new CoordinatorHarness(startedAtLogon: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));

        h.Start();

        Assert.IsEmpty(h.Block.Calls, "The AirPods are in active use, so the nodes stay enabled.");
        Assert.HasCount(1, h.Cards.Shown);
        Assert.AreEqual(BlockCoordinator.ConnectedAtStartUpMessage, h.Cards.Shown[0].Content.Status);
        Assert.AreEqual(CardAnchor.NearTray, h.Cards.Shown[0].Anchor);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connected at start-up"));

        // The idle rule takes it from there.
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void StartedByHandTheSameStateIsLoggedWithoutACard()
    {
        using var h = new CoordinatorHarness(startedAtLogon: false);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));

        h.Start();

        Assert.IsEmpty(h.Cards.Shown);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connected at start-up"));
    }

    [TestMethod]
    public void TheStartUpCheckWaitsUntilBothTheEndpointsAndTheNodesHaveBeenRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("The system cannot find the file specified.", unchecked((int)0x80070002));
        h.Monitor.Set(Devices.NotRead());

        h.Start();
        Assert.IsEmpty(h.Block.Calls);

        // The endpoints are read, but the nodes still are not.
        h.Publish(Devices.Idle(1));
        Assert.IsEmpty(h.Block.Calls);

        h.Block.StatusFailure = null;
        h.Publish(Devices.Idle(2));

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void TheStartUpCheckIsSkippedWhenTheUserActsFirst()
    {
        using var h = new CoordinatorHarness();

        // The nodes are enabled and not in use, which the start-up check would block, but the endpoints have not
        // been read yet, so it is still waiting when the user clicks.
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.NotRead());
        h.Start();
        Assert.IsEmpty(h.Block.Calls);

        h.Toggle(connect: true);

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Start-up check skipped"));
        Assert.IsEmpty(h.Block.Calls, "The start-up check ran after the user had acted.");
    }

    [TestMethod]
    public void TheSessionEndBlockIsIssuedWithoutWaitingForIt()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;

        h.Coordinator.OnSessionEnding(Query());

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The block was not issued as the session ended.");
        Assert.IsTrue(h.Coordinator.IsBusy);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: block queued at "));

        // A second message for the same session end does not start another.
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "session-end block (queued "));
        Assert.IsFalse(h.Coordinator.IsBusy);
    }

    // At-rest. From the first WM_QUERYENDSESSION until Windows says the session end was cancelled, nothing that
    // could enable the nodes or change a setting or the device starts: a process kill right after would leave the
    // nodes enabled across the shutdown. The refusal is a result, at once, never a wait. It does not depend on
    // whether a block was sent for the session end: the tests below cover the block sent and each reason it is not.
    private static void AssertRefused(CoordinatorHarness h, Task<ToggleReport> toggle, string when)
    {
        Assert.IsTrue(toggle.IsCompleted, "The connect " + when + " was left waiting instead of refused at once.");
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.AreEqual(OpStatus.NotAttempted, report.Status, when);
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, report.UserMessage, when);
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, h.Cards.Shown[^1].Content.Status, "The refusal " + when + " had no card.");
        Assert.IsFalse(h.Coordinator.IsBusy, "The refusal " + when + " left the coordinator busy.");
    }

    [TestMethod]
    public void AConnectIsRefusedAtOnceOnceTheSessionEndBlockHasBeenSent()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The session-end block itself must still run.");

        // No pump between the call and the assertions: the refusal is synchronous.
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "after the session-end block");
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "asked for a second time");
        h.Pump();

        Assert.IsEmpty(h.Connection.Calls, "No connect may be sent while the session ends.");
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "Nothing beyond the session-end block may be sent.");
    }

    // The resting state: the nodes already read blocked, so no block is sent for the session end. A connect asked
    // for then must still be refused; it would send the allow that enables the nodes.
    [TestMethod]
    public void AConnectIsRefusedWhileTheSessionEndsWithTheNodesAlreadyBlocked()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because the nodes are already blocked."));

        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "with the nodes already blocked");
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "after WM_ENDSESSION said the session is ending");
        h.Pump();

        Assert.IsEmpty(h.Block.Calls, "An allow went out while the session was ending.");
        Assert.IsEmpty(h.Connection.Calls, "A connect went out while the session was ending.");
        Assert.IsTrue(h.Coordinator.SessionEndInProgress);
    }

    [TestMethod]
    public void AConnectIsRefusedWhileTheSessionEndsBeforeTheNodesHaveEverBeenRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("no status");
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the boot block status was never read"));

        // The status can be read by the time of the click, so nothing but the refusal stands in the allow's way.
        h.Block.StatusFailure = null;
        h.Block.Status = Statuses.Blocked();
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "with the status never read");
        h.Pump();

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsEmpty(h.Connection.Calls);
    }

    [TestMethod]
    public void AConnectIsRefusedWhileTheSessionEndsAndAProtectVerbMayStillRun()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Protection.OnApply = (_, _) => Task.FromResult(Results.GateWaitRanOut("Protect", "Protection did not finish in time. Try again."));
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        Assert.IsTrue(h.Coordinator.ProtectMayRun);
        string[] before = h.Trace.ToArray();

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because an audio quality change this tray started may still be running"));

        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "while a protect verb may still run");
        h.Pump();

        CollectionAssert.AreEqual(before, h.Trace, "Something was sent while the session was ending.");
    }

    [TestMethod]
    public void EverySettingAndDeviceChangeIsRefusedWhileTheSessionEnds()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        bool setupRan = false;

        Task<ControllerResult>[] refused =
        [
            h.Coordinator.SetProtectionAsync(true, CardPlace.NearCursor),
            h.Coordinator.SetProtectionAsync(false, CardPlace.NearCursor),
            h.Coordinator.SetBlockAtBootAsync(false, CardPlace.NearCursor),
            h.Coordinator.SetBlockAtBootAsync(true, CardPlace.NearCursor),
            h.Coordinator.ChangeDeviceAsync(Devices.Address),
            h.Coordinator.RunAsync("setup", _ =>
            {
                setupRan = true;
                return Task.FromResult(ControllerResult.Ok("Done"));
            }),
        ];

        foreach (Task<ControllerResult> task in refused)
        {
            Assert.IsTrue(task.IsCompleted, "A change was left waiting instead of refused at once.");
            ControllerResult result = task.GetAwaiter().GetResult();
            Assert.AreEqual(OpStatus.NotAttempted, result.Status);
            Assert.AreEqual(BlockCoordinator.SessionEndingMessage, result.UserMessage);
        }

        h.Pump();
        Assert.IsFalse(setupRan, "Setup ran while the session was ending.");
        Assert.IsEmpty(h.Trace, "A gate or protection call went out while the session was ending.");
        Assert.IsFalse(h.Coordinator.IsBusy);
    }

    // A connect that was already waiting behind other work when Windows started to end the session is refused as
    // soon as that work ends; it is never left waiting and never goes ahead.
    [TestMethod]
    public void AConnectWaitingBehindOtherWorkIsRefusedOnceTheSessionStartsToEnd()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var blocking = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => blocking.Task;
        h.Publish(Devices.Idle(2));
        h.Advance(BlockCoordinator.IdleGrace);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The idle block this test waits behind did not start.");

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(toggle.IsCompleted, "The connect did not wait for the idle block.");

        h.Coordinator.OnSessionEnding(Query());
        h.Block.Status = Statuses.Blocked();
        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        AssertRefused(h, toggle, "that waited behind the idle block");
        Assert.IsEmpty(h.Connection.Calls);
        Assert.IsFalse(h.Block.Calls.Contains("allow"), "An allow went out while the session was ending.");
    }

    // A disconnect sends no allow: its only gate change is the block that follows it. So it may still run while
    // the session ends, and it never leaves the nodes more enabled than it found them.
    [TestMethod]
    public void ADisconnectStillRunsWhileTheSessionEndsAndSendsNoAllow()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls);

        ToggleReport report = h.Toggle(connect: false);

        Assert.AreNotEqual(BlockCoordinator.SessionEndingMessage, report.UserMessage);
        Assert.HasCount(1, h.Connection.Calls, "The disconnect was not sent.");
        Assert.IsFalse(h.Block.Calls.Contains("allow"));
    }

    // The same with Block at boot on and protection wanted, where the "no allow" half can fail. No block was sent
    // for the session end (the status had never been read), so the block after the disconnect is the only one, and
    // it goes straight out: no protect verb is started in front of it while the session ends.
    [TestMethod]
    public void ADisconnectWhileTheSessionEndsBlocksAtOnceWithNoProtectVerbInFrontOfTheBlock()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.StatusFailure = new IOException("no status");
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the boot block status was never read"), "This test needs the session end that sends no block.");

        // Readable by the time of the click: the nodes are enabled and Hands-Free is back.
        h.Block.StatusFailure = null;
        h.Block.Status = Statuses.Allowed();
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Trace.Clear();

        ToggleReport report = h.Toggle(connect: false);

        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace, "Something other than the block followed the disconnect while the session was ending.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.AreNotEqual(BlockCoordinator.SessionEndingMessage, report.UserMessage);
    }

    // The Hands-Free assisted connect turned protection off and then did not connect, after Windows had started to
    // end a session for which no block was sent (Block at boot is off). Its clean-up starts no protect verb then:
    // the setting is kept for the next connect.
    [TestMethod]
    public void TheConnectCleanUpStartsNoProtectVerbWhileTheSessionEnds()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1, capture: false));
        h.Start();
        h.Trace.Clear();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));
        var second = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => second.Task);
        h.Protection.Effect = protect =>
        {
            h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(protect ? 30 : 20, capture: !protect));
        };

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        CollectionAssert.AreEqual(ConnectProtectOffConnect, h.Trace);

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because Block at boot is off"));
        second.SetResult(Results.A2dpRejected());
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        CollectionAssert.AreEqual(ConnectProtectOffConnect, h.Trace, "A protect verb was started while the session was ending.");
        Assert.AreEqual(true, h.Coordinator.PendingProtect, "Protection is put back at the next connect.");
    }

    // Windows started to end the session before Earshot's start-up check ran. The AirPods are in use and Hands-Free
    // is back, which the check would put right with a protect verb; it starts none while the session ends.
    [TestMethod]
    public void TheStartUpCheckStartsNoProtectVerbWhileTheSessionEnds()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Coordinator.OnSessionEnding(Query());

        h.Start();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connected at start-up"), "The start-up check did not run.");
        Assert.IsEmpty(h.Trace, "The start-up check changed something while the session was ending.");
    }

    // A connect was reading the node state, on its way to the allow, when Windows started to end the session. The
    // status had never been read before, so nothing told the session end to stop the connect; the allow is still
    // not sent.
    [TestMethod]
    public void AConnectSendsNoAllowOnceTheSessionStartsToEndUnderItsStatusRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("no status");
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Block.StatusFailure = null;
        h.Block.Status = Statuses.Blocked();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Block.BeforeReads.Clear();
        h.Block.BeforeReads.Enqueue(() => h.Coordinator.OnSessionEnding(Query()));

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsEmpty(h.Block.Calls, "An allow went out after the session started to end.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsTrue(toggle.IsCompleted);
        ToggleReport report = toggle.GetAwaiter().GetResult();
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, report.UserMessage);
        Assert.AreEqual("the session is ending", report.CancelledBecause);
        Assert.HasCount(1, h.Connection.Calls, "A second connect request went out after the refused allow.");
    }

    // A session-end block that fails has blocked nothing, so Exit afterwards still sends the block before closing.
    private static CoordinatorHarness AfterAFailedSessionEndBlock(Func<int, ControllerResult> blockResult)
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        int blocks = 0;
        h.Block.OnBlock = _ =>
        {
            ControllerResult result = blockResult(++blocks);
            if (result.IsSuccess)
            {
                h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            }

            return Task.FromResult(result);
        };
        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "session-end block (queued "), "This test needs a session-end block that failed.");

        // The AirPods are no longer in use by the time Exit is chosen.
        h.Publish(Devices.Idle(2));
        return h;
    }

    [TestMethod]
    public void ExitAfterAFailedSessionEndBlockStillBlocksBeforeClosing()
    {
        using CoordinatorHarness h = AfterAFailedSessionEndBlock(call => call == 1 ? ControllerResult.Fail("Could not block the AirPods. Try again.", []) : ControllerResult.Ok("Blocked at boot"));

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(BlockTwice, h.Block.Calls, "No block was sent before closing after the session-end block failed.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        Assert.IsNull(h.Coordinator.ClosingNotice);
    }

    [TestMethod]
    public void ExitAfterAFailedSessionEndBlockSaysSoWhenTheBlockBeforeClosingFailsToo()
    {
        using CoordinatorHarness h = AfterAFailedSessionEndBlock(_ => ControllerResult.Fail("Could not block the AirPods. Try again.", []));
        h.CheckInvariantOnPump = false;

        h.Coordinator.BeginShutdown();
        h.Pump();

        CollectionAssert.AreEqual(BlockTwice, h.Block.Calls);
        Assert.AreEqual("Could not block the AirPods. Try again.", h.Coordinator.ClosingNotice, "Earshot closed with the nodes enabled and said nothing.");
    }

    // The session-end block took, and then something outside Earshot enabled the nodes again. Exit, inside the
    // idle grace, goes by what the nodes read now, not by the block that once took: it blocks them before closing.
    [TestMethod]
    public void ExitAfterASessionEndBlockThatTookStillBlocksNodesEnabledSince()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(300));
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();

        h.Coordinator.BeginShutdown();
        h.Pump();

        CollectionAssert.AreEqual(BlockTwice, h.Block.Calls, "Earshot closed with the nodes enabled because a session-end block had taken earlier.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // The block sent at WM_QUERYENDSESSION failed, and its result is in by WM_ENDSESSION TRUE: it is sent once more
    // then, and not a third time.
    [TestMethod]
    public void ASessionEndBlockThatFailedIsSentOnceMoreWhenTheSessionIsSaidToEnd()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        int blocks = 0;
        h.Block.OnBlock = _ =>
        {
            if (++blocks == 1)
            {
                return Task.FromResult(ControllerResult.Fail("Could not block the AirPods. Try again.", []));
            }

            h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        h.Pump();

        CollectionAssert.AreEqual(BlockTwice, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // A session-end block that throws before it is even queued leaves what a returned failure leaves: it is sent
    // once more at WM_ENDSESSION TRUE.
    [TestMethod]
    public void ASessionEndBlockThatThrowsIsSentOnceMoreWhenTheSessionIsSaidToEnd()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        int blocks = 0;
        h.Block.OnBlock = _ =>
        {
            if (++blocks == 1)
            {
                throw new InvalidOperationException("The gate could not be reached.");
            }

            h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        h.Pump();

        CollectionAssert.AreEqual(BlockTwice, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // The same throw, and then the session end is cancelled: the idle wait the session end stopped is running
    // again, so the nodes are still blocked once the grace window has passed.
    [TestMethod]
    public void ASessionEndBlockThatThrowsLeavesTheIdleRuleArmed()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Publish(Devices.Idle(2));
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);
        h.CheckInvariantOnPump = false;
        int blocks = 0;
        h.Block.OnBlock = _ =>
        {
            if (++blocks == 1)
            {
                throw new InvalidOperationException("The gate could not be reached.");
            }

            h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Pump();

        Assert.IsTrue(h.Coordinator.IdleWaitRunning, "Nothing was left armed after the session-end block threw.");
        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        h.AssertAtRest();
    }

    // The coordinator says why it stopped a change, and says it from what happened, not from the flag as it stands
    // when the change returns: here the session end is cancelled again while the protect verb is still running.
    [TestMethod]
    public void AChangeStoppedForASessionEndSaysSoEvenWhenTheSessionEndIsCancelledBeforeItReturns()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        var protecting = new TaskCompletionSource<ControllerResult>();
        h.Protection.OnApply = (_, _) => protecting.Task;
        Task<ControllerResult> change = h.Coordinator.SetProtectionAsync(true, CardPlace.NearCursor);
        h.Pump();

        h.Coordinator.OnSessionEnding(Query());
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Protection.State = AudioProtectionState.Protected;
        protecting.SetResult(ControllerResult.Ok("Audio quality protected"));
        h.Pump();

        Assert.IsTrue(change.IsCompletedSuccessfully, "A change stopped for a session end ended in an exception, not a result.");
        Assert.AreEqual(BlockCoordinator.SessionEndStoppedMessage, change.GetAwaiter().GetResult().UserMessage);
        StringAssert.Contains(BlockCoordinator.SessionEndStoppedMessage, "may not have finished", "The card claims more than is known: the verb may well have completed.");
    }

    [TestMethod]
    public void AConnectStoppedForASessionEndSaysSoEvenWhenTheSessionEndIsCancelledBeforeItReturns()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        var connecting = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => connecting.Task);
        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        h.Coordinator.OnSessionEnding(Query());
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        connecting.SetCanceled();
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.AreEqual("the session is ending", toggle.GetAwaiter().GetResult().CancelledBecause);
    }

    // The Restart Manager asks Earshot to close with the same two messages, lParam ENDSESSION_CLOSEAPP. The nodes
    // are blocked for it as for a shutdown, a connect is refused with a card that is true for it too, and
    // WM_ENDSESSION with wParam FALSE ends the refusal. The Restart Manager's use of the two messages is on the
    // guidelines page; "If wParam is FALSE, the application should not shut down" is on the WM_ENDSESSION page.
    // https://learn.microsoft.com/windows/win32/rstmgr/guidelines-for-applications
    // https://learn.microsoft.com/windows/win32/shutdown/wm-endsession
    [TestMethod]
    public void ACloseRequestFromTheRestartManagerIsTreatedAsASessionEnd()
    {
        const uint CloseApp = 0x1;
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: CloseApp));
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: CloseApp));
        h.Pump();

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The nodes were not blocked for a close request.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "asked Earshot to close"), "The log does not say this was a close request.");
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "during a close request");
        StringAssert.Contains(BlockCoordinator.SessionEndingMessage, "closing Earshot", "The card is not true when Windows closes only Earshot.");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: CloseApp));
        Assert.IsFalse(h.Coordinator.SessionEndInProgress);
    }

    // Ending, ending again, cancelled, ending again, as one sequence.
    [TestMethod]
    public void TheSessionEndStateFollowsEveryMessageInTurn()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        Assert.IsTrue(h.Coordinator.SessionEndInProgress);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));
        h.Pump();
        Assert.IsTrue(h.Coordinator.SessionEndInProgress);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The second message for one session end sent a second block.");
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "after ending twice");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        Assert.IsFalse(h.Coordinator.SessionEndInProgress);
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        Assert.AreNotEqual(BlockCoordinator.SessionEndingMessage, h.Toggle(connect: true).UserMessage, "A connect was refused after the session end was cancelled.");
        CollectionAssert.AreEqual(AllowAfterBlock, h.Block.Calls.Take(2).ToArray());

        // The next session end starts afresh: it blocks again what that connect allowed, and refuses again.
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        int before = h.Block.Calls.Count(c => c == "block");
        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        Assert.IsTrue(h.Coordinator.SessionEndInProgress);
        Assert.AreEqual(before + 1, h.Block.Calls.Count(c => c == "block"), "The second session end sent no block of its own.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "in the second session end");
    }

    // WM_ENDSESSION with wParam FALSE: the session end was cancelled. Whatever was refused meanwhile left nothing
    // behind, so the next connect runs as usual, with no restart.
    [TestMethod]
    public void AfterACancelledSessionEndAConnectRunsAgain()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The session-end block did not run with the nodes allowed.");
        AssertRefused(h, h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true)), "while the session was ending");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Pump();
        Assert.IsFalse(h.Coordinator.SessionEndInProgress);
        Assert.IsFalse(h.Coordinator.IsBusy);

        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        ToggleReport report = h.Toggle(connect: true);

        Assert.AreNotEqual(BlockCoordinator.SessionEndingMessage, report.UserMessage);
        CollectionAssert.AreEqual(AllowAfterBlock, h.Block.Calls.Take(2).ToArray(), "The connect after the cancelled session end did not allow the nodes.");
        Assert.IsNotEmpty(h.Connection.Calls);
    }

    // Exit while the session ends: the block before closing is a block, so it is not refused, and WhenIdleAsync,
    // which Exit waits on, completes.
    [TestMethod]
    public void TheBlockBeforeClosingStillRunsWhileTheSessionEnds()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls, "This test needs the session end that sends no block.");

        // The nodes turn up enabled afterwards (something outside Earshot enabled them), and a read shows it.
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(2));
        _ = h.Coordinator.RefreshStatusAsync();
        h.Pump();

        h.Coordinator.BeginShutdown();
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();

        Assert.IsTrue(idle.IsCompleted, "WhenIdleAsync did not complete, so Exit would wait out its limit.");
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls, "The block before closing did not run while the session was ending.");
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    [TestMethod]
    public void NoSessionEndBlockIsIssuedWithBlockAtBootOffOrTheNodesAlreadyBlocked()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Block at boot is off"));

        h.Block.Status = Statuses.Blocked();
        h.Publish(Devices.NotPresent(2));
        h.Coordinator.OnSessionEnding(Query());
        Assert.IsEmpty(h.Block.Calls);
    }

    // Windows starts to end the session while this tray's own allow is still running. The node read taken before the
    // allow still says Blocked, but the allow is about to enable the nodes, so a block is queued for the session end,
    // the connect is stopped, and its clean-up blocks again what the allow enabled.
    [TestMethod]
    public void ASessionEndWhileThisTraysAllowStillRunsQueuesABlockAndStopsTheConnect()
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

        h.Coordinator.OnSessionEnding(Query());
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0));

        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "No block was queued for the session end while the allow still ran.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: block queued at "));
        Assert.IsFalse(h.Log.Has(LogLevel.Info, "the nodes are already blocked"));
        h.Pump();

        // The allow lands and enables the nodes.
        h.Block.Status = Statuses.Allowed();
        allowing.SetResult(ControllerResult.Ok("Allowed"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled, "The connect went on as the session ended.");
        CollectionAssert.AreEqual(AllowThenBlockTwice, h.Block.Calls);
        Assert.AreEqual(BlockState.Blocked, h.Block.Status.State);
    }

    // With Block at boot on, a session end stops the operation in flight even when no block is needed yet, so a
    // connect that has not sent its allow does not send it as the session ends.
    [TestMethod]
    public void ASessionEndStopsAConnectThatHasNotSentItsAllowYet()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        var connecting = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(token =>
        {
            token.Register(() => connecting.TrySetResult(Results.NodesBlocked()));
            return connecting.Task;
        });

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        h.Coordinator.OnSessionEnding(Query());
        h.Pump();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Session ending: no block issued, because the nodes are already blocked."));
        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(toggle.GetAwaiter().GetResult().Cancelled);
        Assert.AreEqual("the session is ending", toggle.GetAwaiter().GetResult().CancelledBecause, "No block was sent for this session end, and the reason for the cancellation was lost.");
        Assert.IsEmpty(h.Block.Calls, "An allow went out as the session ended.");
    }

    [TestMethod]
    public void NoSessionEndBlockIsIssuedBeforeTheNodesHaveEverBeenRead()
    {
        using var h = new CoordinatorHarness();
        h.Block.StatusFailure = new IOException("no status");
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Coordinator.OnSessionEnding(Query());

        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the boot block status was never read"));
    }

    [TestMethod]
    public void ASessionEndThatIsCancelledIsLoggedAndCanBlockAgainLater()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        h.Block.ActiveLink = ActiveLinkOnBlock.Drops;
        h.Coordinator.OnSessionEnding(Query());
        h.Pump();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "The session end was cancelled"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "issued while the AirPods were in use, so they may have been disconnected"),
            "A block that may have dropped a user who kept the session was not recorded.");
        Assert.HasCount(1, h.Block.Calls);

        // The nodes are enabled again (a driver re-enumeration, say) and the next session end blocks again.
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Active(3));
        h.Coordinator.OnSessionEnding(Query());
        Assert.HasCount(2, h.Block.Calls);
    }

    [TestMethod]
    public void ADeviceChangeWaitsForTheCleanUpOfTheConnectItCancelled()
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

        // The user picked another device: the connect is cancelled and the pin waits for its clean-up.
        cancel.Cancel();
        h.Pump();
        Task<ControllerResult> pin = h.Coordinator.ChangeDeviceAsync(Devices.Address);
        h.Pump();

        Assert.IsFalse(pin.IsCompleted, "The pin moved while the clean-up was still running.");
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls);

        blocking.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(pin.IsCompleted);
        CollectionAssert.AreEqual(new[] { "allow", "block", "set-device:" + Devices.Address }, h.Block.Calls);
    }
}
