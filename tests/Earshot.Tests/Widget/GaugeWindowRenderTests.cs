using System.Drawing;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real gauge window drawing what the content rules say: a repaint that would draw the same thing is
// skipped, a change of the Windows accent colour repaints, hover repaints, and the window's own description
// carries the tooltip. Private desktop, like the other real window executions.
[TestClass]
public sealed class GaugeWindowRenderTests
{
    private sealed class FakeAccent : IAccentColours
    {
        public Color Light { get; set; } = Color.FromArgb(0, 95, 184);

        public Color Dark { get; set; } = Color.FromArgb(96, 205, 255);

        public event EventHandler? Changed;

        public Color AccentFor(bool lightTheme) => lightTheme ? Light : Dark;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static WidgetSnapshot Reading() =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with
        {
            Where = AirPodsWhere.ThisPc,
            Left = new PartReading(70, false, null) { ReadAt = Now - TimeSpan.FromMinutes(2) },
            Right = new PartReading(60, false, null) { ReadAt = Now - TimeSpan.FromMinutes(2) },
        };

    private static Rectangle Bounds => new(50, 50, GaugeLayout.For(96).Width, GaugeLayout.For(96).Height);

    [TestMethod]
    public void ARepaintThatWouldDrawTheSameThingIsNotPushedAgain()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);

            gauge.Render(Reading(), Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            gauge.Render(Reading(), Now + TimeSpan.FromSeconds(1), GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual(1, gauge.PushCount, "The poll asks once a second; the same picture is not redrawn.");

            gauge.Render(Reading() with { Left = new PartReading(50, false, null) { ReadAt = Now } }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual(2, gauge.PushCount, "A different reading is drawn.");
        });
    }

    [TestMethod]
    public void ChangingTheWindowsAccentColourRepaintsWithTheNewColour()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            var accent = new FakeAccent();
            using var gauge = new GaugeWindow(new CapturingLog(), accent);
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            gauge.Render(Reading(), Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            int before = gauge.PushCount;

            accent.Raise();
            Assert.AreEqual(before, gauge.PushCount, "Same colour, same picture: nothing to redraw.");

            accent.Light = Color.FromArgb(200, 30, 90);
            accent.Raise();

            Assert.AreEqual(before + 1, gauge.PushCount, "A new accent colour is drawn at once, without waiting for the next poll.");
        });
    }

    [TestMethod]
    public void HoverRepaintsOnMouseMoveAndOnMouseLeave()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            gauge.Render(Reading(), Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            int before = gauge.PushCount;

            Earshot.Tests.Phase5.TestWindows.Send(gauge.Handle, NativeMethods.WM_MOUSEMOVE);
            Assert.AreEqual(before + 1, gauge.PushCount, "The hover fill is drawn when the pointer arrives.");

            Earshot.Tests.Phase5.TestWindows.Send(gauge.Handle, NativeMethods.WM_MOUSEMOVE);
            Assert.AreEqual(before + 1, gauge.PushCount, "A second move over the same gauge changes nothing.");

            Earshot.Tests.Phase5.TestWindows.Send(gauge.Handle, NativeMethods.WM_MOUSELEAVE);
            Assert.AreEqual(before + 2, gauge.PushCount, "The hover fill goes when the pointer leaves.");
        });
    }

    [TestMethod]
    public void TheWindowsDescriptionIsTheTooltip()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);

            gauge.Render(Reading(), Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("AirPods, L 70%   R 60%, Read 2 min ago", gauge.AccessibleDescription);

            gauge.Render(Reading() with { Where = AirPodsWhere.Elsewhere }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("On your iPhone", gauge.AccessibleDescription);

            gauge.Render(Reading() with { Where = AirPodsWhere.NotInUse }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("Not on this PC", gauge.AccessibleDescription);
        });
    }

    // Time passing changes what the gauge says with no snapshot change at all: a reading turns an hour old.
    [TestMethod]
    public void AReadingThatTurnsAnHourOldIsDrawnAsNoReadingOnTheNextRender()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);
            WidgetSnapshot snapshot = Reading();

            gauge.Render(snapshot, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            StringAssert.StartsWith(gauge.AccessibleDescription, "AirPods");
            gauge.Render(snapshot, Now + TimeSpan.FromMinutes(57), GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            StringAssert.StartsWith(gauge.AccessibleDescription, "AirPods");
            gauge.Render(snapshot, Now + TimeSpan.FromMinutes(63), GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");

            Assert.AreEqual("No recent reading", gauge.AccessibleDescription);
        });
    }
}
