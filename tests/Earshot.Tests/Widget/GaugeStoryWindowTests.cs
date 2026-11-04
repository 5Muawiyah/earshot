using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real gauge window telling the story: a push for each turn of the sweep, its words as the tooltip and the spoken value, no
// frames at rest or while a tick or a cross is held, and none for a window that is hidden. Private desktop, with a fake display.
[TestClass]
public sealed class GaugeStoryWindowTests
{
    private sealed class FakeAccent : IAccentColours
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Color AccentFor(bool lightTheme) => Color.FromArgb(0, 95, 184);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static Rectangle Bounds => new(50, 50, GaugeLayout.For(96).Width, GaugeLayout.For(96).Height);

    private static WidgetSnapshot Reading() =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = AirPodsWhere.ThisPc,
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(70, false, null) { ReadAt = Now - TimeSpan.FromSeconds(3) },
            Right = new PartReading(60, false, null) { ReadAt = Now - TimeSpan.FromSeconds(3) },
        };

    private static GaugeDisplaySettings Story(GaugeStoryKind kind, bool connect = true) =>
        GaugeDisplaySettings.Default with { Story = new GaugeStoryView(kind, connect) };

    private static void Draw(GaugeWindow gauge, GaugeDisplaySettings settings) =>
        gauge.Render(Reading(), Now, settings, 96, Bounds, Color.Black, "Segoe UI");

    [TestMethod]
    public void TheSweepIsPushedForEachRefreshAndTheWordsAreTheTooltipAndTheSpokenValue()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var clock = new FakeVBlankClock(60);
            clock.Blank();
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            gauge.AttachMotion(clock, () => true);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            Draw(gauge, GaugeDisplaySettings.Default);
            Assert.AreEqual(0, clock.SubscribeCalls, "At rest nothing asks for a frame.");
            int before = gauge.PushCount;

            Draw(gauge, Story(GaugeStoryKind.Spinning));

            Assert.AreEqual("Connecting", gauge.AccessibleDescription);
            Assert.AreEqual("Connecting", gauge.SpokenValue);
            Assert.IsTrue(gauge.MotionSubscribed);
            int afterStart = gauge.PushCount;
            Assert.IsGreaterThan(before, afterStart, "The story's first frame is drawn.");

            clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(500));
            Assert.IsGreaterThan(afterStart + 20, gauge.PushCount, "Each turn of the sweep is a new bitmap.");
        });
    }

    [TestMethod]
    public void ATickAndACrossAreDrawnWithTheirWordsAndHoldWithNoFramesThenTheGaugeGoesBackToRest()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var clock = new FakeVBlankClock(60);
            clock.Blank();
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            gauge.AttachMotion(clock, () => true);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            Draw(gauge, GaugeDisplaySettings.Default);
            string? rest = gauge.AccessibleDescription;
            Draw(gauge, Story(GaugeStoryKind.Spinning));
            clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(400));

            Draw(gauge, Story(GaugeStoryKind.Tick));
            Assert.AreEqual("Connected", gauge.AccessibleDescription);
            clock.RunUntilIdle();
            int held = gauge.PushCount;
            clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(800));
            Assert.AreEqual(held, gauge.PushCount, "The hold draws nothing more.");
            Assert.IsFalse(gauge.MotionSubscribed);

            Draw(gauge, GaugeDisplaySettings.Default);
            clock.RunUntilIdle();
            Assert.AreEqual(rest, gauge.AccessibleDescription, "The tooltip is the reading's again.");
            int settled = gauge.PushCount;
            clock.RunUntil(clock.Now + TimeSpan.FromSeconds(2));
            Assert.AreEqual(settled, gauge.PushCount, "No idle cost.");

            Draw(gauge, Story(GaugeStoryKind.Spinning, connect: false));
            Draw(gauge, Story(GaugeStoryKind.Cross, connect: false));
            Assert.AreEqual("Didn't disconnect", gauge.AccessibleDescription);
            Assert.AreEqual("Didn't disconnect", gauge.SpokenValue);
        });
    }

    [TestMethod]
    public void AHiddenWindowAsksForNoFramesAndKeepsTheStoryForWhenItIsShownAgain()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var clock = new FakeVBlankClock(60);
            clock.Blank();
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            gauge.AttachMotion(clock, () => true);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            Draw(gauge, Story(GaugeStoryKind.Spinning));
            clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(300));
            Assert.IsTrue(gauge.MotionSubscribed);

            gauge.HideWindow();

            Assert.IsFalse(gauge.MotionSubscribed, "Hidden for a full-screen application: nothing turns.");
            int hidden = gauge.PushCount;
            clock.RunUntil(clock.Now + TimeSpan.FromSeconds(2));
            Assert.AreEqual(hidden, gauge.PushCount, "And nothing is drawn.");

            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            Draw(gauge, Story(GaugeStoryKind.Spinning));
            Assert.IsTrue(gauge.MotionSubscribed, "Shown again, it turns again.");
            Assert.AreEqual("Connecting", gauge.AccessibleDescription);
        });
    }

    [TestMethod]
    public void WithAnimationEffectsOffTheSweepIsDrawnOnceAndStill()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var clock = new FakeVBlankClock(60);
            clock.Blank();
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            gauge.AttachMotion(clock, () => false);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            Draw(gauge, GaugeDisplaySettings.Default);
            int before = gauge.PushCount;

            Draw(gauge, Story(GaugeStoryKind.Spinning));
            clock.RunUntil(clock.Now + TimeSpan.FromSeconds(2));

            Assert.AreEqual(before + 1, gauge.PushCount, "One still picture, no turning.");
            Assert.AreEqual(0, clock.SubscribeCalls);
        });
    }
}
