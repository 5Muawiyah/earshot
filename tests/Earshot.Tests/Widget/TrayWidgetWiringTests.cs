using System.Drawing;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
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
    private static TaskbarLayout FreeSpaceLayout()
    {
        var bar = new Rectangle(0, 1032, 1920, 48);
        var start = new Rectangle(762, 1032, 45, 48);
        List<Rectangle> buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)).ToList();
        return new TaskbarLayout(0, bar, TaskbarEdge.Bottom, AutoHide: false,
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
