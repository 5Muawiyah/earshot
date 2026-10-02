using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The gauge drawn with GDI+ and sampled: the colour at a known point of each piece is the design's token, in light, dark and high
// contrast, at 100%, 125% and 150% display scale, for every state of the design's table, the away case mark and all six orders.
// Windows only (GDI+ draws the bitmap).
[TestClass]
public sealed class GaugeDesignPixelTests
{
    private const string FontFamily = "Segoe UI";
    private static readonly int[] Dpis = [96, 120, 144];
    private static readonly Color Accent = Color.FromArgb(0x00, 0x5F, 0xB8);

    private static GaugePalette Palette(DesignTheme theme) =>
        theme == DesignTheme.HighContrast
            ? GaugePalette.Create(lightTheme: true, Accent, highContrast: true, System.Drawing.SystemColors.WindowText)
            : GaugePalette.Create(lightTheme: theme == DesignTheme.Light, Accent, highContrast: false, theme == DesignTheme.Light ? Color.Black : Color.White);

    private static Color AccentFor(DesignTheme theme) => theme == DesignTheme.HighContrast ? System.Drawing.SystemColors.Highlight : Accent;

    private static Bitmap Draw(GaugeContent content, DesignTheme theme, int dpi, GaugeOrder order = GaugeOrder.RingNumberBolt) =>
        GaugeRenderer.Render(content, Palette(theme), GaugeLayout.For(dpi, order), hover: false, FontFamily);

    // The pixel on the ring's centre line at an angle clockwise from 12 o'clock.
    private static Color OnRing(Bitmap bitmap, GaugeLayout layout, double degrees)
    {
        PointF c = GaugeRenderer.RingCentre(layout);
        double radians = degrees * Math.PI / 180.0;
        int x = (int)Math.Floor(c.X + (layout.RingRadius * Math.Sin(radians)));
        int y = (int)Math.Floor(c.Y - (layout.RingRadius * Math.Cos(radians)));
        return bitmap.GetPixel(x, y);
    }

    private static void AssertColour(Color expected, Color actual, int tolerance, string message) =>
        Assert.IsLessThanOrEqualTo(tolerance, DesignPixels.Distance(expected, actual), message + ": expected " + expected + ", drawn " + actual);

    // The fullest ink a slot holds: its brightest pixel by alpha, which text and glyphs reach on their stems.
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

    private static void AssertInkIs(Bitmap bitmap, Rectangle slot, Color ink, string message)
    {
        Color drawn = Fullest(bitmap, slot);
        Assert.IsGreaterThanOrEqualTo((int)(ink.A * 0.75), drawn.A, message + ": something is drawn in " + slot);
        AssertColour(Color.FromArgb(drawn.A, ink), drawn, 12, message + " (its colour)");
    }

    // No ink in an area: every pixel at the idle alpha. Except the one column named as the ring's anti-alias fringe, where the alpha
    // may reach FringeAlphaCap and no more (so a pixel of real ink there still fails).
    private static void AssertNoInk(Bitmap bitmap, Rectangle area, string message, int fringeColumn = -1, int fringeCap = 0)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int allowed = x == fringeColumn ? fringeCap : GaugePalette.IdleAlpha + 1;
                Assert.IsLessThanOrEqualTo(allowed, (int)bitmap.GetPixel(x, y).A, message + " at " + x + "," + y);
            }
        }
    }

    // Reading: the track and an accent arc, the pair in ink, the value in ink, no bolt.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void AReadingIsTheTrackAnAccentArcTheInkPairAndTheValue(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 60, false, false, ""), theme, dpi);

        AssertColour(p.Track, OnRing(bitmap, layout, 300), 2, "The track beyond the arc");
        AssertColour(AccentFor(theme), OnRing(bitmap, layout, 90), 2, "The arc at 3 o'clock");
        AssertColour(p.Ink, MarkPixel(bitmap, layout), 2, "The pair");
        AssertInkIs(bitmap, layout.NumberSlot, p.Ink, "The value");
        AssertNoInk(bitmap, layout.ChargingSlot, "No bolt");
    }

    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void ChargingAddsABoltInInk(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 60, false, true, ""), theme, dpi);

        AssertColour(AccentFor(theme), OnRing(bitmap, layout, 90), 2, "The arc");
        AssertInkIs(bitmap, layout.ChargingSlot, p.Ink, "The bolt");
        AssertInkIs(bitmap, layout.NumberSlot, p.Ink, "The value");
    }

    // Low (20% or less): the arc and the value in caution.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void LowIsACautionArcAndACautionValue(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 15, true, true, ""), theme, dpi);

        AssertColour(p.Caution, OnRing(bitmap, layout, 30), 2, "The arc at 1 o'clock");
        AssertColour(p.Track, OnRing(bitmap, layout, 90), 2, "Past 15% the track shows");
        AssertInkIs(bitmap, layout.NumberSlot, p.Caution, "The value");
        AssertInkIs(bitmap, layout.ChargingSlot, p.Ink, "A bolt, in ink, per charging");
        AssertColour(p.Ink, MarkPixel(bitmap, layout), 2, "The pair stays ink");
    }

    // No recent reading: the track only, the pair in tertiary, no value, no bolt.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void NoRecentReadingIsTheTrackAloneAndATertiaryPair(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.MarkOnly, null, false, false, ""), theme, dpi);

        foreach (double angle in new[] { 30.0, 90.0, 180.0, 300.0 })
        {
            AssertColour(p.Track, OnRing(bitmap, layout, angle), 2, "The track at " + angle);
        }

        AssertColour(p.Tertiary, MarkPixel(bitmap, layout), 2, "The pair");
        AssertNoInk(bitmap, layout.NumberSlot, "No value");
        AssertNoInk(bitmap, layout.ChargingSlot, "No bolt");
    }

    // Not on this PC, no case value: no ring (its place kept), the pair in disabled, nothing else.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void NotOnThisPcHidesTheRingAndDrawsADisabledPair(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, ""), theme, dpi);

        Assert.IsLessThanOrEqualTo(GaugePalette.IdleAlpha + 1, (int)OnRing(bitmap, layout, 300).A, "No ring");
        Assert.IsLessThanOrEqualTo(GaugePalette.IdleAlpha + 1, (int)OnRing(bitmap, layout, 90).A, "No ring");
        AssertColour(p.Disabled, MarkPixel(bitmap, layout), 2, "The pair");
        AssertNoInk(bitmap, layout.NumberSlot, "No value");
        AssertNoInk(bitmap, layout.ChargingSlot, "No bolt");
    }

    // On another device: no ring, a tertiary pair and the phone glyph in the number slot, tertiary.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void OnAnotherDeviceHidesTheRingAndShowsATertiaryPairAndPhone(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, ""), theme, dpi);

        Assert.IsLessThanOrEqualTo(GaugePalette.IdleAlpha + 1, (int)OnRing(bitmap, layout, 300).A, "No ring");
        AssertColour(p.Tertiary, MarkPixel(bitmap, layout), 2, "The pair");
        AssertInkIs(bitmap, layout.NumberSlot, p.Tertiary, "The phone");
        AssertNoInk(bitmap, layout.ChargingSlot, "No bolt");
    }

    // A last reading and an estimate on this PC: the ring, the value and the bolt in tertiary; the pair stays ink.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void ALastReadingAndAnEstimateAreTertiaryThroughout(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        foreach (bool estimated in new[] { false, true })
        {
            using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 62, false, true, "") { Tertiary = true, Estimated = estimated }, theme, dpi);
            string what = estimated ? "estimate" : "last reading";

            Color arc = OnRing(bitmap, layout, 90);
            AssertColour(DesignPixels.Over(p.Tertiary, p.Track), arc, 3, "The arc, in tertiary over the track, " + what);
            AssertInkIs(bitmap, layout.NumberSlot, p.Tertiary, "The value, " + what);
            AssertInkIs(bitmap, layout.ChargingSlot, p.Tertiary, "The bolt, " + what);
            AssertColour(p.Ink, MarkPixel(bitmap, layout), 2, "The pair stays ink on this PC, " + what);
        }
    }

    // An estimate carries "≈" before its digits, so its ink in the number slot is wider than the same digits as a last reading.
    [TestMethod]
    public void AnEstimateHasAnExtraMarkBeforeItsDigits()
    {
        GaugeLayout layout = GaugeLayout.For(96);
        using Bitmap plain = Draw(new GaugeContent(GaugeMode.Reading, 62, false, false, "") { Tertiary = true }, DesignTheme.Light, 96);
        using Bitmap estimate = Draw(new GaugeContent(GaugeMode.Reading, 62, false, false, "") { Tertiary = true, Estimated = true }, DesignTheme.Light, 96);

        // The background is the idle fill (alpha 1), read inside the window: the corner pixel is outside the rounded rectangle and clear.
        (int First, int Last) a = DesignPixels.InkColumns(plain, layout.NumberSlot, plain.GetPixel(layout.Width / 2, 2))!.Value;
        (int First, int Last) b = DesignPixels.InkColumns(estimate, layout.NumberSlot, estimate.GetPixel(layout.Width / 2, 2))!.Value;
        Assert.IsGreaterThan(a.Last - a.First, b.Last - b.First, "≈ adds a mark.");
    }

    // The away case: the case mark in tertiary, the ring and the value at the case's value in tertiary, a bolt when it was charging;
    // a live case value is drawn in full ink and the accent.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void AwayWithACaseValueDrawsTheCaseMarkTheRingAndTheNumberInTertiary(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap last = Draw(new GaugeContent(GaugeMode.CaseAway, 80, false, true, "") { CaseMark = true, Tertiary = true }, theme, dpi);

        AssertColour(DesignPixels.Over(p.Tertiary, p.Track), OnRing(last, layout, 90), 3, "The arc");
        AssertColour(p.Track, OnRing(last, layout, 340), 2, "The track past 80%");
        AssertColour(p.Tertiary, CaseMarkPixel(last, layout), 2, "The case mark");
        AssertInkIs(last, layout.NumberSlot, p.Tertiary, "The value");
        AssertInkIs(last, layout.ChargingSlot, p.Tertiary, "The bolt: the case was charging");

        using Bitmap live = Draw(new GaugeContent(GaugeMode.CaseAway, 75, false, false, "") { CaseMark = true }, theme, dpi);
        AssertColour(AccentFor(theme), OnRing(live, layout, 90), 2, "A live case value fills in the accent");
        AssertColour(p.Ink, CaseMarkPixel(live, layout), 2, "And its mark is ink");
        AssertInkIs(live, layout.NumberSlot, p.Ink, "And its value");
        AssertNoInk(live, layout.ChargingSlot, "No bolt when it is not charging");
    }

    // The case mark replaces the earbud pair. The case mark (the middle 14 units of the 24 unit grid) covers all but a sliver of where
    // the pair is, so the pair's absence shows in the case's own seam: the thin unpainted line a little below the lid. The pair's left
    // head runs from grid y 6 to 11 and x 6.5 to 11.5, across the seam (grid y 9.76 to 10.88); at x 8 only that head could fill it.
    [TestMethod]
    public void TheCaseMarkTakesThePlaceOfThePair()
    {
        GaugeLayout layout = GaugeLayout.For(144);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.CaseAway, 80, false, false, "") { CaseMark = true, Tertiary = true }, DesignTheme.Light, 144);
        Assert.IsGreaterThan(0, (int)CaseMarkPixel(bitmap, layout).A);

        float k = layout.Mark.Width / (float)GaugeLayout.MarkGrid;
        int seamX = (int)(layout.Mark.X + (8f * k));
        int seamY = (int)(layout.Mark.Y + (10.32f * k));
        Assert.IsLessThanOrEqualTo(GaugePalette.IdleAlpha + 1, (int)bitmap.GetPixel(seamX, seamY).A, "The pair is not drawn with the case mark: the seam is clear where the pair's head would fill it.");

        // And the same point is filled when the pair is drawn instead.
        using Bitmap pair = Draw(new GaugeContent(GaugeMode.Reading, 80, false, false, ""), DesignTheme.Light, 144);
        Assert.IsGreaterThan(GaugePalette.IdleAlpha + 1, (int)pair.GetPixel(seamX, seamY).A, "Sanity: the pair's head is there.");
    }

    // GDI+ anti-aliases an edge a little past the geometry it is given: probed on this machine (Windows 11, GDI+ with
    // SmoothingMode.AntiAlias and PixelOffsetMode.HighQuality), the ring's outer edge, which sits exactly on its box's edge, writes alpha
    // 0x14 to 0x1D (8 to 11%) into the one pixel column to the right of the box in light and dark, and up to 0x2F (18%) under high
    // contrast (a reviewer measured 0x20 there; this probe, run over every theme, scale and order, is the figure kept), and at most 1
    // into the second column or into the column to its left. So that one column may hold alpha up to a little over the measured top
    // (0x24, and 0x34 under high contrast) and every other pixel of a gap or padding must be clear.
    internal static int FringeAlphaCap(bool highContrast) => highContrast ? 0x34 : 0x24;

    // All six orders at the three scales: the ring's track, the value and the bolt are each where the layout puts them, and the gaps
    // between them are empty.
    [TestMethod]
    [DynamicData(nameof(ThemesAndOrdersAndDpis))]
    public void EveryOrderPutsTheRingTheNumberAndTheBoltWhereTheLayoutSaysInTheTokens(DesignTheme theme, GaugeOrder order, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi, order);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 60, false, true, ""), theme, dpi, order);
        string where = " (" + order + ", dpi " + dpi + ")";

        Assert.AreEqual(layout.Width, bitmap.Width);
        AssertColour(p.Track, OnRing(bitmap, layout, 300), 2, "The track" + where);
        AssertColour(AccentFor(theme), OnRing(bitmap, layout, 90), 2, "The arc" + where);
        AssertInkIs(bitmap, layout.NumberSlot, p.Ink, "The value" + where);
        AssertInkIs(bitmap, layout.ChargingSlot, p.Ink, "The bolt" + where);

        // Left to right, the gaps hold nothing.
        (GaugePiece first, GaugePiece second, GaugePiece third) = GaugeOrders.Sequence(order);
        Rectangle Slot(GaugePiece piece) => piece switch { GaugePiece.Ring => layout.RingBox, GaugePiece.Number => layout.NumberSlot, _ => layout.ChargingSlot };
        foreach ((GaugePiece left, GaugePiece right) in new[] { (first, second), (second, third) })
        {
            int gapLeft = Slot(left).Right;
            int gapRight = Slot(right).Left;
            AssertNoInk(
                bitmap, new Rectangle(gapLeft, 0, Math.Max(0, gapRight - gapLeft), layout.Height), "The gap between " + left + " and " + right + where,
                fringeColumn: left == GaugePiece.Ring ? layout.RingBox.Right : -1, fringeCap: FringeAlphaCap(theme == DesignTheme.HighContrast));
        }

        AssertNoInk(bitmap, new Rectangle(0, 0, Slot(first).Left, layout.Height), "The left padding" + where);
        int rightPadStart = Slot(third).Right;
        AssertNoInk(
            bitmap, new Rectangle(rightPadStart, 0, Math.Max(0, layout.Width - rightPadStart), layout.Height), "The right padding" + where,
            fringeColumn: third == GaugePiece.Ring ? layout.RingBox.Right : -1, fringeCap: FringeAlphaCap(theme == DesignTheme.HighContrast));
    }

    // The earbud pair's four shapes are filled in ink at their grid places.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void TheEarbudPairIsFilledAtTheDesignsGridPlaces(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        using Bitmap bitmap = Draw(new GaugeContent(GaugeMode.Reading, 60, false, false, ""), theme, dpi);

        foreach (RectangleF shape in layout.EarbudShapes().Shapes)
        {
            AssertColour(p.Ink, bitmap.GetPixel((int)(shape.X + (shape.Width / 2)), (int)(shape.Y + (shape.Height / 2))), 2, "A shape's middle " + shape);
        }
    }

    // Hover fills the whole window with the subtle hover token, an idle window with alpha 1.
    [TestMethod]
    [DynamicData(nameof(ThemesAndDpis))]
    public void HoverIsTheSubtleHoverTokenOverTheWholeWindowAndIdleIsAlpha1(DesignTheme theme, int dpi)
    {
        GaugePalette p = Palette(theme);
        GaugeLayout layout = GaugeLayout.For(dpi);
        var content = new GaugeContent(GaugeMode.Reading, 60, false, false, "");
        using Bitmap hover = GaugeRenderer.Render(content, p, layout, hover: true, FontFamily);
        using Bitmap idle = GaugeRenderer.Render(content, p, layout, hover: false, FontFamily);

        AssertColour(p.HoverFill, hover.GetPixel(layout.Width / 2, 2), 2, "Hover, at the top edge");
        Assert.AreEqual(GaugePalette.IdleAlpha, idle.GetPixel(layout.Width / 2, 2).A, "Idle");
        Assert.AreEqual(0, hover.GetPixel(0, 0).A, "The corner is outside the 4 px radius.");
    }

    private static Color MarkPixel(Bitmap bitmap, GaugeLayout layout)
    {
        RectangleF head = layout.EarbudShapes().Shapes[0];
        return bitmap.GetPixel((int)(head.X + (head.Width / 2)), (int)(head.Y + (head.Height / 2)));
    }

    // A point on the case mark's box: its middle.
    private static Color CaseMarkPixel(Bitmap bitmap, GaugeLayout layout)
    {
        RectangleF r = GaugeRenderer.CaseMarkRect(layout);
        return bitmap.GetPixel((int)(r.X + (r.Width / 2)), (int)(r.Y + (r.Height * 0.75f)));
    }

    public static IEnumerable<object[]> ThemesAndDpis()
    {
        foreach (DesignTheme theme in DesignPixels.Themes)
        {
            foreach (int dpi in Dpis)
            {
                yield return [theme, dpi];
            }
        }
    }

    public static IEnumerable<object[]> ThemesAndOrdersAndDpis()
    {
        foreach (DesignTheme theme in DesignPixels.Themes)
        {
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                foreach (int dpi in Dpis)
                {
                    yield return [theme, order, dpi];
                }
            }
        }
    }
}
