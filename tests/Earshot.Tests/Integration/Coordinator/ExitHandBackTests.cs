using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Exit's hand-back against the coordinator alone: the order, the cap, what is said when a step fails, and what
// Exit does when the AirPods are not connected. Fakes and a clock the test moves, as the rest of this folder; nothing
// reaches a device, a window or Task Scheduler.
[TestClass]
public sealed class ExitHandBackTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] DisconnectThenBlock = ["disconnect", "block"];
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] DisconnectOnly = ["disconnect"];
    private static readonly ExitHandBackPlan Plan = new(Budget, DisconnectWait, StreamingHeld: false);

    private static CoordinatorHarness Harness(bool handBack = true, bool safeMode = false)
    {
        var h = new CoordinatorHarness(safeMode);
        h.Settings.Update(s => s.HandBackOnShutdownAndSleep = handBack);
        h.Monitor.Set(Devices.Active(0));
        h.Start();
        return h;
    }

    private static void Arrange(CoordinatorHarness h, BootBlockStatus status, DeviceSnapshot snapshot)
    {
        h.Block.Status = status;
        h.Publish(snapshot);
        h.Coordinator.RefreshStatusAsync();
        h.Pump();
    }

    // Exit as the tray runs it: begin the shutdown with the plan, then wait for the coordinator to go idle.
    private static Task Exit(CoordinatorHarness h, ExitHandBackPlan? plan)
    {
        // The process ends after Exit, so the invariant a running coordinator keeps (something in hand that ends in a
        // block) no longer applies; what Exit owes instead is the notice, and each test asserts that.
        h.CheckInvariantOnPump = false;
        h.Coordinator.BeginShutdown(plan);
        Task idle = h.Coordinator.WhenIdleAsync();
        h.Pump();
        return idle;
    }

    [TestMethod]
    public void ExitWhileConnectedReleasesConfirmsThenBlocksAndHasNothingToSay()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Task idle = Exit(h, Plan);

        Assert.IsTrue(idle.IsCompleted, "Exit's wait did not end.");
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): disconnect S_OK, confirmed after"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): block sent at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): finished in"));
        Assert.IsNull(h.Coordinator.ClosingNotice);
        Assert.IsFalse(h.Coordinator.ExitHandBackRunning);
        Assert.IsFalse(h.Log.Has(LogLevel.Warn, "Closing while the AirPods are in use"), "Exit still left the nodes enabled in use.");
    }

    // The Started line names Exit and describes the world as it stood.
    [TestMethod]
    public void TheStartedLineNamesExit()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));

        Exit(h, Plan);

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "(Exit); render Active; nodes Allowed; streaming none; Block at boot on"));
    }

    [TestMethod]
    public void AHeldPlayFromAPhoneLinkIsNamedOnTheStartedLine()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));

        Exit(h, Plan with { StreamingHeld = true });

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "streaming held"));
    }

    // Block at boot off: the AirPods are still let go (release is not conditional on the block), and nothing is blocked
    // and nothing said, because the owner chose that.
    [TestMethod]
    public void BlockAtBootOffStillReleasesAndBlocksNothing()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(blockAtBoot: false), Devices.Active(1));
        h.Trace.Clear();

        Exit(h, Plan);

        CollectionAssert.AreEqual(DisconnectOnly, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): block not sent: "));
        Assert.IsNull(h.Coordinator.ClosingNotice);
    }

    // Before set-up there is no gate to block with: the AirPods are still let go.
    [TestMethod]
    public void NotSetUpStillReleases()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.NotSetUp(), Devices.Active(1));
        h.Trace.Clear();

        Exit(h, Plan);

        CollectionAssert.AreEqual(DisconnectOnly, h.Trace);
        Assert.IsNull(h.Coordinator.ClosingNotice);
    }

    // The setting off: Exit is what it was. In use, the nodes stay enabled and the closing notice says so.
    [TestMethod]
    public void HandBackOffLeavesExitAsItWas()
    {
        using CoordinatorHarness h = Harness(handBack: false);
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Exit(h, Plan);

        Assert.IsEmpty(h.Trace);
        Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, h.Coordinator.ClosingNotice);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back: off, so nothing runs for this Exit."));
    }

    // Exit begun without a plan (the message loop ending, never Exit itself) does not hand back.
    [TestMethod]
    public void ABeginShutdownWithoutAPlanDoesNotHandBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Exit(h, plan: null);

        Assert.IsEmpty(h.Trace);
        Assert.AreEqual(BlockCoordinator.ClosedWhileInUseMessage, h.Coordinator.ClosingNotice);
    }

    // Not connected: exactly the block before closing, no hand-back line, no notice.
    [TestMethod]
    public void NotConnectedIsExactlyTheBlockBeforeClosing()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Trace.Clear();

        Exit(h, Plan);

        CollectionAssert.AreEqual(BlockOnly, h.Trace);
        Assert.IsFalse(h.Log.Has(LogLevel.Info, "Hand-back (exit)"));
        Assert.IsNull(h.Coordinator.ClosingNotice);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Closing: the nodes are enabled and the AirPods are not in use."));
    }

    [TestMethod]
    public void NotConnectedWithTheNodesBlockedSendsNothing()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Blocked(), Devices.NotPresent(1));
        h.Trace.Clear();

        Exit(h, Plan);

        Assert.IsEmpty(h.Trace);
        Assert.IsNull(h.Coordinator.ClosingNotice);
    }

    // A block that did not take is the at-rest failure: it is said, and the raw code is in the log.
    [TestMethod]
    public void ABlockThatFailsIsSaidAndItsRawCodeLogged()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail("Could not block", [StepOutcomes.FromWin32("cm-disable:BTHENUM", 5)]));

        Exit(h, Plan);

        Assert.AreEqual(BlockCoordinator.ExitNotBlockedMessage, h.Coordinator.ClosingNotice);
        Assert.AreEqual(HandBackBlockOutcome.NotBlocked, h.Coordinator.LastHandBackOutcome?.Block);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block Failed: cm-disable:BTHENUM ERROR_ACCESS_DENIED"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Exit will say: " + BlockCoordinator.ExitNotBlockedMessage));
    }

    // Partial is seven of eight nodes: one still enabled, so it is not blocked.
    [TestMethod]
    public void APartialBlockIsNotBlocked()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.OnBlock = _ => Task.FromResult(new ControllerResult(OpStatus.Partial, "One entry vetoed", [StepOutcomes.FromConfigRet("cm-disable:SWD", 0x10)]));

        Exit(h, Plan);

        Assert.AreEqual(BlockCoordinator.ExitNotBlockedMessage, h.Coordinator.ClosingNotice);
    }

    // A block task that throws is recorded with its raw code and said, and Exit still ends.
    [TestMethod]
    public void ABlockThatThrowsIsSaidAndExitStillEnds()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.OnBlock = _ => throw new InvalidOperationException("gate unreachable");

        Task idle = Exit(h, Plan);

        Assert.IsTrue(idle.IsCompleted);
        Assert.AreEqual(BlockCoordinator.ExitNotBlockedMessage, h.Coordinator.ClosingNotice);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "the block could not be started"));
    }

    // A disconnect that is sent and never confirms is given up at its own share of the cap. The block still goes, as
    // it does at shut down, and the notice says the AirPods did not disconnect, not that they are unblocked.
    [TestMethod]
    public void ADisconnectThatNeverConfirmsStillBlocksAndSaysTheAirPodsDidNotDisconnect()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        var pending = new TaskCompletionSource<ConnectResult>();
        h.Connection.OnDisconnect = ct =>
        {
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        };
        h.Trace.Clear();

        Task idle = Exit(h, Plan);
        Assert.IsFalse(idle.IsCompleted, "The disconnect has not been given up yet.");
        h.Advance(DisconnectWait);

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "not confirmed within"));
        Assert.AreEqual(BlockCoordinator.ExitNotDisconnectedBlockedMessage, h.Coordinator.ClosingNotice);
    }

    // The cap is enforced: a disconnect that never returns and never notices cancellation ends Exit's wait at the
    // budget, logs "cut short" naming the disconnect, sends no block, and says the change did not finish.
    [TestMethod]
    public void ADisconnectThatNeverReturnsIsCutShortAtTheBudget()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Connection.OnDisconnect = _ => new TaskCompletionSource<ConnectResult>().Task;
        h.Block.Calls.Clear();
        ExitHandBackPlan plan = Plan with { DisconnectWait = Budget * 2 };

        Task idle = Exit(h, plan);
        Assert.IsFalse(idle.IsCompleted);
        h.Advance(Budget);

        Assert.IsTrue(idle.IsCompleted, "Exit's wait outlived the cap.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Hand-back (exit): cut short at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "still running: disconnect; block was not sent"));
        Assert.IsEmpty(h.Block.Calls, "A block was sent after the disconnect that comes first never finished.");
        Assert.AreEqual(BlockCoordinator.ClosedBeforeChangeEndedMessage, h.Coordinator.ClosingNotice);
    }

    // A block already sent and still running at the cap is left to finish in its own process: Exit ends, says the
    // change did not finish, and the block's own outcome is still recorded when it arrives.
    [TestMethod]
    public void ABlockStillRunningAtTheBudgetIsCutShortAndStillRecorded()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task idle = Exit(h, Plan);
        Assert.IsFalse(idle.IsCompleted);
        h.Advance(Budget);

        Assert.IsTrue(idle.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Hand-back (exit): cut short at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "block was sent at"));
        Assert.AreEqual(BlockCoordinator.ClosedBeforeChangeEndedMessage, h.Coordinator.ClosingNotice);

        pending.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the block that was still running finished"));
    }

    // Safe mode refuses every device action: nothing was tried, so nothing failed and nothing is said.
    [TestMethod]
    public void SafeModeSaysNothing()
    {
        using CoordinatorHarness h = Harness(safeMode: true);
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.OnBlock = _ => Task.FromResult(new ControllerResult(OpStatus.NotAttempted, "Safe mode: no device actions.", []));
        h.Connection.OnDisconnect = _ => Task.FromResult(new ConnectResult(ConnectOutcome.Failed, "Safe mode: no device actions.", []));

        Exit(h, Plan);

        Assert.IsNull(h.Coordinator.ClosingNotice);
    }

    // Exit while a connect is in flight that ends not connected: nothing is handed back, and the connect's own
    // clean-up and the block before closing are as they were.
    [TestMethod]
    public void ExitDuringAConnectThatDoesNotConnectHandsNothingBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Blocked(), Devices.NotPresent(1));
        var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Connection.Connects.Enqueue(ct =>
        {
            ct.Register(() => release.TrySetResult(Results.TimedOut()));
            return release.Task;
        });
        Task<ToggleReport> connect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(connect.IsCompleted);
        h.Connection.Calls.Clear();

        Task idle = Exit(h, Plan);
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        Assert.IsFalse(h.Connection.Calls.Any(c => !c.Connect), "Exit disconnected AirPods that never connected.");
        Assert.IsFalse(h.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
    }

    // Exit while a connect is in flight that the cancel did not stop, so the AirPods are connected when Exit's own turn
    // comes: they are handed back after the connect has ended.
    [TestMethod]
    public void ExitDuringAConnectThatReachesActiveHandsTheAirPodsBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Blocked(), Devices.NotPresent(1));
        var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Connection.Connects.Enqueue(ct =>
        {
            ct.Register(() =>
            {
                h.Monitor.Publish(Devices.Active(50));
                release.TrySetResult(Results.Connected());
            });
            return release.Task;
        });
        Task<ToggleReport> connect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsFalse(connect.IsCompleted);
        h.Trace.Clear();
        h.Block.Status = Statuses.Allowed();

        Task idle = Exit(h, Plan);
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        CollectionAssert.Contains(h.Trace, "disconnect", "The AirPods the cancelled connect left connected were not handed back.");
        Assert.IsLessThan(h.Trace.LastIndexOf("block"), h.Trace.IndexOf("disconnect"), "The block came before the release.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
    }

    // Exit while the user's own disconnect is in flight: the disconnect finishes as it always did, and is not repeated.
    [TestMethod]
    public void ExitDuringADisconnectDoesNotRepeatIt()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        var release = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Connection.OnDisconnect = _ => release.Task;
        Task<ToggleReport> disconnect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        Assert.IsFalse(disconnect.IsCompleted);
        h.Connection.Calls.Clear();

        Task idle = Exit(h, Plan);
        h.Pump();
        h.Monitor.Publish(Devices.Idle(2));
        release.SetResult(Results.Disconnected());
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        Assert.IsEmpty(h.Connection.Calls, "Exit sent a second disconnect on top of the one already in flight.");
        Assert.IsFalse(h.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
    }

    // A disconnect the cancel stopped before render left ACTIVE is not the AirPods let go: Exit hands them back.
    [TestMethod]
    public void ExitDuringADisconnectThatDidNotLetGoStillHandsBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        var stuck = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        h.Connection.OnDisconnect = ct =>
        {
            calls++;
            if (calls == 1)
            {
                ct.Register(() => stuck.TrySetCanceled(ct));
                return stuck.Task;
            }

            h.Monitor.Publish(Devices.Idle(70 + calls));
            return Task.FromResult(Results.Disconnected());
        };
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        Task<ToggleReport> disconnect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        Assert.IsFalse(disconnect.IsCompleted);

        Task idle = Exit(h, Plan);
        h.Pump();

        Assert.IsTrue(idle.IsCompleted);
        Assert.AreEqual(2, calls, "Exit did not send its own disconnect after the one it cancelled had not let go.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (exit): started at"));
    }

    // HandBackAsync is for the shut down and sleep messages; Exit's hand-back is started only by BeginShutdown.
    [TestMethod]
    public void HandBackAsyncRefusesTheExitTrigger()
    {
        using CoordinatorHarness h = Harness();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            h.Coordinator.HandBackAsync(HandBackTrigger.Exit, h.Time.GetUtcNow() + Budget, DisconnectWait).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void ExitHandBackAppliesOnlyWhileConnectedWithTheSettingOn()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        Assert.IsTrue(h.Coordinator.ExitHandBackApplies);

        h.Publish(Devices.Idle(2));
        Assert.IsFalse(h.Coordinator.ExitHandBackApplies);

        h.Publish(Devices.Active(3));
        h.Settings.Update(s => s.HandBackOnShutdownAndSleep = false);
        Assert.IsFalse(h.Coordinator.ExitHandBackApplies);
    }
}
