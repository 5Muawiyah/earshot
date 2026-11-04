using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The connect story as the gauge draws it, off screen: the sweep, the tick and the cross at the number's place, what stays and
// what leaves, at 100%, 125% and 150%, light and dark. The accent is a colour of the test's own that matches nothing else in
// the palette, so a ring pixel can only be the fill, the track or nothing.
[TestClass]
public sealed class GaugeStoryRenderTests
{
    private const string FontFamily = "Segoe UI";
    private static readonly Color Accent = Color.FromArgb(12, 160, 88);
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static GaugePalette Palette(bool light) => GaugePalette.Create(light, Accent, highContrast: false, light ? Color.Black : Color.White);

    private static GaugeContent Reading(int percent, GaugeStoryKind story, bool connect = true, bool charging = false) =>
        new GaugeContent(GaugeMode.Reading, percent, percent <= 20, charging, "") { Story = new GaugeStoryView(story, connect) };

    private static Bitmap Draw(GaugeContent content, int dpi, bool light, GaugeStoryFrame? frame = null) =>
        GaugeRenderer.Render(content, Palette(light), GaugeLayout.For(dpi), 0, FontFamily, ring: null, frame);

    private static Color OnRing(Bitmap bitmap, GaugeLayout layout, double degrees)
    {
        PointF c = GaugeRenderer.RingCentre(layout);
        double radians = degrees * Math.PI / 180.0;
        int x = (int)Math.Floor(c.X + (layout.RingRadius * Math.Sin(radians)));
        int y = (int)Math.Floor(c.Y - (layout.RingRadius * Math.Cos(radians)));
        return bitmap.GetPixel(x, y);
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static bool IsFill(Color pixel, Color fill) => pixel.A >= 200 && Near(pixel, fill, 24);

    private static bool IsTrack(Color pixel, GaugePalette palette) => pixel.A > 20 && Math.Abs(pixel.A - palette.Track.A) <= 30 && !IsFill(pixel, palette.Accent);

    private static List<Point> Painted(Bitmap bitmap, Rectangle area, int threshold = 100)
    {
        var found = new List<Point>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (bitmap.GetPixel(x, y).A >= threshold)
                {
                    found.Add(new Point(x, y));
                }
            }
        }

        return found;
    }

    private static Color Fullest(Bitmap bitmap, Rectangle slot)
    {
        Color best = Color.Transparent;
        for (int y = slot.Top; y < slot.Bottom; y++)
        {
            for (int x = slot.Left; x < slot.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A > best.A)
                {
                    best = pixel;
                }
            }
        }

        return best;
    }

    private static bool AnyFill(Bitmap bitmap, GaugeLayout layout, GaugePalette palette)
    {
        for (int degrees = 0; degrees < 360; degrees += 6)
        {
            if (IsFill(OnRing(bitmap, layout, degrees), palette.Accent) || IsFill(OnRing(bitmap, layout, degrees), palette.Caution))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Connecting

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(120, false)]
    [DataRow(144, true)]
    public void ConnectingDrawsANinetyDegreeAccentSweepFromTwelveOClockOnTheTrackWithTheNumberSlotEmptyAndTheMarkKept(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        using Bitmap bitmap = Draw(Reading(70, GaugeStoryKind.Spinning, charging: true), dpi, light);

        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 20), Accent), "The sweep starts at 12 o'clock.");
        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 45), Accent));
        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 70), Accent), "And is 90 degrees long.");
        foreach (int degrees in new[] { 135, 200, 270, 330 })
        {
            Assert.IsTrue(IsTrack(OnRing(bitmap, layout, degrees), palette), "Track only at " + degrees + " degrees.");
        }

        Assert.HasCount(0, Painted(bitmap, layout.NumberSlot), "The number slot is empty while it connects.");
        Assert.HasCount(0, Painted(bitmap, layout.ChargingSlot), "So is the bolt's.");
        Assert.IsGreaterThan(10, Painted(bitmap, layout.Mark).Count, "The mark stays put.");
    }

    [TestMethod]
    public void TheSweepTurnsWithTheFramesStart()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        GaugeContent content = Reading(70, GaugeStoryKind.Spinning);
        var frame = new GaugeStoryFrame(GaugeStoryKind.Spinning, 0, 90, GaugeSlotGlyph.None, 0, 0);
        using Bitmap bitmap = Draw(content, 96, light: true, frame);

        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 135), Accent), "Starting at 3 o'clock it covers the lower right.");
        Assert.IsFalse(IsFill(OnRing(bitmap, layout, 45), Accent), "And no longer 12 o'clock to 3.");
    }

    [TestMethod]
    public void ConnectingDrawsTheSweepForAnyStateTheGaugeWasIn()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        foreach (GaugeMode mode in new[] { GaugeMode.NotOnThisPc, GaugeMode.MarkOnly, GaugeMode.OnOtherDevice, GaugeMode.CaseAway })
        {
            var content = new GaugeContent(mode, mode == GaugeMode.CaseAway ? 50 : null, false, false, "")
            {
                Story = new GaugeStoryView(GaugeStoryKind.Spinning, true),
                CaseMark = mode == GaugeMode.CaseAway,
            };
            using Bitmap bitmap = Draw(content, 96, light: true);

            Assert.IsTrue(IsFill(OnRing(bitmap, layout, 45), Accent), "The sweep is drawn from " + mode);
            Assert.HasCount(0, Painted(bitmap, layout.NumberSlot), "No number and no phone in the slot from " + mode);
        }
    }

    // ---- Connected

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(120, false)]
    [DataRow(144, true)]
    public void ConnectedDrawsAnAccentTickInTheNumberSlotAndTheArcAtTheLiveLevel(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        using Bitmap bitmap = Draw(Reading(70, GaugeStoryKind.Tick), dpi, light);

        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 20), Accent));
        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 150), Accent));
        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 240), Accent), "70% is 252 degrees.");
        Assert.IsTrue(IsTrack(OnRing(bitmap, layout, 300), palette), "And no further.");
        Assert.IsGreaterThan(5, Painted(bitmap, layout.NumberSlot).Count, "The tick is in the number's place.");
        Assert.IsTrue(Near(Fullest(bitmap, layout.NumberSlot), Accent, 40), "In the accent.");
    }

    [TestMethod]
    public void ConnectedWithNoLiveReadingYetDrawsTheTrackOnlyBesideTheTick()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        GaugePalette palette = Palette(true);
        var none = new GaugeContent(GaugeMode.MarkOnly, null, false, false, "") { Story = new GaugeStoryView(GaugeStoryKind.Tick, true) };
        var last = new GaugeContent(GaugeMode.Reading, 70, false, false, "") { Tertiary = true, Story = new GaugeStoryView(GaugeStoryKind.Tick, true) };

        foreach (GaugeContent content in new[] { none, last })
        {
            using Bitmap bitmap = Draw(content, 96, light: true);
            Assert.IsFalse(AnyFill(bitmap, layout, palette), "No arc: a last reading is not the live level.");
            Assert.IsTrue(IsTrack(OnRing(bitmap, layout, 45), palette));
            Assert.IsGreaterThan(5, Painted(bitmap, layout.NumberSlot).Count, "The tick is still drawn.");
        }
    }

    // ---- Failed

    [TestMethod]
    [DataRow(96, true, true)]
    [DataRow(120, false, false)]
    [DataRow(144, true, false)]
    public void FailedDrawsTheTrackOnlyAndACautionCrossInTheNumberSlot(int dpi, bool light, bool connect)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        using Bitmap bitmap = Draw(Reading(70, GaugeStoryKind.Cross, connect), dpi, light);

        Assert.IsFalse(AnyFill(bitmap, layout, palette), "The arc has emptied to the track.");
        Assert.IsTrue(IsTrack(OnRing(bitmap, layout, 45), palette));
        Assert.IsGreaterThan(5, Painted(bitmap, layout.NumberSlot).Count);
        Assert.IsTrue(Near(Fullest(bitmap, layout.NumberSlot), palette.Caution, 40), "The cross is in caution.");
    }

    // ---- Fades and the leave

    [TestMethod]
    public void AGlyphPartWayThroughItsFadeIsPartlyOpaqueAndOneNotYetInIsNotThere()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        GaugeContent content = Reading(70, GaugeStoryKind.Tick);

        using Bitmap whole = Draw(content, 96, true, new GaugeStoryFrame(GaugeStoryKind.Tick, -90, 252, GaugeSlotGlyph.Tick, 1, 0));
        using Bitmap half = Draw(content, 96, true, new GaugeStoryFrame(GaugeStoryKind.Tick, -90, 252, GaugeSlotGlyph.Tick, 0.5, 0));
        int full = Fullest(whole, layout.NumberSlot).A;
        int alpha = Fullest(half, layout.NumberSlot).A;
        Assert.IsGreaterThan(30, full, "Sanity: the whole tick is drawn.");
        Assert.IsTrue(Math.Abs(alpha - (full / 2.0)) <= 3, "Half of the whole glyph's alpha: " + alpha + " of " + full);

        using Bitmap none = Draw(content, 96, true, new GaugeStoryFrame(GaugeStoryKind.Tick, -90, 252, GaugeSlotGlyph.Tick, 0, 0));
        Assert.HasCount(0, Painted(none, layout.NumberSlot, 20));
    }

    [TestMethod]
    public void TheNumberComesBackOverTheSameEightyThreeMillisecondsAsTheMarkLeaves()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        GaugeContent content = new(GaugeMode.Reading, 70, false, false, "");

        using Bitmap half = Draw(content, 96, true, new GaugeStoryFrame(GaugeStoryKind.None, -90, 252, GaugeSlotGlyph.Tick, 0.5, 0.5));

        int alpha = Fullest(half, layout.NumberSlot).A;
        Assert.IsTrue(alpha is > 90 and < 190, "The number at half: " + alpha);
        Assert.IsTrue(IsFill(OnRing(half, layout, 240), Accent), "The arc is already at the reading.");
    }

    [TestMethod]
    public void ALowReadingKeepsItsCautionArcAndNumberThroughTheLeave()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        GaugePalette palette = Palette(true);
        var content = new GaugeContent(GaugeMode.Reading, 15, true, false, "");

        using Bitmap bitmap = Draw(content, 96, true, new GaugeStoryFrame(GaugeStoryKind.None, -90, 54, GaugeSlotGlyph.None, 0, 1));

        Assert.IsTrue(IsFill(OnRing(bitmap, layout, 20), palette.Caution), "Low is caution, as at rest.");
        Assert.IsTrue(Near(Fullest(bitmap, layout.NumberSlot), palette.Caution, 40));
    }

    [TestMethod]
    public void AFrameBuiltForAContentWithNoStoryIsTheGaugeAtRest()
    {
        GaugeContent content = new(GaugeMode.Reading, 70, false, true, "");
        GaugeStoryFrame rest = GaugeStoryFrame.Still(content);

        Assert.AreEqual(GaugeStoryKind.None, rest.Phase);
        Assert.AreEqual(1.0, rest.NumberOpacity);
        Assert.AreEqual(0.0, rest.GlyphOpacity);
        Assert.AreEqual(252.0, rest.Extent, 1e-9);
    }

    [TestMethod]
    public void NothingIsDrawnDifferentlyForAContentWithNoStoryAndNoFrame()
    {
        GaugeContent content = new(GaugeMode.Reading, 70, false, true, "");

        using Bitmap plain = GaugeRenderer.Render(content, Palette(true), GaugeLayout.For(96), hover: false, FontFamily);
        using Bitmap viaFrame = Draw(content, 96, true);

        for (int y = 0; y < plain.Height; y++)
        {
            for (int x = 0; x < plain.Width; x++)
            {
                Assert.AreEqual(plain.GetPixel(x, y), viaFrame.GetPixel(x, y), "Pixel " + x + "," + y);
            }
        }
    }

    // ---- High contrast

    [TestMethod]
    public void InHighContrastTheTickIsTheTextColourAndTheSweepIsHighlight()
    {
        Color text = SystemColors.WindowText;
        GaugePalette hc = GaugePalette.Create(lightTheme: true, Accent, highContrast: true, text);
        GaugePalette normal = Palette(true);

        Assert.AreEqual(text, hc.Tick, "In high contrast the tick is WindowText.");
        Assert.AreEqual(text, hc.Caution, "As is the cross.");
        Assert.AreEqual(SystemColors.Highlight, hc.Accent, "The sweep is Highlight.");
        Assert.AreEqual(Accent, normal.Tick, "Otherwise the tick is the accent.");
    }

    // ---- Words

    [TestMethod]
    public void TheTooltipsAreTheStoryWordsAndNothingElse()
    {
        WidgetSnapshot snapshot = OnThisPc();

        Assert.AreEqual("Connecting", Of(snapshot, GaugeStoryKind.Spinning, true).Tooltip);
        Assert.AreEqual("Connected", Of(snapshot, GaugeStoryKind.Tick, true).Tooltip);
        Assert.AreEqual("Didn't connect", Of(snapshot, GaugeStoryKind.Cross, true).Tooltip);
        Assert.AreEqual("Disconnecting", Of(snapshot, GaugeStoryKind.Spinning, false).Tooltip);
        Assert.AreEqual("Didn't disconnect", Of(snapshot, GaugeStoryKind.Cross, false).Tooltip);
    }

    [TestMethod]
    public void TheStoryIsPartOfTheContentAndTheModeAndTheFigureBeneathItAreKept()
    {
        WidgetSnapshot snapshot = OnThisPc();
        GaugeContent rest = GaugeContent.From(snapshot, Now, GaugeDisplaySettings.Default);
        GaugeContent story = Of(snapshot, GaugeStoryKind.Spinning, true);

        Assert.AreEqual(new GaugeStoryView(GaugeStoryKind.Spinning, true), story.Story);
        Assert.AreEqual(default(GaugeStoryView), rest.Story);
        Assert.AreEqual(rest.Mode, story.Mode);
        Assert.AreEqual(rest.Percent, story.Percent, "The figure is kept so the tick can sweep to it.");
        Assert.AreNotEqual(rest.Tooltip, story.Tooltip);
    }

    [TestMethod]
    public void TheScreenReaderIsToldTheSameWordsOncePerChange()
    {
        WidgetSnapshot snapshot = OnThisPc();

        Assert.AreEqual("Connecting", GaugeSpeech.Value(snapshot, Now, Settings(GaugeStoryKind.Spinning, true)));
        Assert.AreEqual("Connected", GaugeSpeech.Value(snapshot, Now, Settings(GaugeStoryKind.Tick, true)));
        Assert.AreEqual("Didn't connect", GaugeSpeech.Value(snapshot, Now, Settings(GaugeStoryKind.Cross, true)));
        StringAssert.StartsWith(GaugeSpeech.Value(snapshot, Now, GaugeDisplaySettings.Default), "Left 70%", "With no story the speech is the reading's, as before.");
    }

    // ---- The push key

    [TestMethod]
    public void TheStoryAndEachFrameOfItAreDrawnChangesAndSoMakeNewPushes()
    {
        GaugePalette palette = Palette(true);
        GaugeLayout layout = GaugeLayout.For(96);
        GaugeContent rest = new(GaugeMode.Reading, 70, false, false, "");
        GaugeContent spinning = rest with { Story = new GaugeStoryView(GaugeStoryKind.Spinning, true) };

        GaugePushKey Key(GaugeContent content, GaugeStoryFrame? frame = null) =>
            GaugePushKey.Of(content, palette, layout, 0, FontFamily, Point.Empty, content.Percent ?? 0, frame);

        Assert.AreNotEqual(Key(rest), Key(spinning), "A story is a drawn change.");
        Assert.AreNotEqual(
            Key(spinning, new GaugeStoryFrame(GaugeStoryKind.Spinning, -90, 90, GaugeSlotGlyph.None, 0, 0)),
            Key(spinning, new GaugeStoryFrame(GaugeStoryKind.Spinning, -60, 90, GaugeSlotGlyph.None, 0, 0)), "Each turn of the sweep is a push.");
        Assert.AreEqual(
            Key(spinning, new GaugeStoryFrame(GaugeStoryKind.Spinning, -90, 90, GaugeSlotGlyph.None, 0, 0)),
            Key(spinning with { Tooltip = "other words" }, new GaugeStoryFrame(GaugeStoryKind.Spinning, -90, 90, GaugeSlotGlyph.None, 0, 0)),
            "The tooltip is still not part of the key.");
        Assert.AreEqual(Key(rest), Key(rest, null));
    }

    private static WidgetSnapshot OnThisPc() =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = AirPodsWhere.ThisPc,
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(70, false, null) { ReadAt = Now - TimeSpan.FromSeconds(3) },
            Right = new PartReading(60, false, null) { ReadAt = Now - TimeSpan.FromSeconds(3) },
        };

    private static GaugeDisplaySettings Settings(GaugeStoryKind kind, bool connect) =>
        GaugeDisplaySettings.Default with { Story = new GaugeStoryView(kind, connect) };

    private static GaugeContent Of(WidgetSnapshot snapshot, GaugeStoryKind kind, bool connect) =>
        GaugeContent.From(snapshot, Now, Settings(kind, connect));
}
