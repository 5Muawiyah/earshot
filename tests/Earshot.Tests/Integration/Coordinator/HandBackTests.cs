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

    // A fault none of HandBackCoreAsync's own steps catches (an OperationCanceledException the block task ends
    // in, which WaitForBlockAsync's own catch deliberately excludes, since that type is reserved for the shared
    // deadline elsewhere) used to escape HandBackAsync itself: _handingBack was set to false only after the
    // await, on the line straight after the try/catch, so an exception neither catch there matched left it
    // stuck true forever, every later action refused as "a hand-back is already running" until the process
    // restarted. Every exit now runs through a finally instead.
    [TestMethod]
    public void AFaultThatEscapesTheProcedureStillClearsHandingBackSoTheNextSessionEndRuns()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.OnBlock = _ => Task.FromException<ControllerResult>(new OperationCanceledException("unexpected"));

        Task first = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(first.IsCompleted, "The first hand-back's own outer task must complete, faulted or not, not hang.");
        Assert.IsFalse(h.Coordinator.HandBackInProgress,
            "HandBackInProgress must clear even though the procedure faulted, or every later click is refused until restart.");

        h.Block.OnBlock = null;
        h.Block.Calls.Clear();
        Task second = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(second.IsCompleted, "A second session end after the fault must actually run.");
        Assert.IsTrue(h.Block.Calls.Contains("block"), "The second hand-back must have reached its own block step, not been refused as already running.");
        Assert.IsFalse(h.Log.Has(LogLevel.Debug, "a hand-back is already running; this one does nothing."));
    }

    // _handingBack and _sleeping used to be set, and the first Changed raised, before the try/finally began: a
    // subscriber's own Changed handler throwing there escaped the whole method without ever reaching the
    // finally that clears them, leaving HandBackInProgress stuck true and every later hand-back doing nothing
    // (with the setting on, the query defers to it too, so the next shut down would send no block at all).
    [TestMethod]
    public void AChangedHandlerThatThrowsWhenTheHandBackStartsStillLetsTheFlagsClearSoTheNextHandBackRuns()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));

        bool thrown = false;
        h.Coordinator.Changed += (_, _) =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("a subscriber's own bug");
            }
        };

        Task first = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(thrown, "The throwing handler never ran.");
        Assert.IsTrue(first.IsCompleted, "The first hand-back's own outer task must complete, not hang.");
        Assert.IsFalse(h.Coordinator.HandBackInProgress,
            "HandBackInProgress must clear even though a Changed subscriber faulted, or every later hand-back is refused until restart.");

        h.Block.Calls.Clear();
        Task second = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(second.IsCompleted);
        Assert.IsTrue(h.Block.Calls.Contains("block"), "The second hand-back must actually send a block, not defer to a setting nothing acts on again.");
        Assert.IsFalse(h.Log.Has(LogLevel.Debug, "a hand-back is already running; this one does nothing."));
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
        // The real wait PumpAfterRealHop exists for, not merely a posted continuation: see its own comment.
        h.PumpAfterRealHop(() => handBack.IsCompleted);

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(handBack.IsCompleted);
        CollectionAssert.Contains(h.Trace, "connect");
        CollectionAssert.Contains(h.Trace, "block");
        Assert.IsTrue(h.Trace.IndexOf("connect") < h.Trace.IndexOf("block"),
            "The operation already in flight finishes before the hand-back's own block runs; never beside it.");
    }

    // The same defect as the test above, in the exact shape that used to catch even a hand-back that had
    // genuinely started: an operation already in flight frees the exclusive slot one tick before the shared
    // deadline, so the hand-back claims the slot and starts with a tick to spare, then its own block step is
    // still pending one tick later when that same deadline passes. On the old code, Task.Delay(Remaining(deadline))
    // was created once, at the very start, before the wait for the slot; WaitForBlockAsync's own block.WaitAsync
    // timer, created only once the slot was claimed, lands on the exact same due time, and ManualTime's own
    // tie-break (equal due times fire in registration order) let the independent Task.Delay win the race even
    // though the hand-back's own honest cut-short had already run. The honest cut-short, naming "block", must
    // still fire; the misleading "cut short before it started" must never join it.
    [TestMethod]
    public void AHandBackGrantedTheSlotOneTickBeforeItsOwnDeadlineNeverLogsCutShortBeforeItStartedBesideItsHonestOne()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pendingConnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => pendingConnect.Task);
        var pendingBlock = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pendingBlock.Task;

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        DateTimeOffset deadline = h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(50);
        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, deadline, DisconnectWait);
        h.Pump();
        Assert.IsFalse(handBack.IsCompleted, "Still waiting for the connect already in flight.");

        // The slot is granted one tick (one millisecond: the smallest unit "cut short at N ms" can even show,
        // since Ms() truncates to whole milliseconds, and the smallest a real Task.WaitAsync(TimeSpan) timeout
        // reliably holds off for) before the shared deadline. The wait loop's own completion needs the real
        // wait PumpAfterRealHop exists for (see CoordinatorHarness's own comment on it): a plain Pump() here
        // can leave it still suspended, and the deadline firing a tick later would then cancel that very wait,
        // reproducing a different, narrower race than the one this test means to prove. Waiting for the block
        // call itself confirms the slot was actually claimed and the body genuinely started.
        h.Advance(TimeSpan.FromMilliseconds(49));
        pendingConnect.SetResult(Results.Connected());
        h.PumpAfterRealHop(() => h.Block.Calls.Contains("block"));
        Assert.IsTrue(h.Coordinator.HandBackInProgress, "The hand-back must have claimed the slot and genuinely started.");
        Assert.IsFalse(handBack.IsCompleted, "Still waiting on its own block, one tick later.");

        // One tick later, the shared deadline passes while the block step is still pending.
        h.Advance(TimeSpan.FromMilliseconds(1));

        Assert.IsTrue(handBack.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "cut short at 50 ms"), "The honest cut-short, naming the block step, must still fire.");
        Assert.IsFalse(h.Log.Has(LogLevel.Warn, "cut short before it started"),
            "Granted the slot before the deadline, so the hand-back genuinely started; the misleading line must never appear beside the honest one.");
    }

    // t0 is taken once, at the moment HandBackAsync itself is called, not once the exclusive slot is actually
    // claimed: "finished in" must count the whole hold, including any wait behind an operation already in
    // flight. Proved with an exact figure, not merely a "the line exists" check: the manual clock only moves
    // when the test moves it, so 3000 ms in the logged line can only be the 3 seconds spent waiting, never a
    // coincidence of wall-clock timing.
    [TestMethod]
    public void FinishedInCountsTheWholeHoldIncludingTheWaitForAnOperationAlreadyInFlight()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pendingConnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => pendingConnect.Task);

        Task<ToggleReport> toggle = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromSeconds(30), DisconnectWait);
        h.Pump();
        Assert.IsFalse(handBack.IsCompleted, "Still waiting for the connect already in flight.");

        h.Advance(TimeSpan.FromSeconds(3));
        pendingConnect.SetResult(Results.Connected());
        h.PumpAfterRealHop(() => handBack.IsCompleted);

        Assert.IsTrue(toggle.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "finished in 3000 ms"),
            "The 3 seconds spent waiting for the connect already in flight must be counted, not only the work that ran once the slot was actually claimed.");
    }

    // A query-time block that completes before WM_ENDSESSION leaves _sessionBlock holding a reference to an
    // already-finished task, reused rather than sent again. This alone does not reproduce the spin the review
    // named (that needs a second operation actually queued through RunExclusiveAsync while the hand-back is
    // running and _current is still null, which the next test below constructs): kept as its own, narrower
    // regression for the reuse path, not as proof of that defect. Timeout is a permanent safety net regardless.
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

    // The spin _sessionBlock's own staleness causes is only reachable while _handingBack is true and something
    // else calls into RunExclusiveAsync: reusing an already-completed query-time block by itself (the test
    // above) never does that. This reproduces it for real: a first session end (render not ACTIVE) queues and
    // completes a block, leaving _sessionBlock referencing that finished task; it is then cancelled, which
    // clears every session-end flag except _sessionBlock itself. A second session end,
    // now with render ACTIVE, takes the hand-back's normal disconnect-then-block path rather than the reuse
    // branch; from inside the disconnect step (reentrant, the same trick the M5 tests use), a second, unrelated
    // operation is queued through RunExclusiveAsync while _handingBack is still true and _current is still null
    // -- exactly the state that made the old, unguarded "_current ?? _sessionBlock ?? _handBackInFlight" pick
    // the finished _sessionBlock over the hand-back that was actually running.
    [TestMethod]
    [Timeout(5000)]
    public void AnOperationQueuedDuringTheHandBackDoesNotSpinWhenAnEarlierCancelledSessionEndLeftASessionBlockStale()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls, "The first, cancelled session end's own query must have queued and completed a block.");

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: false, ending: false, flags: 0));
        h.Pump();

        h.Publish(Devices.Active(2));
        h.Block.Calls.Clear();
        h.Connection.Calls.Clear();

        Task<ToggleReport>? second = null;
        bool reentered = false;
        h.Connection.OnDisconnect = _ =>
        {
            // Fired reentrant, from inside the hand-back's own disconnect step: _current is the hand-back's own
            // exclusive slot at this point, _sessionBlock is the stale completed task from the first, cancelled
            // session end, and _handingBack is true. Not pumped here: only started, so RunExclusiveAsync's own
            // wait loop is the thing under test, not a nested pump. Only the first call reenters: the second
            // toggle's own eventual disconnect must reach this same fake too, and must not recurse forever.
            if (!reentered)
            {
                reentered = true;
                second = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false), CancellationToken.None);
            }

            return Task.FromResult(Results.Disconnected());
        };

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(handBack.IsCompleted, "The hand-back itself must not spin either.");
        Assert.IsNotNull(second, "The reentrant disconnect hook never ran.");
        Assert.IsTrue(second.IsCompleted, "The operation queued from inside the disconnect step must not have spun re-awaiting the stale, completed _sessionBlock.");
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

    // The coordinator itself refuses an action that could enable the nodes while a sleep hold is up, not only
    // the tray's own guard in front of it: otherwise an action reaching the coordinator directly would simply
    // queue behind the hand-back through RunExclusiveAsync's own wait and run once it finishes, an allow
    // landing as the machine actually sleeps rather than never running at all.
    [TestMethod]
    public void ACoordinatorLevelConnectIsRefusedDuringASleepHoldRatherThanQueuedBehindIt()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        var pendingBlock = new TaskCompletionSource<ControllerResult>();
        h.Block.OnBlock = _ => pendingBlock.Task;

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromSeconds(10), DisconnectWait);
        h.Pump();
        Assert.IsTrue(h.Coordinator.HandBackInProgress, "The hand-back's own block must still be pending.");
        h.Connection.Calls.Clear();

        Task<ToggleReport> connect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsTrue(connect.IsCompleted, "The connect must be refused at once, not queued behind the hand-back.");
        Assert.IsEmpty(h.Connection.Calls, "Nothing was sent: the connect never reached the fake connection controller.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connect: not started, because Earshot is handing the AirPods back."));

        pendingBlock.SetResult(ControllerResult.Ok("Blocked at boot"));
        h.PumpAfterRealHop(() => handBack.IsCompleted);
    }

    // HandBackInProgress itself only covers HandBackAsync's own procedure, which has now finished: the gap the
    // test above cannot reach. Windows never promises the machine has actually finished suspending by the time
    // the suspend handler's reply returns, still less that it has already resumed, so an allow-capable action
    // reaching the coordinator in that gap must still be refused, exactly as it was while the hand-back itself
    // was running, until PBT_APMRESUMEAUTOMATIC actually arrives.
    [TestMethod]
    public void ACoordinatorLevelConnectStaysRefusedAfterTheSuspendHandBackFinishesUntilResume()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.Suspend, h.Time.GetUtcNow() + TimeSpan.FromSeconds(10), DisconnectWait);
        h.PumpAfterRealHop(() => handBack.IsCompleted);
        Assert.IsFalse(h.Coordinator.HandBackInProgress, "The procedure itself has finished.");
        h.Connection.Calls.Clear();

        Task<ToggleReport> connect = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsTrue(connect.IsCompleted, "Refused at once, not queued.");
        Assert.IsEmpty(h.Connection.Calls, "Nothing was sent: the machine has not resumed yet.");
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "connect: not started, because the machine is asleep."));

        _ = h.Coordinator.ResumeCheckAsync();
        h.Pump();
        h.Connection.Calls.Clear();

        Task<ToggleReport> afterResume = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();

        Assert.IsTrue(afterResume.IsCompleted);
        Assert.IsTrue(h.Connection.Calls.Count > 0, "The connect must actually reach the fake connection controller now that resume has run, not be refused again.");
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

    // With the setting on, a query while the AirPods are in use issues no block (it would be
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

    // A query while not in use still blocks at once, as today, gaining the query-to-end gap.
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

    // The block a not-in-use query already queued is the one the hand-back at WM_ENDSESSION
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

    // blockAlreadySentAt only ever describes what has actually happened before the disconnect step runs: on the
    // reuse branch (a block already queued at the query) it is the query's own issue time, so a disconnect
    // that is itself cut short here must say "block was sent at", the query's own time, never "block was not
    // sent", which would say the opposite of what happened, since the block genuinely is already on its way.
    [TestMethod]
    public void WmEndSessionsReuseBranchNamesTheQueryBlockAsAlreadySentWhenItsOwnDisconnectIsCutShort()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.Calls.Clear();

        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();
        Assert.HasCount(1, h.Block.Calls, "The not-in-use query already queued and completed its block.");

        // Sound started after the query, so render is ACTIVE by WM_ENDSESSION: the reuse branch's own disconnect
        // step actually runs this time (see the test above), rather than "nothing to disconnect".
        h.Publish(Devices.Active(2));
        h.Connection.Calls.Clear();
        var pendingDisconnect = new TaskCompletionSource<ConnectResult>();
        h.Connection.OnDisconnect = _ => pendingDisconnect.Task;

        Task handBack = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        h.Pump();
        Assert.IsFalse(handBack.IsCompleted, "The disconnect has not returned.");

        h.Advance(TimeSpan.FromMilliseconds(50));

        Assert.IsTrue(handBack.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "cut short"));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "block was sent at"),
            "The query's own block genuinely is already sent; saying otherwise would be backwards.");
        Assert.IsFalse(h.Log.Has(LogLevel.Warn, "block was not sent"));
    }

    // A Partial or Failed block outcome that reaches this line the ordinary way (not through the retry path
    // below, which already proves the retry's own outcome) names the first vetoed step and its raw code, not
    // merely the bare status: a reader must be able to tell which of the eight nodes was vetoed and why
    // without opening the gate's own log.
    [TestMethod]
    public void APartialBlockOutcomeNamesTheVetoedStepAndItsRawCode()
    {
        using CoordinatorHarness h = Harness();
        Arrange(h, Statuses.Allowed(), Devices.Idle(1));
        h.Block.OnBlock = _ => Task.FromResult(new ControllerResult(
            OpStatus.Partial,
            "One entry vetoed",
            [StepOutcomes.FromHResult("block:cm-disable", unchecked((int)0x80070005))]));

        Task task = h.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, h.Time.GetUtcNow() + Budget, DisconnectWait);
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "block Partial: block:cm-disable"),
            "The vetoed step's own name must be in the line, not just the bare status \"Partial\".");
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
