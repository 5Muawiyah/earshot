using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// What the coordinator tells the moved-to rule: a leave Earshot did not start is told, and Earshot's own Disconnect, a hand-back, a
// fast switch, Send to and a change of its own are not, so none of them shows "Moved to". Fakes and a clock the test moves.
[TestClass]
public sealed class MovedToLeaveCoordinatorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromMilliseconds(1500);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Harness = new CoordinatorHarness();
            Harness.Settings.Update(s => s.HandBackOnShutdownAndSleep = true);
            Sessions = new TracingMediaSessions { Trace = Harness.Trace, Sessions = { TracingMediaSessions.Playing("player.exe") } };
            Activity = new FakeRenderActivity { Trace = Harness.Trace };
            Pause = new PauseOnLeave(Sessions, Activity, () => true, Harness.Time, Harness.Log);
            Pause.LeftByOther += at => Told.Add(at);
            Harness.Coordinator.LeavePause = Pause;
            Harness.Monitor.Set(Devices.Active(0));
            Harness.Start();
        }

        public CoordinatorHarness Harness { get; }

        public TracingMediaSessions Sessions { get; }

        public FakeRenderActivity Activity { get; }

        public PauseOnLeave Pause { get; }

        public List<DateTimeOffset> Told { get; } = new();

        public void InUseAndPlaying()
        {
            Harness.Block.Status = Statuses.Allowed();
            Harness.Publish(Devices.Active(1));
            Harness.Coordinator.RefreshStatusAsync();
            Harness.Pump();
            Harness.Advance(TimeSpan.Zero);
            Harness.Trace.Clear();
        }

        public ToggleReport Toggle(bool connect, SwitchTrigger trigger)
        {
            Task<ToggleReport> task = Harness.Coordinator.ToggleAsync(CoordinatorHarness.Request(connect) with { Trigger = trigger });
            Harness.Pump();
            Assert.IsTrue(task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Pause.Dispose();
            Harness.Dispose();
        }
    }

    [TestMethod]
    public void TheEndpointLeavingWithNothingOfEarshotsInFlightIsTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.Idle(2));

        Assert.HasCount(1, rig.Told);
    }

    // Out of range looks the same as taken: the endpoint goes away. The rule's own wait for messages tells them apart.
    [TestMethod]
    public void TheEndpointGoingAwayIsToldToo()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Publish(Devices.NotPresent(2));

        Assert.HasCount(1, rig.Told);
    }

    [TestMethod]
    public void EarshotsOwnDisconnectIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        ToggleReport report = rig.Toggle(connect: false, SwitchTrigger.Click);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsEmpty(rig.Told);
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "Move: own leave (Disconnect), nothing shown."));
    }

    [TestMethod]
    public void AFastSwitchToThePhoneIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        ToggleReport report = rig.Toggle(connect: false, SwitchTrigger.ShortcutToPhone);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsEmpty(rig.Told);
    }

    [TestMethod]
    public void SendToThePhoneIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        ToggleReport report = rig.Toggle(connect: false, SwitchTrigger.SendTo);

        Assert.AreEqual(OpStatus.Success, report.Status);
        Assert.IsEmpty(rig.Told);
    }

    [TestMethod]
    public void TheHandBackAtShutDownIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.SessionEnd, rig.Harness.Time.GetUtcNow() + Budget, DisconnectWait);
        rig.Harness.Pump();

        Assert.IsEmpty(rig.Told);
    }

    [TestMethod]
    public void TheHandBackAtSleepIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();

        rig.Harness.Coordinator.HandBackAsync(HandBackTrigger.Suspend, rig.Harness.Time.GetUtcNow() + TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(750));
        rig.Harness.Pump();

        Assert.IsEmpty(rig.Told);
    }

    [TestMethod]
    public void TheHandBackOnExitIsNotTold()
    {
        using var rig = new Rig();
        rig.InUseAndPlaying();
        rig.Harness.CheckInvariantOnPump = false;

        rig.Harness.Coordinator.BeginShutdown(new ExitHandBackPlan(Budget, DisconnectWait, StreamingHeld: false));
        rig.Harness.Pump();

        Assert.IsEmpty(rig.Told);
    }

    // A protection change drops the link and brings it back: Earshot's own change, so not a leave.
    [TestMethod]
    public void AChangeEarshotIsMakingIsNotTold()
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

        Assert.IsEmpty(rig.Told);
    }
}
