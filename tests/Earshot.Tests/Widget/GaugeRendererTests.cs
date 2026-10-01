using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// GaugeRenderer pixel checks, off screen: every state at 100%, 125% and 150%, light and dark, with the
// assertions at the coordinates the design gives (GaugeLayout). The accent is a colour of the test's own that
// matches nothing else in the palette, so a ring pixel can only be the fill, the track or nothing.
[TestClass]
public sealed class GaugeRendererTests
{
    private const string FontFamily = "Segoe UI";

    private static readonly Color Accent = Color.FromArgb(12, 160, 88);

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static GaugePalette Palette(bool light) => GaugePalette.Create(light, Accent, highContrast: false, light ? Color.Black : Color.White);

    private static Bitmap Draw(GaugeContent content, int dpi, bool light, bool hover = false) =>
        GaugeRenderer.Render(content, Palette(light), GaugeLayout.For(dpi), hover, FontFamily);

    private static GaugeContent Reading(int percent, bool charging = false, int low = 20) =>
        new(GaugeMode.Reading, percent, percent <= low, charging, "");

    private static GaugeContent Of(GaugeMode mode) => new(mode, null, false, false, "");

    // The pixel on the ring's centre line at an angle measured clockwise from 12 o'clock.
    private static Color RingPixel(Bitmap bitmap, GaugeLayout layout, double degrees)
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

    private static bool IsTrack(Color pixel, Color track) => Math.Abs(pixel.A - track.A) <= 30 && pixel.A > 20;

    private static int MaxAlpha(Bitmap bitmap, Rectangle area)
    {
        int max = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                max = Math.Max(max, bitmap.GetPixel(x, y).A);
            }
        }

        return max;
    }

    // Pixels in area that are clearly painted (not the idle hit-test alpha).
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

    // ---- The bitmap ----

    [TestMethod]
    [DataRow(96, 74, 40)]
    [DataRow(120, 93, 50)]
    [DataRow(144, 111, 60)]
    public void TheBitmapIsTheDesignsSizeAtEachScale(int dpi, int width, int height)
    {
        using Bitmap bitmap = Draw(Reading(60), dpi, light: true);

        Assert.AreEqual(width, bitmap.Width);
        Assert.AreEqual(height, bitmap.Height);
        Assert.AreEqual(width, GaugeLayout.For(dpi).Width);
    }

    // Every caller now says what time it is (the reading's age is drawn from it), so no overload may take a
    // snapshot and quietly read the clock itself, and the width comes from GaugeLayout, not a second route.
    [TestMethod]
    public void NoRenderOverloadTakesASnapshotWithoutATimeAndThereIsNoSecondWidthRoute()
    {
        const System.Reflection.BindingFlags all =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        string[] offenders = typeof(GaugeRenderer).GetMethods(all)
            .Where(m => m.Name == "Render"
                && m.GetParameters().Any(p => p.ParameterType == typeof(WidgetSnapshot))
                && !m.GetParameters().Any(p => p.ParameterType == typeof(DateTimeOffset)))
            .Select(m => m.ToString() ?? m.Name)
            .ToArray();

        Assert.AreEqual(0, offenders.Length, "Render overloads that read the clock themselves: " + string.Join("; ", offenders));
        Assert.IsNull(typeof(GaugeRenderer).GetMethod("WidthFor", all), "GaugeLayout.For(dpi).Width is the one way to get the width.");
    }

    // ---- The ring ----

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(96, false)]
    [DataRow(120, true)]
    [DataRow(120, false)]
    [DataRow(144, true)]
    [DataRow(144, false)]
    public void TheRingRunsClockwiseFromTwelveOClockForTheLowerBudsShare(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        foreach (int percent in new[] { 10, 60, 100 })
        {
            using Bitmap bitmap = Draw(Reading(percent, low: -1), dpi, light);
            double end = 3.6 * percent;

            if (percent < 100)
            {
                Assert.IsTrue(IsFill(RingPixel(bitmap, layout, Math.Max(3, end - 8)), Accent), percent + "%: filled just before the end at " + dpi);
                Assert.IsTrue(IsTrack(RingPixel(bitmap, layout, end + 8), palette.Track), percent + "%: track just after the end at " + dpi);
                Assert.IsTrue(IsFill(RingPixel(bitmap, layout, 8), Accent), percent + "%: the arc starts at twelve o'clock and runs clockwise (a little past it)");
                Assert.IsTrue(IsTrack(RingPixel(bitmap, layout, 352), palette.Track), percent + "%: just before twelve o'clock is track, so it is not counter-clockwise");
            }
            else
            {
                foreach (double angle in new[] { 8, 90, 180, 270, 352 })
                {
                    Assert.IsTrue(IsFill(RingPixel(bitmap, layout, angle), Accent), "100%: whole ring filled at " + angle + " degrees");
                }
            }
        }
    }

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(96, false)]
    [DataRow(144, true)]
    [DataRow(144, false)]
    public void ZeroPercentDrawsTheTrackAndNoFill(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        using Bitmap bitmap = Draw(Reading(0), dpi, light);

        foreach (double angle in new[] { 8, 90, 180, 270, 352 })
        {
            Color p = RingPixel(bitmap, layout, angle);
            Assert.IsTrue(IsTrack(p, palette.Track), "0%: track only at " + angle + " degrees, got " + p);
            Assert.IsFalse(IsFill(p, Accent));
        }
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void TheRingFillIsTheAccentColourNotTheTextColour(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap light = Draw(Reading(100), dpi, light: true);
        using Bitmap dark = Draw(Reading(100), dpi, light: false);

        Assert.IsTrue(Near(RingPixel(light, layout, 90), Accent, 24), "light theme ring: " + RingPixel(light, layout, 90));
        Assert.IsTrue(Near(RingPixel(dark, layout, 90), Accent, 24), "dark theme ring: " + RingPixel(dark, layout, 90));
    }

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(96, false)]
    [DataRow(120, true)]
    [DataRow(144, false)]
    public void TheRingAndTheNumberTurnToCautionAtTheThresholdAndNotJustAbove(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        var settings = GaugeDisplaySettings.Default;

        GaugeContent atThreshold = ContentAt(20, settings);
        GaugeContent above = ContentAt(21, settings);
        using Bitmap lowBitmap = Draw(atThreshold, dpi, light);
        using Bitmap okBitmap = Draw(above, dpi, light);

        Assert.IsTrue(atThreshold.Low);
        Assert.IsFalse(above.Low);
        Assert.IsTrue(IsFill(RingPixel(lowBitmap, layout, 8), palette.Caution), "ring at 20%: " + RingPixel(lowBitmap, layout, 8));
        Assert.IsTrue(IsFill(RingPixel(okBitmap, layout, 8), Accent), "ring at 21%: " + RingPixel(okBitmap, layout, 8));
        Assert.IsTrue(Near(Strongest(lowBitmap, layout.NumberSlot), palette.Caution, 40), "number at 20%: " + Strongest(lowBitmap, layout.NumberSlot));
        Assert.IsTrue(Near(Strongest(okBitmap, layout.NumberSlot), palette.Ink, 40), "number at 21%: " + Strongest(okBitmap, layout.NumberSlot));
    }

    private static GaugeContent ContentAt(int percent, GaugeDisplaySettings settings)
    {
        var buds = new PartReading(percent, null, null) { ReadAt = Now };
        WidgetSnapshot snapshot = WidgetSnapshot.Empty(WidgetWatcherState.Started) with { Where = AirPodsWhere.ThisPc, Left = buds, Right = buds };
        return GaugeContent.From(snapshot, Now, settings);
    }

    private static Color Strongest(Bitmap bitmap, Rectangle area)
    {
        Color best = Color.Transparent;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                Color p = bitmap.GetPixel(x, y);
                if (p.A > best.A)
                {
                    best = p;
                }
            }
        }

        return best;
    }

    // ---- The number ----

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void TheNumberStaysInsideItsSlotForZeroAndOneHundredAndTheWidthNeverChanges(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap zero = Draw(Reading(0), dpi, light: true);
        using Bitmap hundred = Draw(Reading(100), dpi, light: true);
        Rectangle beyond = Rectangle.FromLTRB(layout.NumberSlot.Right, 0, layout.Width, layout.Height);

        List<Point> zeroInk = Painted(zero, layout.NumberSlot);
        List<Point> hundredInk = Painted(hundred, layout.NumberSlot);

        Assert.AreEqual(zero.Width, hundred.Width);
        Assert.IsNotEmpty(zeroInk, "0 is drawn in the slot.");
        Assert.IsNotEmpty(hundredInk, "100 is drawn in the slot.");
        Assert.IsEmpty(Painted(zero, beyond, 20), "Nothing of 0 lies beyond the slot.");
        Assert.IsEmpty(Painted(hundred, beyond, 20), "Nothing of 100 lies beyond the slot: the slot fits three digits.");
        Assert.IsGreaterThan(zeroInk.Max(p => p.X), hundredInk.Max(p => p.X), "Three digits reach further right than one.");
        Assert.IsLessThanOrEqualTo(layout.NumberSlot.Left + 2, zeroInk.Min(p => p.X), "The number is left aligned in its slot.");
    }

    // Every digit takes the same width, so 11 and 88 end in the same column (tabular figures).
    [TestMethod]
    [DataRow(96)]
    [DataRow(144)]
    public void EveryDigitHasTheSameWidth(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        float cell = GaugeRenderer.DigitCell(FontFamily, layout.TypePixels);
        using Bitmap ones = Draw(Reading(11), dpi, light: true);
        using Bitmap eights = Draw(Reading(88), dpi, light: true);

        int onesRight = Painted(ones, layout.NumberSlot).Max(p => p.X);
        int eightsRight = Painted(eights, layout.NumberSlot).Max(p => p.X);

        Assert.IsGreaterThan(0f, cell);
        Assert.IsLessThan(cell, (float)Math.Abs(eightsRight - onesRight), "A '1' is drawn in a cell as wide as an '8'.");
    }

    // ---- The mark, the bolt and the other states ----

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(120, false)]
    [DataRow(144, true)]
    public void TheChargingSlotIsAlwaysReservedAndTheBoltFillsItOnlyWhenCharging(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap idle = Draw(Reading(50), dpi, light);
        using Bitmap charging = Draw(Reading(50, charging: true), dpi, light);
        Rectangle slot = layout.ChargingSlot;

        Assert.IsEmpty(Painted(idle, slot, 20), "No bolt, and nothing else in the slot.");
        Assert.IsNotEmpty(Painted(charging, slot, 200), "The bolt is drawn inside the reserved slot.");
        Assert.AreEqual(idle.Width, charging.Width, "The window does not change width for a bolt.");
        Assert.AreEqual(
            string.Join(",", Painted(idle, layout.NumberSlot)),
            string.Join(",", Painted(charging, layout.NumberSlot)),
            "The number does not move for a bolt.");
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void TheEarbudMarkIsAtFortyPercentWhenNotOnThisPc(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap full = Draw(Of(GaugeMode.MarkOnly), dpi, light: false);
        using Bitmap away = Draw(Of(GaugeMode.NotOnThisPc), dpi, light: false);

        int fullAlpha = MaxAlpha(full, layout.Mark);
        int awayAlpha = MaxAlpha(away, layout.Mark);

        Assert.IsGreaterThanOrEqualTo(250, fullAlpha, "Sanity: the full mark is solid.");
        Assert.IsInRange(96, 108, awayAlpha, "The away mark is 40% of full: " + awayAlpha);
    }

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(96, false)]
    [DataRow(144, true)]
    [DataRow(144, false)]
    public void OnlyTheReadingStateDrawsARing(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        foreach (GaugeMode mode in new[] { GaugeMode.MarkOnly, GaugeMode.NotOnThisPc, GaugeMode.OnOtherDevice })
        {
            using Bitmap bitmap = Draw(Of(mode), dpi, light);
            foreach (double angle in new[] { 8, 90, 180, 270, 352 })
            {
                Assert.IsLessThan(20, (int)RingPixel(bitmap, layout, angle).A, mode + ": no ring at " + angle);
            }

            Assert.IsEmpty(Painted(bitmap, layout.ChargingSlot, 20), mode + ": no bolt");
        }
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void OnAnotherDeviceAPhoneMarkIsDrawnInTheNumberSlotAtFullInk(int dpi)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light: true);
        using Bitmap bitmap = Draw(Of(GaugeMode.OnOtherDevice), dpi, light: true);

        List<Point> phone = Painted(bitmap, layout.NumberSlot);

        Assert.IsNotEmpty(phone, "The phone outline is in the number slot.");
        Assert.IsTrue(Near(Strongest(bitmap, layout.NumberSlot), palette.Ink, 40));
        Assert.IsGreaterThanOrEqualTo(250, MaxAlpha(bitmap, layout.Mark), "The mark is at full opacity beside the phone.");
        Assert.IsLessThanOrEqualTo(layout.NumberSlot.Left + layout.PhoneSize, phone.Max(p => p.X) + 1, "The phone is PhoneSize wide, left aligned in the slot.");
    }

    // ---- Hover and hit testing ----

    [TestMethod]
    [DataRow(96, true)]
    [DataRow(96, false)]
    [DataRow(144, true)]
    public void HoverPaintsTheWholeWindowAndIdleKeepsItClickable(int dpi, bool light)
    {
        GaugeLayout layout = GaugeLayout.For(dpi);
        GaugePalette palette = Palette(light);
        using Bitmap idle = Draw(Of(GaugeMode.MarkOnly), dpi, light);
        using Bitmap hover = Draw(Of(GaugeMode.MarkOnly), dpi, light, hover: true);
        var spot = new Point(layout.Width - 2, layout.Height / 2);
        var corner = new Point(0, 0);

        Assert.AreEqual(GaugePalette.IdleAlpha, idle.GetPixel(spot.X, spot.Y).A, "Idle is alpha 1, never 0: the whole gauge is one click target.");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(palette.HoverFill.A - hover.GetPixel(spot.X, spot.Y).A), "Hover is the design's gauge.hover fill.");
        Assert.AreEqual(0, idle.GetPixel(corner.X, corner.Y).A, "The rounded corner is transparent: a click there reaches the taskbar.");
        Assert.AreEqual(0, hover.GetPixel(corner.X, corner.Y).A);
    }

    // ---- The snapshot path (the way the window and the probe call it) ----

    [TestMethod]
    public void ASnapshotDrawsThroughTheContentRules()
    {
        var buds = new PartReading(70, true, null) { ReadAt = Now - TimeSpan.FromMinutes(2) };
        WidgetSnapshot snapshot = WidgetSnapshot.Empty(WidgetWatcherState.Started) with { Where = AirPodsWhere.ThisPc, Left = buds, Right = buds with { Percent = 60 } };
        GaugeLayout layout = GaugeLayout.For(96);

        using Bitmap bitmap = GaugeRenderer.Render(snapshot, Now, 96, 48, Color.Black, hover: false, FontFamily, accent: Accent);

        Assert.IsTrue(IsFill(RingPixel(bitmap, layout, 8), Accent));
        Assert.IsTrue(IsTrack(RingPixel(bitmap, layout, 352), Palette(true).Track), "60% is the lower bud, so the ring is not full.");
        Assert.IsNotEmpty(Painted(bitmap, layout.ChargingSlot, 200), "The left bud is charging, a proved flag.");
    }

    [TestMethod]
    public void ANumberIsNeverDrawnForAnUnprovedReading()
    {
        WidgetSnapshot snapshot = WidgetSnapshot.Empty(WidgetWatcherState.Started) with { Where = AirPodsWhere.ThisPc };
        GaugeLayout layout = GaugeLayout.For(96);

        using Bitmap bitmap = GaugeRenderer.Render(snapshot, Now, 96, 48, Color.Black, hover: false, FontFamily, accent: Accent);

        Assert.IsEmpty(Painted(bitmap, layout.NumberSlot, 20));
        Assert.IsLessThan(20, (int)RingPixel(bitmap, layout, 90).A, "No ring without a proved reading.");
    }
}
