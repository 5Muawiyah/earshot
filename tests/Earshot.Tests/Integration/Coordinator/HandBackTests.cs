using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// T2 to T4, T7, T9, T10: the hand-back's own order, budgets and blockers, against fakes and a clock the test
// moves, exactly as the rest of this folder tests the coordinator. Nothing here touches a device, a window or
// Task Scheduler.
[TestClass]
public sealed class HandBackTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] DisconnectThenBlock = ["disconnect", "block"];
    private static readonly string[] BlockOnly = ["block"];

    // Starts with the AirPods in use (render ACTIVE), so the coordinator's own start-up check (which runs once,
    // against the first good snapshot) has nothing to block and its protection reverify is a no-op against the
    // harness's default settings. Every test then arranges its own scenario with Arrange, never with the first
    // Publish after this, so the start-up check never fires a second time and never contaminates what a test
    // means to prove about the hand-back alone.
    private static CoordinatorHarness Harness(bool handBack = true)
    {
        var h = new CoordinatorHarness();
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

    // T2 (the setting-off branch): nothing runs, nothing is sent, and the reason is logged so a reader can
    // tell "off" from "never reached".
    [TestMethod]
    public void OffSendsNothingAndLogsWhy()
    {
        using CoordinatorHarness h = Harness(handBack: false);
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Connection.Calls.Clear();
        h.Block.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Connection.Calls);
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back: off"));
    }

    // T3: render ACTIVE runs the disconnect, then the block, in that order, and finishes.
    [TestMethod]
    public void ActiveRenderDisconnectsThenBlocksInOrder()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted, "The hand-back did not finish.");
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "confirmed after"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block sent at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
    }

    // T3: render not ACTIVE sends no disconnect, straight to the block.
    [TestMethod]
    public void NotInUseSkipsTheDisconnectAndBlocksAtOnce()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Trace.Clear();
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Connection.Calls);
        CollectionAssert.AreEqual(BlockOnly, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "nothing to disconnect"));
    }

    // T3, and the target of the "block before confirmation" mutation: a disconnect that never confirms (the
    // fake never publishes a snapshot where render has left ACTIVE) must never be followed by a block while
    // render still reads ACTIVE.
    [TestMethod]
    public void TheBlockIsNeverSentWhileRenderStillReadsActiveAfterTheDisconnectTimesOut()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.Calls.Clear();
        var pending = new TaskCompletionSource<ConnectResult>();
        h.Connection.OnDisconnect = ct =>
        {
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        };

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();
        Assert.IsFalse(task.IsCompleted, "The disconnect has not been abandoned yet.");

        h.Advance(DisconnectWait);

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"), "Render is still ACTIVE (the disconnect never confirmed), so no block may be sent.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "not confirmed within"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block not sent"));
    }

    // T4: a disconnect that never confirms is abandoned at DisconnectHandBackWait; the block is still sent once
    // render has actually left ACTIVE by then.
    [TestMethod]
    public void ADisconnectThatConfirmsStillLetsTheBlockRun()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();
        h.Connection.OnDisconnect = ct =>
        {
            // Confirmed from the fake's own default behaviour (publishes Idle), but only once awaited; the
            // token is not observed, so the request is treated as having gone out and come back in time.
            h.Monitor.Publish(Devices.Idle(2));
            return Task.FromResult(Results.Disconnected());
        };

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "confirmed after"));
    }

    // T4, and the target of the "return before the deadline without logging" mutation: a block that has not
    // completed by HandBackBudget is left running and the hold reports "cut short", naming the block.
    [TestMethod]
    public void ABlockStillRunningAtTheBudgetIsReportedCutShort()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20));
        h.Pump();
        Assert.IsFalse(task.IsCompleted, "The block is still running.");

        h.Advance(TimeSpan.FromMilliseconds(50));

        Assert.IsTrue(task.IsCompleted, "The hold must still return once its own budget has passed.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "cut short"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "block was sent at"));

        // The block itself is not abandoned: it is a gate request already sent, so it runs to completion and its
        // outcome is still recorded once it arrives.
        pending.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "the block that was still running finished"));
    }

    // T4: a block completed inside the budget logs "finished in", never "cut short".
    [TestMethod]
    public void ABlockCompletedInsideTheBudgetLogsFinished()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsFalse(h.Log.Has(LogLevel.Warn, "cut short"));
    }

    // T7: each blocker still lets the disconnect run, and names why the block was not sent.
    [TestMethod]
    public void BlockAtBootOffStillDisconnectsButSendsNoBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(blockAtBoot: false), Devices.Active(1));
        h.Block.Calls.Clear();
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.Contains((false, Devices.Container), h.Connection.Calls);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block not sent"));
    }

    [TestMethod]
    public void NotSetUpStillDisconnectsButSendsNoBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.NotSetUp(), Devices.Active(1));
        h.Block.Calls.Clear();
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.Contains((false, Devices.Container), h.Connection.Calls);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
    }

    [TestMethod]
    public void AlreadyBlockedSendsNoSecondBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Blocked(), Devices.Idle(1));
        h.Block.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block not sent"));
    }

    // T9: no protection step (ProtectOn, ReadServices) runs inside a hand-back, even though the coordinator's
    // own start-up check already settled its one-time reverify beforehand.
    [TestMethod]
    public void NoProtectionStepRunsInsideAHandBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Settings.Update(s => s.ProtectAudioQuality = true);
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Protection.Applies.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Protection.Applies);
    }

    // T6: a second hand-back started while one is already running does nothing; the first one finishes normally.
    [TestMethod]
    public void ASecondHandBackWhileOneIsRunningDoesNothing()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task first = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();
        Assert.IsTrue(h.Coordinator.HandBackInProgress);

        Task second = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(second.IsCompleted, "A re-entrant call must return at once.");
        Assert.HasCount(1, h.Block.Calls, "Only the first hand-back's block was sent.");

        pending.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();
        Assert.IsTrue(first.IsCompleted);
        Assert.IsFalse(h.Coordinator.HandBackInProgress);
    }

    // Section 3.3: with the setting on, a query while the AirPods are in use issues no block (it would be
    // vetoed on the sink entry); the hand-back at WM_ENDSESSION is what disconnects and blocks, in order.
    [TestMethod]
    public void AQueryWhileInUseWithHandBackOnIssuesNoBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        Assert.IsFalse(h.Block.Calls.Contains("block"), "A block at the query while in use would be vetoed; the hand-back handles it instead.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "no block issued at the query"));
    }

    // Section 3.3: a query while not in use still blocks at once, as today, gaining the query-to-end gap.
    [TestMethod]
    public void AQueryWhileNotInUseWithHandBackOnStillBlocksAsToday()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        Assert.IsTrue(h.Block.Calls.Contains("block"));
    }

    // Section 3.3 / 8.2: the block a not-in-use query already queued is the one the hand-back at WM_ENDSESSION
    // waits for; nothing sends a second block.
    [TestMethod]
    public void WmEndSessionReusesTheQueryTimeBlockRatherThanSendingASecondOne()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls);

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted);
        Assert.HasCount(1, h.Block.Calls, "The query's own block is reused, not sent again.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "nothing to disconnect"));
    }

    // T10: the target of the "skip the disconnect" mutation, the most direct: render ACTIVE must call
    // IConnectionController.DisconnectAsync.
    [TestMethod]
    public void ActiveRenderAlwaysCallsDisconnect()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.Contains((false, Devices.Container), h.Connection.Calls);
    }

    // T12: the sleep trigger runs the same procedure under its own prefix and its own budgets.
    [TestMethod]
    public void SuspendRunsTheSameProcedureUnderTheSleepPrefix()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(750));
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (sleep): started at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (sleep): finished in"));
    }

    // T12: the resume check blocks at once, no idle grace, when the nodes read enabled and not in use.
    [TestMethod]
    public void ResumeCheckBlocksAtOnceWhenNodesAreEnabledAndNotInUse()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        Task task = h.Coordinator.ResumeCheckAsync();
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.Contains("block", h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (resume): the nodes were enabled"));
    }

    // T12: render ACTIVE at resume leaves the nodes to the idle rule and only logs the evidence line.
    [TestMethod]
    public void ResumeCheckLeavesActiveRenderToTheIdleRule()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.Calls.Clear();

        Task task = h.Coordinator.ResumeCheckAsync();
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (resume): connected at resume; the resume check did not hold"));
    }

    // T12: nodes already blocked, or Block at boot off, is one log line and no block.
    [TestMethod]
    public void ResumeCheckLogsOnceWhenThereIsNothingToDo()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Blocked(), Devices.Idle(1));
        h.Block.Calls.Clear();

        Task task = h.Coordinator.ResumeCheckAsync();
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (resume): nothing to do"));
    }

    // T12: a block still running from a cut-short sleep hand-back is waited for first, never raced.
    [TestMethod]
    public void ResumeCheckWaitsForABlockStillRunningFromASuspendHandBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task suspend = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, TimeSpan.Zero, TimeSpan.FromMilliseconds(750));
        h.Pump();
        Assert.IsTrue(suspend.IsCompleted, "The suspend hand-back itself returns (cut short) once its own budget passes.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "cut short"));

        Task resume = h.Coordinator.ResumeCheckAsync();
        h.Pump();
        Assert.IsFalse(resume.IsCompleted, "The resume check must wait for the block the suspend hand-back left running.");
        Assert.HasCount(1, h.Block.Calls, "Nothing has sent a second block while the first is still in flight.");

        // The fake's own status mutation is bypassed by overriding OnBlock directly, so the test sets what the
        // gate would have: the nodes read Blocked once the cut-short block actually finishes.
        h.Block.Status = Statuses.Blocked();
        pending.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.Pump();
        Assert.IsTrue(resume.IsCompleted);
        Assert.HasCount(1, h.Block.Calls, "The resume check found the nodes already blocked and sent none of its own.");
    }
}
