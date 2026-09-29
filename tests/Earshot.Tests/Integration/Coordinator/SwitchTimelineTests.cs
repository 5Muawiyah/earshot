using Earshot.App;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The measurement of a connect or disconnect, taken by the coordinator from one clock. Every figure below is what
// the test advanced a fake clock by inside the fake controller that the phase waits for, so a stamp that is removed,
// moved or taken in the wrong place changes a figure and fails a named test. Nothing here changes what the
// coordinator sends or in which order: the coordinator's own tests, unedited, are the proof of that.
[TestClass]
public sealed class SwitchTimelineTests
{
    private static readonly string[] BlockOnly = ["block"];
    private static readonly string[] AllowThenBlock = ["allow", "block"];

    private static ConnectResult Sent() =>
        new(ConnectOutcome.Confirmed, ConnectMessages.Connected, [StepOutcomes.FromHResult("ks-reconnect:src", 0, "a2dp: adapter")]);

    private static double Ms(TimeSpan? span) => span!.Value.TotalMilliseconds;

    private static ToggleReport Run(CoordinatorHarness h, bool connect, SwitchTrigger trigger = SwitchTrigger.Click)
    {
        Task<ToggleReport> task = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect) with { Trigger = trigger });
        h.Pump();
        Assert.IsTrue(task.IsCompleted, "The switch did not finish.");
        return task.GetAwaiter().GetResult();
    }

    // --- to this PC ---

    [TestMethod]
    public void ToPcOnTheAlreadyPathStampsTheFirstPassAndNothingElse()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(40));
            return Task.FromResult(Results.Connected());
        });

        ToggleReport report = Run(h, connect: true, SwitchTrigger.ShortcutToPc);

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(SwitchPath.Already, t.Path, "Nothing was sent to a filter, so render was already ACTIVE.");
        Assert.AreEqual(SwitchTrigger.ShortcutToPc, t.Trigger);
        Assert.AreEqual(0, Ms(t.Phase(SwitchPhase.Queued)));
        Assert.AreEqual(40, Ms(t.Phase(SwitchPhase.FirstPass)));
        Assert.AreEqual(40, Ms(t.ActiveAfter));
        Assert.IsNull(t.Phase(SwitchPhase.Status));
        Assert.IsNull(t.Phase(SwitchPhase.Allow));
        Assert.IsNull(t.Phase(SwitchPhase.Endpoints));
        Assert.IsNull(t.Phase(SwitchPhase.Connect));
        Assert.AreEqual(40, Ms(t.Total));
    }

    [TestMethod]
    public void ToPcOnTheDirectPathIsMeasuredFromTheRequestToTheConfirmation()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(1719));
            return Task.FromResult(Sent());
        });

        ToggleReport report = Run(h, connect: true);

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(SwitchPath.Direct, t.Path);
        Assert.AreEqual(1719, Ms(t.Phase(SwitchPhase.FirstPass)));
        Assert.AreEqual(1719, Ms(t.ActiveAfter));
    }

    [TestMethod]
    public void ToPcOnTheAllowFirstPathStampsEveryPhaseWhereItRuns()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(31));
            return Task.FromResult(Results.NodesBlocked());
        });
        h.Block.BeforeReads.Enqueue(() => h.Time.Advance(TimeSpan.FromMilliseconds(7)));
        h.Block.OnAllow = _ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(250));
            h.Block.Status = h.Block.Status with { State = BlockState.Allowed };
            return Task.FromResult(ControllerResult.Ok("Allowed"));
        };
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(1500));
            h.Monitor.Publish(Devices.Active(51));
            return Task.FromResult(Sent());
        });

        // The endpoints come back 11 ms after the allow ended: the toggle waits for them until then.
        Task<ToggleReport> task = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true) with { Trigger = SwitchTrigger.Menu });
        h.Pump();
        Assert.IsFalse(task.IsCompleted, "The connect should be waiting for a render endpoint.");
        h.Time.Advance(TimeSpan.FromMilliseconds(11));
        h.Publish(Devices.Idle(50));
        Assert.IsTrue(task.IsCompleted, "The connect did not finish.");
        ToggleReport report = task.GetAwaiter().GetResult();

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.AreEqual(SwitchPath.AllowFirst, t.Path);
        Assert.AreEqual(31, Ms(t.Phase(SwitchPhase.FirstPass)), "first-pass");
        Assert.AreEqual(7, Ms(t.Phase(SwitchPhase.Status)), "status");
        Assert.AreEqual(250, Ms(t.Phase(SwitchPhase.Allow)), "allow");
        Assert.AreEqual(11, Ms(t.Phase(SwitchPhase.Endpoints)), "endpoints");
        Assert.AreEqual(1500, Ms(t.Phase(SwitchPhase.Connect)), "connect");
        Assert.AreEqual(31 + 7 + 250 + 11 + 1500, Ms(t.ActiveAfter), "The handover is the whole of it up to the confirmation.");
        Assert.AreEqual(Ms(t.Total) - Ms(t.ActiveAfter), Ms(t.Phase(SwitchPhase.Protection)), "The protection check is what is left after ACTIVE.");
    }

    [TestMethod]
    public void ToPcTheProtectionCheckIsItsOwnPhaseAfterTheConfirmation()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(900));
            return Task.FromResult(Sent());
        });
        h.Block.BeforeReads.Enqueue(() => h.Time.Advance(TimeSpan.FromMilliseconds(3228)));

        ToggleReport report = Run(h, connect: true);

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(900, Ms(t.ActiveAfter), "The figure must stop at the confirmation, not include the protection check.");
        Assert.AreEqual(3228, Ms(t.Phase(SwitchPhase.Protection)));
        Assert.AreEqual(900 + 3228, Ms(t.Total));
    }

    [TestMethod]
    public void ToPcOnTheHandsFreeAssistedPathSaysSo()
    {
        using var h = new CoordinatorHarness(protectAudio: true);
        h.Protection.State = AudioProtectionState.Protected;
        h.Settings.Update(s => s.ProtectAudioNoticeShown = true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1, capture: false));
        h.Start();
        h.Publish(Devices.Idle(2, capture: false));
        h.Protection.Effect = protect =>
        {
            h.Protection.State = protect ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
            h.Monitor.Publish(Devices.Idle(protect ? 30 : 20, capture: !protect));
        };
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.A2dpRejected()));

        ToggleReport report = Run(h, connect: true);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.AreEqual(SwitchPath.HandsFreeAssisted, report.Timeline!.Path);
        Assert.IsTrue(report.Timeline.ActiveReached);
    }

    // A connect that fails after an allow blocks again before it reports, and the timeline closes after that
    // clean-up, not before it.
    [TestMethod]
    public void ToPcThatFailsAfterAnAllowReportsBlockedAgainAndItsTotalIncludesTheCleanUp()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        h.Connection.Connects.Enqueue(_ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(15000));
            return Task.FromResult(Results.TimedOut());
        });
        h.Block.OnBlock = _ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(280));
            h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            h.Monitor.Publish(Devices.NotPresent(60));
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        ToggleReport report = Run(h, connect: true);

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(OpStatus.Failed, report.Status);
        Assert.IsFalse(t.ActiveReached);
        Assert.AreEqual(SwitchBlockedAgain.Yes, t.BlockedAgain);
        Assert.AreEqual(SwitchPath.None, t.Path, "No confirmation, so no path.");
        Assert.AreEqual(15280, Ms(t.Total), "The total must include the block that put the nodes back.");
        Assert.AreEqual(OpStatus.Failed, t.Outcome);
        StringAssert.Contains(SwitchTimelineText.Format(t), "not active (outcome Failed, trigger click, path -, blocked-again yes, total 15280,");
        CollectionAssert.AreEqual(AllowThenBlock, h.Block.Calls, "The allow and the block that took it back.");
    }

    [TestMethod]
    public void ToPcThatFailedWithNothingChangedIsNotNeededAndOneThatCouldNotBlockIsNo()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NothingSent()));

        ToggleReport nothing = Run(h, connect: true);

        Assert.AreEqual(SwitchBlockedAgain.NotNeeded, nothing.Timeline!.BlockedAgain);

        using var open = new CoordinatorHarness();
        open.Block.Status = Statuses.Blocked(blockAtBoot: false);
        open.Monitor.Set(Devices.NotPresent(1));
        open.Start();
        open.Connection.Connects.Enqueue(_ => Task.FromResult(Results.NodesBlocked()));
        open.Connection.Connects.Enqueue(_ => Task.FromResult(Results.TimedOut()));

        ToggleReport left = Run(open, connect: true);

        Assert.AreEqual(SwitchBlockedAgain.No, left.Timeline!.BlockedAgain, "Block at boot is off, so the nodes were left enabled.");
    }

    // --- to the phone ---

    private static CoordinatorHarness Connected(bool blockAtBoot = true)
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed(blockAtBoot);
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        return h;
    }

    [TestMethod]
    public void ToPhoneStampsTheReleaseThenTheBlockAndOnlyThenAtRest()
    {
        using CoordinatorHarness h = Connected();
        h.Connection.OnDisconnect = _ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(58));
            h.Monitor.Publish(Devices.Idle(70));
            return Task.FromResult(Results.Disconnected());
        };
        h.Block.OnBlock = _ =>
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(291));
            h.Block.Status = h.Block.Status with { State = BlockState.Blocked };
            h.Monitor.Publish(Devices.NotPresent(71));
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        ToggleReport report = Run(h, connect: false, SwitchTrigger.ShortcutToPhone);

        SwitchTimeline t = report.Timeline!;
        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsTrue(t.ReleasedSeen);
        Assert.AreEqual(58, Ms(t.ReleasedAfter));
        Assert.IsTrue(t.AtRest);
        Assert.IsGreaterThanOrEqualTo(58 + 291, Ms(t.AtRestAfter), "At rest cannot come before the block has ended.");
        Assert.AreEqual(291, Ms(t.Phase(SwitchPhase.Block)));
        Assert.AreEqual(Ms(t.AtRestAfter), Ms(t.Total));
        Assert.IsNull(t.NotAtRestReason);
    }

    // What the block reported is not the evidence. The block says it worked and the nodes still read Allowed.
    [TestMethod]
    public void ToPhoneIsNotAtRestWhenTheBlockClaimedSuccessButTheNodesReadAllowed()
    {
        using CoordinatorHarness h = Connected();
        h.Block.OnBlock = _ =>
        {
            // Success is returned and the state is left as it was.
            h.Monitor.Publish(Devices.NotPresent(80));
            return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
        };

        ToggleReport report = Run(h, connect: false);

        SwitchTimeline t = report.Timeline!;
        Assert.IsTrue(t.ReleasedSeen);
        Assert.IsFalse(t.AtRest, "At rest was claimed from the block's result rather than the node read.");
        Assert.AreEqual(SwitchTimelineText.ReasonBlockDidNotTake, t.NotAtRestReason);
        StringAssert.Contains(SwitchTimelineText.Format(t), "released after 0 ms, not at rest: the block did not take (");
    }

    [TestMethod]
    public void ToPhoneWithBlockAtBootOffIsReleasedButNotAtRestForThatReason()
    {
        using CoordinatorHarness h = Connected(blockAtBoot: false);

        ToggleReport report = Run(h, connect: false);

        SwitchTimeline t = report.Timeline!;
        Assert.IsTrue(t.ReleasedSeen);
        Assert.IsFalse(t.AtRest);
        Assert.AreEqual(SwitchTimelineText.ReasonBlockAtBootOff, t.NotAtRestReason);
        Assert.IsNull(t.Phase(SwitchPhase.Block), "No block was sent, so there is no block phase.");
        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void ToPhoneWhenTheBootBlockStatusCannotBeReadSaysSo()
    {
        using CoordinatorHarness h = Connected();
        h.Block.StatusFailure = new IOException("no status");

        ToggleReport report = Run(h, connect: false);

        Assert.AreEqual(SwitchTimelineText.ReasonStatusUnreadable, report.Timeline!.NotAtRestReason);
        Assert.IsFalse(report.Timeline.AtRest);
    }

    [TestMethod]
    public void ToPhoneBeforeSetUpSaysNotSetUp()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.NotSetUp();
        h.Monitor.Set(Devices.Active(1));
        h.Start();

        ToggleReport report = Run(h, connect: false);

        Assert.AreEqual(SwitchTimelineText.ReasonNotSetUp, report.Timeline!.NotAtRestReason);
    }

    // Pressed when the AirPods are not on this PC and the nodes are already blocked: nothing to release, and the
    // machine is at rest now.
    [TestMethod]
    public void ToPhoneWhenAlreadyBlockedIsAtRestAtOnce()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();

        ToggleReport report = Run(h, connect: false);

        Assert.IsTrue(report.Timeline!.AtRest);
        Assert.IsEmpty(h.Block.Calls);
    }

    // --- shared ---

    // A request accepted while another operation runs has its own timeline, and its wait is `queued`.
    [TestMethod]
    public void ARequestThatWaitsBehindAnotherHasItsOwnTimelineAndAQueuedFigure()
    {
        using CoordinatorHarness h = Connected();
        var holding = new TaskCompletionSource<ConnectResult>();
        h.Connection.Connects.Enqueue(_ => holding.Task);
        Task<ToggleReport> first = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true));
        h.Pump();
        Task<ToggleReport> second = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: false));
        h.Pump();
        Assert.IsFalse(second.IsCompleted, "The second request should be waiting for the first.");
        h.Time.Advance(TimeSpan.FromMilliseconds(300));

        holding.SetResult(Results.Connected());
        h.Pump();
        h.PumpAfterRealHop(() => second.IsCompleted);

        SwitchTimeline a = first.GetAwaiter().GetResult().Timeline!;
        SwitchTimeline b = second.GetAwaiter().GetResult().Timeline!;
        Assert.AreNotSame(a, b, "The timeline must be per request, not a field.");
        Assert.AreEqual(0, Ms(a.Phase(SwitchPhase.Queued)));
        Assert.AreEqual(300, Ms(b.Phase(SwitchPhase.Queued)));
        Assert.IsFalse(a.Connect == b.Connect);
    }

    [TestMethod]
    public void ACancelledSwitchEndsItsTimelineAsCancelled()
    {
        using CoordinatorHarness h = Connected();
        using var cts = new CancellationTokenSource();
        h.Connection.Connects.Enqueue(token =>
        {
            var waiting = new TaskCompletionSource<ConnectResult>();
            token.Register(() => waiting.TrySetCanceled(token));
            return waiting.Task;
        });

        Task<ToggleReport> task = h.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect: true), cts.Token);
        h.Pump();
        h.Time.Advance(TimeSpan.FromMilliseconds(220));
        cts.Cancel();
        h.Pump();
        h.PumpAfterRealHop(() => task.IsCompleted);

        ToggleReport report = task.GetAwaiter().GetResult();
        Assert.IsTrue(report.Cancelled);
        Assert.IsTrue(report.Timeline!.Cancelled);
        Assert.AreEqual(220, Ms(report.Timeline.Total));
        StringAssert.Contains(SwitchTimelineText.Format(report.Timeline), "Switch to-pc: cancelled (trigger click, total 220,");
    }

    // A refusal because the session is ending is a switch that was not started: cancelled, not failed.
    [TestMethod]
    public void AConnectRefusedAtSessionEndIsCancelled()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.NotPresent(1));
        h.Start();
        h.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));
        h.Pump();

        ToggleReport report = Run(h, connect: true);

        Assert.IsTrue(report.Cancelled);
        Assert.IsTrue(report.Timeline!.Cancelled);
        StringAssert.Contains(SwitchTimelineText.Format(report.Timeline), "Switch to-pc: cancelled (");
    }

    // A clock that throws costs the figures and one warning, never the switch or the block.
    [TestMethod]
    public void AClockThatThrowsDoesNotStopTheSwitchOrTheBlock()
    {
        var time = new ThrowingTimestampTime();
        using CoordinatorHarness h = Connected();
        var log = new CapturingLog();
        var timeline = new SwitchTimeline(time, log, connect: false, SwitchTrigger.Click);
        time.ThrowOnTimestamp = true;

        long mark = timeline.Mark();
        timeline.CoreBegan();
        timeline.Record(SwitchPhase.Block, mark);
        timeline.MarkReleased();
        timeline.MarkAtRest();
        timeline.Complete(OpStatus.Success, cancelled: false);

        Assert.IsTrue(timeline.ReleasedSeen, "The fact must survive a clock that cannot give the figure.");
        Assert.IsTrue(timeline.AtRest);
        Assert.IsNull(timeline.ReleasedAfter);
        Assert.IsNull(timeline.Total);
        Assert.IsTrue(timeline.ClockFailed);
        Assert.AreEqual(1, log.Entries.Count(e => e.Message.Contains("InvalidOperationException", StringComparison.Ordinal)), "One warning, naming the exception type.");
        StringAssert.Contains(SwitchTimelineText.Format(timeline), "released after - ms, at rest after - ms (");

        // And a real disconnect through the coordinator with the same kind of clock still blocks.
        h.Time.Advance(TimeSpan.Zero);
        ToggleReport report = Run(h, connect: false);
        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    private sealed class ThrowingTimestampTime : TimeProvider
    {
        private readonly ManualTime _inner = new();

        public bool ThrowOnTimestamp { get; set; }

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() =>
            ThrowOnTimestamp ? throw new InvalidOperationException("clock") : _inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _inner.CreateTimer(callback, state, dueTime, period);
    }
}
