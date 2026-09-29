using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// Pause on leave wired into the coordinator: a leave Earshot did not start is seen from the snapshots, and every
// disconnect Earshot sends itself (the menu's Disconnect, the hand-back at shut down, at sleep and on Exit, and the
// disconnect a fast switch to the phone ends in) pauses first. Fakes and a clock the test moves; nothing reaches a
// device.
[TestClass]
public sealed class PauseOnLeaveCoordinatorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] PauseOnly = ["pause"];
    private static readonly string[] ReadPauseDisconnectBlock = ["read", "pause", "disconnect", "block"];
    private static readonly string[] ReadDisconnectBlock = ["read", "disconnect", "block"];
    private static readonly string[] DisconnectBlock = ["disconnect", "block"];
    private static readonly string[] PlayerOnly = ["player.exe"];

    private sealed class Rig : IDisposable
    {
        public Rig(bool enabled = true)
        {
            Harness = new CoordinatorHarness();
            Harness.Settings.Update(s => s.HandBackOnShutdownAndSleep = true);
            Sessions = new TracingMediaSessions { Trace = Harness.Trace, Sessions = { TracingMediaSessions.Playing("player.exe") } };
            Activity = new FakeRenderActivity { Trace = Harness.Trace };
            Enabled = enabled;
            Pause = new PauseOnLeave(Sessions, Activity, () => Enabled, Harness.Time, Harness.Log);
            Harness.Coordinator.LeavePause = Pause;
            Harness.Monitor.Set(Devices.Active(0));
            Harness.Start();
        }

        public CoordinatorHarness Harness { get; }

        public TracingMediaSessions Sessions { get; }

        public FakeRenderActivity Activity { get; }

        public bool Enabled { get; set; }

        public PauseOnLeave Pause { get; }

        // The AirPods in use with the nodes enabled, one sample taken: playing to them.
        public void InUseAndPlaying()
        {
            Harness.Block.Status = Statuses.Allowed();
            Harness.Publish(Devices.Active(1));
            Harness.Coordinator.RefreshStatusAsync();
            Harness.Pump();
            Harness.Advance(TimeSpan.Zero);
            Harness.Trace.Clear();
        }

        public void Dispose()
        {
            Pause.Dispose();
            Harness.Dispose();
        }
    }

    // The phone takes them (or they go out of range): the snapshot shows render leave ACTIVE with nothing of Earshot's
    // in flight, and the session playing is paused as soon as that is seen.
    [TestMethod]
    public void ALeaveEarshotDidNotStartPausesAsSoonAsTheEndpointChangeIsSeen()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.Idle(2));

        CollectionAssert.AreEqual(PauseOnly, rig.Harness.Trace);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "Paused player.exe 0 ms after the change was seen"));
        Assert.IsEmpty(rig.Sessions.PlayCalls);
    }

    // Out of range looks the same as taken: the endpoint goes away.
    [TestMethod]
    public void TheEndpointGoingAwayIsALeaveToo()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.NotPresent(2));

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count);
    }

    [TestMethod]
    public void ALeaveWhileNotPlayingToThemPausesNothing()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.Idle(2));

        Assert.IsEmpty(rig.Sessions.PauseCalls);
    }

    [TestMethod]
    public void ALeaveWithTheSettingOffPausesNothing()
    {
        using var rig = new Rig(enabled: false);
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.Idle(2));

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads);
    }

    // The menu's Disconnect: paused first, so the sound does not jump to the speakers, then the disconnect, then the
    // block, in that order. A fast switch to the phone ends in the same disconnect.
    [TestMethod]
    public void DisconnectPausesBeforeItDisconnects()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        ToggleReport report = rig.Harness.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, rig.Harness.Trace);
        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count, "The endpoint change that followed the disconnect paused a second time.");
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "before Earshot lets the AirPods go (Disconnect), paused player.exe"));
    }

    [TestMethod]
    public void DisconnectWithNothingPlayingDisconnectsWithoutPausing()
    {
        using var rig = new Rig();
        rig.Activity.State = RenderActivityState.Silent;
        rig.InUseAndPlaying();

        ToggleReport report = rig.Harness.Toggle(connect: false);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsEmpty(rig.Sessions.PauseCalls);
        CollectionAssert.AreEqual(ReadDisconnectBlock, rig.Harness.Trace);
    }

    // A disconnect that did not let go gives the decision back: the phone taking them afterwards still pauses.
    [TestMethod]
    public void ADisconnectThatDidNotLetGoLeavesALaterLeaveToBeDecided()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.Connection.OnDisconnect = _ => Task.FromResult(Results.DidNotDisconnect());
        rig.Harness.Block.ActiveLink = ActiveLinkOnBlock.Stays;
        rig.Harness.Toggle(connect: false);
        rig.Sessions.PauseCalls.Clear();
        rig.Harness.CheckInvariantOnPump = false;
        rig.Harness.Advance(TimeSpan.FromSeconds(1));

        rig.Harness.Publish(Devices.Idle(20));

        Assert.AreEqual(1, rig.Sessions.PauseCalls.Count, "A later leave in the same stretch was never decided.");
    }

    // The hand-back at shut down, at sleep and on Exit each pause before their own disconnect, then block.
    [TestMethod]
    public void TheShutDownHandBackPausesThenDisconnectsThenBlocks()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, rig.Harness.Time.GetUtcNow() + Budget, DisconnectWait);
        rig.Harness.Pump();

        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, rig.Harness.Trace);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "(hand-back at shut down), paused player.exe"));
    }

    [TestMethod]
    public void TheSleepHandBackPausesThenDisconnectsThenBlocks()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.Suspend, rig.Harness.Time.GetUtcNow() + TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(750));
        rig.Harness.Pump();

        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, rig.Harness.Trace);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "(hand-back at sleep), paused player.exe"));
    }

    [TestMethod]
    public void TheExitHandBackPausesThenDisconnectsThenBlocks()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Coordinator.BeginShutdown(new ExitHandBackPlan(Budget, DisconnectWait, StreamingHeld: false));
        rig.Harness.Pump();

        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, rig.Harness.Trace);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "(hand-back on Exit), paused player.exe"));
    }

    // A slow media session never eats the hand-back's own wait: the pause is held to a share of the disconnect wait, the
    // disconnect and block still go, and the pause that carries on is still logged once it ends.
    [TestMethod]
    public void ASlowMediaSessionDoesNotStopTheHandBackDisconnectingAndBlocking()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        var release = new TaskCompletionSource<bool>();
        rig.Sessions.OnPause = () => release.Task;

        Task task = rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, rig.Harness.Time.GetUtcNow() + Budget, DisconnectWait);
        rig.Harness.Pump();
        Assert.IsFalse(task.IsCompleted, "The pause has not been given up yet.");
        rig.Harness.Advance(PauseOnLeave.CapFor(DisconnectWait));

        // The cap's own timer ends the wait on a pool thread (Task.WaitAsync completes its result asynchronously), so
        // the rest of the hand-back needs a short real hop before the pump sees it.
        rig.Harness.PumpAfterRealHop(() => task.IsCompleted);

        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, rig.Harness.Trace);
        Assert.IsTrue(task.IsCompleted);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "Hand-back (shutdown): finished in"));
    }

    // Pause on leave never touches a hand-back that has nothing to hand back: AirPods not in use, no read, no pause.
    [TestMethod]
    public void AHandBackWithTheAirPodsNotInUseReadsAndPausesNothing()
    {
        using var rig = new Rig();
        rig.Harness.Block.Status = Statuses.Allowed();
        rig.Harness.Publish(Devices.Idle(1));
        rig.Harness.Coordinator.RefreshStatusAsync();
        rig.Harness.Pump();
        rig.Harness.Trace.Clear();

        rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, rig.Harness.Time.GetUtcNow() + Budget, DisconnectWait);
        rig.Harness.Pump();

        Assert.IsEmpty(rig.Sessions.PauseCalls);
        Assert.AreEqual(0, rig.Activity.Reads);
    }

    // A protection change drops the link and brings it back: an Earshot change in flight, so not a leave.
    [TestMethod]
    public void AProtectionChangeThatBlipsTheLinkPausesNothing()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;
        var release = new TaskCompletionSource<ControllerResult>();
        rig.Harness.Protection.OnApply = (_, _) => release.Task;
        Task<ControllerResult> change = rig.Harness.Coordinator.SetProtectionAsync(true, CardPlace.NearTray);
        rig.Harness.Pump();
        Assert.IsFalse(change.IsCompleted);

        rig.Harness.Publish(Devices.Idle(3));
        release.SetResult(ControllerResult.Ok("Protected"));
        rig.Harness.Pump();
        rig.Harness.Publish(Devices.Active(4));

        Assert.IsEmpty(rig.Sessions.PauseCalls, "A protection change was taken for the AirPods leaving.");
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "Earshot was changing the AirPods when they went away"));
    }

    // With no pause set (a test or a tray with no audio worker) the coordinator is exactly what it was.
    [TestMethod]
    public void WithNoPauseSetADisconnectIsUnchanged()
    {
        using var h = new CoordinatorHarness();
        h.Monitor.Set(Devices.Active(0));
        h.Start();
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Active(1));
        h.Trace.Clear();

        h.Toggle(connect: false);

        CollectionAssert.AreEqual(DisconnectBlock, h.Trace);
    }
}
