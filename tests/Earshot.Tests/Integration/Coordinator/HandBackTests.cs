using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The hand-back's own order, budgets and blockers, against fakes and a clock the test
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

    // Nothing runs, nothing is sent, and the reason is logged so a reader can
    // tell "off" from "never reached".
    [TestMethod]
    public void OffSendsNothingAndLogsWhy()
    {
        using CoordinatorHarness h = Harness(handBack: false);
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Connection.Calls.Clear();
        h.Block.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Connection.Calls);
        Assert.IsEmpty(h.Block.Calls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back: off"));
    }

    // Render ACTIVE runs the disconnect, then the block, in that order, and finishes.
    [TestMethod]
    public void ActiveRenderDisconnectsThenBlocksInOrder()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted, "The hand-back did not finish.");
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "confirmed after"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block sent at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
    }

    // Render not ACTIVE sends no disconnect, straight to the block.
    [TestMethod]
    public void NotInUseSkipsTheDisconnectAndBlocksAtOnce()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Trace.Clear();
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Connection.Calls);
        CollectionAssert.AreEqual(BlockOnly, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "nothing to disconnect"));
    }

    // The design sends the block regardless of whether the disconnect confirmed (today's code already blocks at
    // the query while the AirPods are in use, and gets seven of eight): a disconnect that never confirms (the
    // fake never publishes a snapshot where render has left ACTIVE) is still followed by a block, and a vetoed
    // sink entry is recorded honestly as Partial, never as "not sent" and never as an assumed Success.
    [TestMethod]
    public void TheBlockIsStillSentAfterAnUnconfirmedDisconnectAndAVetoedEntryIsRecordedAsPartial()
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
        h.Block.OnBlock = _ => Task.FromResult(new ControllerResult(OpStatus.Partial, "One entry vetoed", Array.Empty<StepOutcome>()));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();
        Assert.IsFalse(task.IsCompleted, "The disconnect has not been abandoned yet.");

        h.Advance(DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Block.Calls.Contains("block"),
            "Render is still ACTIVE (the disconnect never confirmed), but the block is sent anyway: refusing it " +
            "would leave every node enabled by choice.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "not confirmed within"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block Partial"));
    }

    // A disconnect that never confirms is abandoned at DisconnectHandBackWait; the block is still sent once
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

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "confirmed after"));
    }

    // The "return before the deadline without logging" mutation: a block that has not
    // completed by HandBackBudget is left running and the hold reports "cut short", naming the block.
    [TestMethod]
    public void ABlockStillRunningAtTheBudgetIsReportedCutShort()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20));
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

    // A non-cancellation fault from the disconnect (a COM call failing, say) is never a silent catch: it is
    // recorded with its raw code, and the procedure still goes on to send the block, which is the at-rest action.
    [TestMethod]
    public void AnUnexpectedDisconnectFaultIsRecordedAndTheProcedureStillSendsTheBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Block.Calls.Clear();
        h.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        h.Connection.OnDisconnect = _ => throw new InvalidOperationException("COM call failed");

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Block.Calls.Contains("block"), "The block still runs after an unexpected disconnect fault.");
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "disconnect failed"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
    }

    // A fault starting the block itself (a disposed system worker, say) is never a silent catch either.
    [TestMethod]
    public void AFaultStartingTheBlockIsRecordedWithItsRawCode()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.OnBlock = _ => throw new ObjectDisposedException("SystemWorker");

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "the block could not be started"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "not sent:"));
    }

    // A block task that faults after being sent (rather than timing out) is also recorded, not left as an
    // unobserved task exception with no finished, cut-short or error line at all.
    [TestMethod]
    public void ABlockTaskThatFaultsAfterBeingSentIsRecorded()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.OnBlock = _ => Task.FromException<ControllerResult>(new InvalidOperationException("gate pipe broke"));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "the block faulted"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "faulted:"));
    }

    // The started line reads the real streaming state passed in, rather than always saying "streaming none".
    [TestMethod]
    public void TheStartedLineReadsTheRealStreamingStateWhenAHeldLinkIsPassedIn()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait, streamingHeld: true);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "streaming held"));
        Assert.IsFalse(h.Log.Has(LogLevel.Info, "streaming none"));
    }

    // A suspend or session end arriving while another device action (an allow that a connect sends, say) is
    // already running waits for it, never running beside it on the same SystemWorker; the order is pinned
    // through the shared trace.
    [TestMethod]
    public void AHandBackWaitsForAnOperationAlreadyInFlightRatherThanRunningBesideIt()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Trace.Clear();
        var pendingConnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => pendingConnect.Task);

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsTrue(h.Coordinator.IsBusy, "The connect must be the operation in flight.");

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromSeconds(10), DisconnectWait);
        h.Pump();

        Assert.IsFalse(handBack.IsCompleted, "The hand-back must wait for the connect already in flight to finish.");
        Assert.IsFalse(h.Trace.Contains("block"), "Nothing the hand-back sends has run yet.");
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "waiting for connect to finish"));
        Assert.IsTrue(h.Coordinator.HandBackInProgress, "HandBackInProgress must already be set while still waiting for the connect, not only once the exclusive slot is claimed.");

        pendingConnect.SetResult(Results.Connected());
        h.Pump();

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(handBack.IsCompleted);
        CollectionAssert.Contains(h.Trace, "connect");
        CollectionAssert.Contains(h.Trace, "block");
        Assert.IsTrue(h.Trace.IndexOf("connect") < h.Trace.IndexOf("block"),
            "The operation already in flight finishes before the hand-back's own block runs; never beside it.");
    }

    // A query-time block that completes before WM_ENDSESSION leaves _sessionBlock holding a reference to
    // an already-finished task; the hand-back must still run and the reply must still return within its own
    // cap, never spin re-awaiting that finished task forever. Timeout is a permanent safety net for this exact
    // defect class (a spin that would otherwise hang the whole test run), not part of the red-check alone.
    [TestMethod]
    [Timeout(5000)]
    public void AHandBackRunsAndReturnsWithinTheCapWhenTheQueryTimeBlockAlreadyCompleted()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls, "The not-in-use query already queued and completed its block (the fake resolves synchronously).");

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted,
            "The hand-back must run and the reply must return within the cap, not spin re-awaiting the already-finished query block.");
        Assert.HasCount(1, h.Block.Calls, "The query's own block is reused, not sent again.");
    }

    // The wait for the operation already in flight is bounded by the one shared deadline; if it has not
    // finished by then, the hand-back is cut short before it ever started, and never sends anything, then or
    // later once the abandoned claim on the exclusive slot finally gets its turn.
    [TestMethod]
    public void AHandBackIsCutShortBeforeItStartsWhenTheOperationInFlightOutlivesTheDeadline()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Trace.Clear();
        var pendingConnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => pendingConnect.Task);

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsTrue(h.Coordinator.IsBusy, "The connect must be the operation in flight.");

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(500), DisconnectWait);
        h.Pump();
        Assert.IsFalse(handBack.IsCompleted);

        h.Advance(TimeSpan.FromMilliseconds(500));
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted, "The hand-back must return once the shared deadline passes, not wait for the operation in flight forever.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "cut short before it started: waiting for connect."));
        Assert.IsFalse(h.Trace.Contains("block"), "Nothing was ever sent: the hand-back never started.");
        Assert.IsFalse(h.Coordinator.HandBackInProgress, "HandBackInProgress must clear once the reply has returned.");

        pendingConnect.SetResult(Results.Connected());
        h.Pump();
        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsFalse(h.Trace.Contains("block"),
            "The abandoned claim must still send nothing once it finally runs: the deadline had already passed.");
    }

    // With the setting on, a query while the AirPods are in use still cancels an operation already in
    // flight, exactly as it would if a block were being sent right now, even though no block is actually sent
    // at the query in this case.
    [TestMethod]
    public void AQueryWhileInUseWithHandBackOnStillStopsAnOperationInFlight()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        var pendingConnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(ct =>
        {
            ct.Register(() => pendingConnect.TrySetCanceled(ct));
            return pendingConnect.Task;
        });

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Assert.IsTrue(h.Coordinator.IsBusy, "The connect must be the operation in flight.");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "is stopped."), "The operation in flight must be stopped, exactly as it would be if a block were being sent now.");
        Assert.IsTrue(toggle.IsCompleted, "The stopped connect must have completed (cancelled), not still be waiting on the never-resolved fake.");
    }

    // The resume check runs only with the hand-back setting on; with it off, today's code has no resume
    // check at all, so this must be the byte-for-byte "off" path for it too.
    [TestMethod]
    public void ResumeCheckDoesNothingWithTheSettingOff()
    {
        using CoordinatorHarness h = Harness(handBack: false);
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        Task task = h.Coordinator.ResumeCheckAsync();
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (resume): off, so the resume check does not run"));
    }

    // A block completed inside the budget logs "finished in", never "cut short".
    [TestMethod]
    public void ABlockCompletedInsideTheBudgetLogsFinished()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsFalse(h.Log.Has(LogLevel.Warn, "cut short"));
    }

    // Each blocker still lets the disconnect run, and names why the block was not sent.
    [TestMethod]
    public void BlockAtBootOffStillDisconnectsButSendsNoBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(blockAtBoot: false), Devices.Active(1));
        h.Block.Calls.Clear();
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
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

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
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

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsFalse(h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block not sent"));
    }

    // No protection step (ProtectOn, ReadServices) runs inside a hand-back, even though the coordinator's
    // own start-up check already settled its one-time reverify beforehand.
    [TestMethod]
    public void NoProtectionStepRunsInsideAHandBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Settings.Update(s => s.ProtectAudioQuality = true);
        h.Protection.State = AudioProtectionState.NotProtected;
        h.Protection.Applies.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsEmpty(h.Protection.Applies);
    }

    // A second hand-back started while one is already running does nothing; the first one finishes normally.
    [TestMethod]
    public void ASecondHandBackWhileOneIsRunningDoesNothing()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task first = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();
        Assert.IsTrue(h.Coordinator.HandBackInProgress);

        Task second = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
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

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted);
        Assert.HasCount(1, h.Block.Calls, "The query's own block is reused, not sent again.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "nothing to disconnect"));
    }

    // Render is re-read at WM_ENDSESSION regardless of what the query found. Sound started on the pinned
    // container between the query (not in use, so it queued a block) and WM_ENDSESSION is still disconnected;
    // the block already in flight is still the one this hand-back waits for, never a second one.
    [TestMethod]
    public void WmEndSessionStillDisconnectsSoundThatStartedAfterTheQueryEvenWhileReusingItsBlock()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls, "The not-in-use query already queued its block.");

        // Sound started after the query, before WM_ENDSESSION arrives.
        h.Publish(Devices.Active(2));
        h.Connection.Calls.Clear();

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted);
        Assert.Contains((false, Devices.Container), h.Connection.Calls, "Sound that started after the query must still be disconnected.");
        Assert.HasCount(1, h.Block.Calls, "The query's own block is still reused, not sent again.");
    }

    // The result written is the result observed: a query-time block that came back Partial is retried exactly
    // once before the reply returns, and the line names the real outcome of whichever attempt actually finished
    // last, never an assumed Success.
    [TestMethod]
    public void WmEndSessionRetriesAPartialQueryTimeBlockOnceAndReportsTheRetrysOutcome()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        var outcomes = new Queue<ControllerResult>(new[]
        {
            new ControllerResult(OpStatus.Partial, "One entry vetoed", Array.Empty<StepOutcome>()),
            ControllerResult.Ok("Blocked at boot"),
        });
        h.Block.OnBlock = _ => Task.FromResult(outcomes.Dequeue());

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls);

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted);
        Assert.HasCount(2, h.Block.Calls, "A Partial query-time block is sent once more, and only once.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "the block queued at the query was Partial, so it is sent once more"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block Success"), "The retry succeeded, so the last outcome recorded is the retry's own.");
    }

    // As above, but the retry fails too: the line names Failed, never Success, because the gate never said so.
    [TestMethod]
    public void WmEndSessionRetriesAFailedQueryTimeBlockOnceAndNeverClaimsSuccessWhenTheRetryAlsoFails()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Block.OnBlock = _ => Task.FromResult(new ControllerResult(OpStatus.Failed, "Access denied", Array.Empty<StepOutcome>()));

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls);

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted);
        Assert.HasCount(2, h.Block.Calls, "A Failed query-time block is sent once more, and only once.");
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "the block queued at the query was Failed, so it is sent once more"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block Failed"));
        Assert.IsFalse(
            h.Log.Entries.Any(e => e.Message.StartsWith("Hand-back (shutdown): finished", StringComparison.Ordinal) && e.Message.Contains("block Success", StringComparison.Ordinal)),
            "Never Success unless the gate said so: both attempts failed.");
    }

    // The target of the "skip the disconnect" mutation, the most direct: render ACTIVE must call
    // IConnectionController.DisconnectAsync.
    [TestMethod]
    public void ActiveRenderAlwaysCallsDisconnect()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Connection.Calls.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.Contains((false, Devices.Container), h.Connection.Calls);
    }

    // The sleep trigger runs the same procedure under its own prefix and its own budgets.
    [TestMethod]
    public void SuspendRunsTheSameProcedureUnderTheSleepPrefix()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Active(1));
        h.Trace.Clear();

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(750));
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        CollectionAssert.AreEqual(DisconnectThenBlock, h.Trace);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (sleep): started at"));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back (sleep): finished in"));
    }

    // The resume check blocks at once, no idle grace, when the nodes read enabled and not in use.
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

    // Render ACTIVE at resume leaves the nodes to the idle rule and only logs the evidence line.
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

    // Nodes already blocked, or Block at boot off, is one log line and no block.
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

    // A block still running from a cut-short sleep hand-back is waited for first, never raced.
    [TestMethod]
    public void ResumeCheckWaitsForABlockStillRunningFromASuspendHandBack()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pending = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pending.Task;

        Task suspend = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.Zero, TimeSpan.FromMilliseconds(750));
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
