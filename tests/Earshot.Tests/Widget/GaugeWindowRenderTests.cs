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
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = AirPodsWhere.ThisPc,
            Selection = BroadcastSelectionState.Linked,
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
            // The figure of the lower bud, both buds, and the age of a reading that is no longer live (over 30 s old): "Last read".
            Assert.AreEqual("AirPods 60%, L 70%   R 60%, Last read 2 min ago", gauge.AccessibleDescription);

            gauge.Render(Reading() with { Where = AirPodsWhere.Elsewhere }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("AirPods, on iPhone", gauge.AccessibleDescription);

            gauge.Render(Reading() with { Where = AirPodsWhere.NotInUse }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("Not on this PC", gauge.AccessibleDescription);
        });
    }

    // A screen reader gets a fixed name and the battery as the value, with the tooltip's words as the description.
    [TestMethod]
    public void TheGaugeIsNamedAirPodsBatteryAndItsValueIsTheSpokenReading()
    {
        Earshot.Tests.Phase5.CardDesktop.Run(() =>
        {
            using var gauge = new GaugeWindow(new CapturingLog(), new FakeAccent());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Assert.IsTrue(gauge.ShowAt(Bounds).Ok);

            gauge.Render(Reading(), Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");

            Assert.AreEqual("AirPods battery", gauge.AccessibilityObject.Name);
            Assert.AreEqual(GaugeSpeech.Value(Reading(), Now, GaugeDisplaySettings.Default), gauge.AccessibilityObject.Value);
            Assert.AreEqual(gauge.AccessibleDescription, gauge.AccessibilityObject.Description);

            gauge.Render(Reading() with { Where = AirPodsWhere.NotInUse }, Now, GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");

            Assert.AreEqual("Not on this PC", gauge.AccessibilityObject.Value);
        });
    }

    // Time passing changes what the gauge says with no snapshot change at all: a reading turns stale and its age grows. It does
    // not leave the gauge at an hour or at any age: the owner's decision of 2 October 2026 keeps the last reading shown until a
    // newer one arrives (this replaces the one-hour rule).
    [TestMethod]
    public void AReadingThatTurnsAnHourOldOrDaysOldIsStillDrawnAsTheLastReadingOnTheNextRender()
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

            Assert.AreEqual("AirPods 60%, L 70%   R 60%, Last read 1 h ago", gauge.AccessibleDescription, "An hour old, it is still the last reading, not 'no recent reading'.");

            gauge.Render(snapshot, Now + TimeSpan.FromDays(3), GaugeDisplaySettings.Default, 96, Bounds, Color.Black, "Segoe UI");
            Assert.AreEqual("AirPods 60%, L 70%   R 60%, Last read 3 d ago", gauge.AccessibleDescription, "Days old, the same: shown at any age.");
        });
    }
}
