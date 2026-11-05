using System.Drawing;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Tests.Streaming;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// A disconnect whose block did not take leaves the nodes enabled. Its cross must reach a gauge that is on screen, even when a
// presentation hid the gauge for the whole of the cross's hold, through the real tray with only the taskbar read and the window
// replaced. Every tray runs on a private desktop.
[TestClass]
public sealed class BlockFailurePresentationModeTests
{
    private const string CouldNotBlock = "Could not block the AirPods. Try again.";

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

    // The next read of the taskbar answers with the layout, and a setting saved makes the tray read at once.
    private static void Read(TrayHarness tray, TaskbarLayout layout)
    {
        tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(layout));
        tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = !s.Widget.LeftClickConnects });
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Surface = new FakeGaugeSurface();
            Time = new TestTimeProvider();
            Tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected), gaugeSurfaceFactory: () => Surface, taskbarWatcherPollIntervalMs: 600_000, time: Time,
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true },
                arrange: t => t.Block.Status = Block(BlockState.Allowed));
            Tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail(CouldNotBlock, [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));

            // The card he asks for is not open, so it holds the failure.
            Tray.Toggle.Answer = FailureShown.Held;
            Tray.PumpUntilIdle();
            Read(Tray, FreeSpaceLayout());
            TrayHarness.PumpUntil(() => Tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "Sanity: a free-space layout shows the gauge.");
            Tray.PumpUntilIdle();
        }

        public FakeGaugeSurface Surface { get; }

        public TestTimeProvider Time { get; }

        public TrayHarness Tray { get; }

        // A presentation hides the gauge on every display, and nothing is drawn while it does.
        public void StartPresentation()
        {
            Read(Tray, FreeSpaceLayout() with { NotificationState = Shell.QUNS_PRESENTATION_MODE });
            TrayHarness.PumpUntil(() => Tray.Context.WidgetGaugeStateForTest is GaugeState.Hidden, "Sanity: a presentation hides the gauge.");
            Tray.PumpUntilIdle();
        }

        public void EndPresentation()
        {
            Read(Tray, FreeSpaceLayout());
            TrayHarness.PumpUntil(() => Tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "Sanity: the gauge is back.");
            Tray.PumpUntilIdle();
        }

        public GaugeStoryKind LastDrawn => Surface.Renders[^1].Settings.Story.Kind;

        public void Disconnect()
        {
            Tray.Context.StartToggleFromWidget(CardPlace.NearTray);
            Tray.PumpUntilIdle();
        }

        public void Dispose() => Tray.Dispose();
    }

    [TestMethod]
    public void TheCrossWaitsOutAPresentationAndIsDrawnWhenTheGaugeIsBackThenHoldsItsTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.StartPresentation();
            int drawnBefore = rig.Surface.Renders.Count;

            rig.Disconnect();
            rig.Time.Advance(GaugeStory.CrossHold + TimeSpan.FromMinutes(5));
            rig.Tray.PumpUntilIdle();
            Assert.AreEqual(drawnBefore, rig.Surface.Renders.Count, "Sanity: nothing was drawn while the gauge was hidden.");

            rig.EndPresentation();

            Assert.AreEqual(GaugeStoryKind.Cross, rig.LastDrawn, "The cross was owed and is said now.");
            rig.Time.Advance(GaugeStory.CrossHold - TimeSpan.FromMilliseconds(1));
            rig.Tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.Cross, rig.Tray.Context.GaugeStoryForTest.Current.Kind, "It holds its full time from when it was seen.");

            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
            rig.Tray.PumpUntilIdle();
            Assert.AreEqual(GaugeStoryKind.None, rig.Tray.Context.GaugeStoryForTest.Current.Kind, "and then it is over.");
            Assert.AreEqual(GaugeStoryKind.None, rig.LastDrawn);
        });
    }

    [TestMethod]
    public void ACrossThatWasSeenIsNotSaidAgainWhenAPresentationComesAndGoes()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.Disconnect();
            Assert.AreEqual(GaugeStoryKind.Cross, rig.LastDrawn, "The gauge was on screen, so it drew the cross at once.");
            rig.Time.Advance(GaugeStory.CrossHold);
            rig.Tray.PumpUntilIdle();

            rig.StartPresentation();
            rig.EndPresentation();

            Assert.AreEqual(GaugeStoryKind.None, rig.LastDrawn, "It was said once, so it does not return.");
        });
    }

    [TestMethod]
    public void ACrossOwedOverAPresentationEndsWhenTheAirPodsConnectAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.StartPresentation();
            rig.Disconnect();
            rig.Time.Advance(GaugeStory.CrossHold + TimeSpan.FromMinutes(1));
            rig.Tray.Monitor.Raise(Target(ConnectionState.Disconnected));
            rig.Tray.PumpUntilIdle();

            rig.Tray.Monitor.Raise(Target(ConnectionState.Connected));
            rig.Tray.PumpUntilIdle();
            rig.EndPresentation();

            Assert.AreNotEqual(GaugeStoryKind.Cross, rig.LastDrawn, "They connected again, so the old failure is over.");
        });
    }
}
