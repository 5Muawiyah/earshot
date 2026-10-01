using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// Two lines of the tray's own widget wiring that a unit test of the class they call cannot reach: that a setting which
// changes how the gauge is drawn redraws it at once, and that the choice of whether the cards move follows whether the tray
// has real surfaces. Both run through a real TrayContext on fakes, never a real gauge window.
[TestClass]
public sealed class TrayWidgetWiringTests
{
    private static TaskbarLayout FreeSpaceLayout(nint taskbar = 0)
    {
        var bar = new Rectangle(0, 1032, 1920, 48);
        var start = new Rectangle(762, 1032, 45, 48);
        List<Rectangle> buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)).ToList();
        return new TaskbarLayout(taskbar, bar, TaskbarEdge.Bottom, AutoHide: false,
            new Rectangle(0, 0, 1920, 1080), [start, .. buttons, new Rectangle(1678, 1032, 242, 48)], start,
            Dpi: 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, 1032, 242, 48));
    }

    // The gauge is drawn the moment a setting that changes how it is drawn is saved, not when the taskbar poll next answers.
    // The next read is made to find no taskbar before the setting is saved, so the poke the same change makes brings back a
    // layout that hides the gauge: whatever is drawn after the save was drawn by the change itself, before that layout came.
    [TestMethod]
    public void ASettingThatChangesHowTheGaugeIsDrawnRedrawsItAtOnceNotAtTheNextPoll()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var surface = new FakeGaugeSurface();
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), gaugeSurfaceFactory: () => surface, taskbarWatcherPollIntervalMs: 600_000,
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
            tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = true });
            TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "Sanity: a free-space layout must show the gauge.");
            tray.PumpUntilIdle();
            Assert.IsNotEmpty(surface.Renders, "Sanity: the gauge was drawn when it was shown.");
            int before = surface.Renders.Count;

            tray.LastTaskbarReader.SetNextResult(ITaskbarReader.Result.Fail(
                new TaskbarReadFailure(TaskbarReadFailureStep.NoTaskbar, new StepOutcome("fake", false, 0, "S_OK", null))));
            tray.Settings.Update(s => s.Widget = s.Widget with { GaugeOrder = GaugeOrder.NumberRingBolt });
            tray.PumpUntilIdle();

            Assert.AreEqual(before + 1, surface.Renders.Count, "The change drew the gauge itself, once, before any new layout came.");
        });
    }

    private static SessionEndingEventArgs WmEndSession() => new(isQuery: false, ending: true, flags: 0);

    private static TrayHarness OwnedGaugeTray(FakeGaugeSurface surface)
    {
        var tray = new TrayHarness(
            snapshot: Target(ConnectionState.Connected), gaugeSurfaceFactory: () => surface, taskbarWatcherPollIntervalMs: 600_000,
            time: TimeProvider.System,
            settings: s =>
            {
                s.HandBackOnShutdownAndSleep = true;
                s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true };
            },
            handBackBudget: TimeSpan.FromSeconds(2),
            disconnectHandBackWait: TimeSpan.FromMilliseconds(500));
        tray.PumpUntilIdle();
        ShowOwned(tray, surface);
        return tray;
    }

    // A layout that names a taskbar window is read, which shows the gauge and owns it by that window.
    private static void ShowOwned(TrayHarness tray, FakeGaugeSurface surface)
    {
        tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(FreeSpaceLayout(0x777)));
        tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = !s.Widget.LeftClickConnects });
        TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "Sanity: a free-space layout must show the gauge.");
        tray.PumpUntilIdle();
        Assert.AreEqual((nint)0x777, surface.OwnerWindow, "Sanity: the gauge is owned by the taskbar.");
    }

    // The hand-back at shut down holds this thread (it pumps, but it is a wait): the gauge must already be off the taskbar's
    // ownership when the hold begins and while it lasts, however many layouts arrive meanwhile.
    [TestMethod]
    public void TheGaugeIsOffTheTaskbarsOwnershipWhileTheShutDownHandBackHolds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var surface = new FakeGaugeSurface();
            using TrayHarness tray = OwnedGaugeTray(surface);
            nint ownerDuringHold = -1;
            nint ownerAfterLayoutDuringHold = -1;
            tray.Block.OnBlock = _ => Task.Run(async () =>
            {
                ownerDuringHold = surface.OwnerWindow;

                // A new taskbar window is read while the hold is pumping this thread's messages.
                tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(FreeSpaceLayout(0x888)));
                tray.Ui.Post(_ => tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = !s.Widget.LeftClickConnects }), null);
                await Task.Delay(TimeSpan.FromMilliseconds(600));
                ownerAfterLayoutDuringHold = surface.OwnerWindow;
                return ControllerResult.Ok("Blocked at boot");
            });

            tray.Context.OnSessionEnding(null, WmEndSession());

            Assert.AreNotEqual((nint)(-1), ownerDuringHold, "The hand-back's block step ran, so the hold was on.");
            Assert.AreEqual((nint)0, ownerDuringHold, "Owned by the taskbar when the hold began.");
            Assert.AreEqual((nint)0, ownerAfterLayoutDuringHold, "A layout read during the hold owned the gauge again.");
        });
    }

    // Sleep the same, and the gauge is owned again by the first layout once the machine has woken.
    [TestMethod]
    public void TheGaugeIsOffTheTaskbarsOwnershipWhileTheSleepHandBackHoldsAndOwnedAgainAfterWaking()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var surface = new FakeGaugeSurface();
            using TrayHarness tray = OwnedGaugeTray(surface);
            nint ownerDuringHold = -1;
            tray.Block.OnBlock = _ =>
            {
                ownerDuringHold = surface.OwnerWindow;
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend));

            Assert.AreNotEqual((nint)(-1), ownerDuringHold, "The hand-back's block step ran, so the hold was on.");
            Assert.AreEqual((nint)0, ownerDuringHold);

            tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.ResumeAutomatic));
            tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(FreeSpaceLayout(0x777)));
            tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = !s.Widget.LeftClickConnects });
            tray.PumpUntilIdle();

            Assert.AreEqual((nint)0x777, surface.OwnerWindow, "Owned again by the first layout after waking.");
        });
    }

    // Closing the tray lets go of the gauge's owner before its waits (a stopping coordinator, a speech engine, a streaming
    // release): the gauge's window is disposed by then, which ends ownership, but the release is made first on purpose.
    [TestMethod]
    public void ClosingTheTrayTakesTheGaugeOffTheTaskbarsOwnership()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var surface = new FakeGaugeSurface();
            using TrayHarness tray = OwnedGaugeTray(surface);

            tray.Context.Dispose();

            Assert.AreEqual((nint)0, surface.OwnerWindow, "The gauge was taken off the taskbar when the tray closed.");
        });
    }

    // Whether the cards slide and fade follows whether the tray was given fakes for its surfaces: a tray with real surfaces reads
    // Windows' own animation setting, one with fakes has none.
    [TestMethod]
    public void TheCardAnimationChoiceFollowsWhetherTheTrayHasRealSurfaces()
    {
        // One tray to a thread: each reads its own answer on its own.
        bool fakes = true;
        bool real = false;
        bool supplied = false;
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            fakes = tray.Context.WidgetCardAnimationsChosenForTest;
        });
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), realTrayIconSurface: true);
            real = tray.Context.WidgetCardAnimationsChosenForTest;
        });
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), cardAnimationsEnabled: () => false);
            supplied = tray.Context.WidgetCardAnimationsChosenForTest;
        });

        Assert.IsFalse(fakes, "A tray whose surfaces are fakes has no motion.");
        Assert.IsTrue(real, "A tray with real surfaces follows Windows' animation setting.");
        Assert.IsTrue(supplied, "What a test supplies wins, even on a tray of fakes.");
    }
}
