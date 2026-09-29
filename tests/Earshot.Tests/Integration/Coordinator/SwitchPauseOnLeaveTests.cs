using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Widget;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// A switch to the phone ends in the coordinator's ordinary disconnect, which is where pause on leave hooks in. So a
// shortcut that asks for the phone pauses what is playing first, exactly as the menu's Disconnect does, and a switch
// to this PC never pauses anything.
[TestClass]
public sealed class SwitchPauseOnLeaveTests
{
    private static readonly string[] ReadPauseDisconnectBlock = ["read", "pause", "disconnect", "block"];
    private static readonly string[] Connect = ["connect"];

    private static ToggleRequest Request(bool connect, SwitchTrigger trigger) =>
        CoordinatorHarness.Request(connect) with { Trigger = trigger };

    [TestMethod]
    public void ASwitchToThePhoneByShortcutPausesBeforeItDisconnectsAndBlocks()
    {
        using var h = new CoordinatorHarness();
        var sessions = new TracingMediaSessions { Trace = h.Trace, Sessions = { TracingMediaSessions.Playing("player.exe") } };
        var activity = new FakeRenderActivity { Trace = h.Trace };
        using var pause = new PauseOnLeave(sessions, activity, () => true, h.Time, h.Log);
        h.Coordinator.LeavePause = pause;
        h.Monitor.Set(Devices.Active(0));
        h.Start();
        h.Block.Status = Statuses.Allowed();
        h.Publish(Devices.Active(1));
        h.Coordinator.RefreshStatusAsync();
        h.Pump();
        h.Advance(TimeSpan.Zero);
        h.Trace.Clear();

        Task<ToggleReport> task = h.Coordinator.ToggleAsync(Request(connect: false, SwitchTrigger.ShortcutToPhone));
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        ToggleReport report = task.GetAwaiter().GetResult();
        Assert.AreEqual(OpStatus.Success, report.Status);
        CollectionAssert.AreEqual(ReadPauseDisconnectBlock, h.Trace);
        Assert.AreEqual(1, sessions.PauseCalls.Count, "The endpoint change that followed the disconnect paused a second time.");
        Assert.IsTrue(report.Timeline!.ReleasedSeen);
        Assert.AreEqual(SwitchTrigger.ShortcutToPhone, report.Timeline.Trigger);
    }

    [TestMethod]
    public void ASwitchToThisPcPausesNothing()
    {
        using var h = new CoordinatorHarness();
        var sessions = new TracingMediaSessions { Trace = h.Trace, Sessions = { TracingMediaSessions.Playing("player.exe") } };
        var activity = new FakeRenderActivity { Trace = h.Trace };
        using var pause = new PauseOnLeave(sessions, activity, () => true, h.Time, h.Log);
        h.Coordinator.LeavePause = pause;
        h.Block.Status = Statuses.Allowed(blockAtBoot: false);
        h.Monitor.Set(Devices.Idle(1));
        h.Start();
        h.Trace.Clear();

        Task<ToggleReport> task = h.Coordinator.ToggleAsync(Request(connect: true, SwitchTrigger.ShortcutToPc));
        h.Pump();

        Assert.IsTrue(task.IsCompleted);
        Assert.AreEqual(OpStatus.Success, task.GetAwaiter().GetResult().Status);
        CollectionAssert.AreEqual(Connect, h.Trace);
        Assert.IsEmpty(sessions.PauseCalls);
    }
}
