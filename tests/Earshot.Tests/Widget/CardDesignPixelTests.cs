using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card drawn with GDI+ and sampled against the tokens, at 100% and 150% display scale and text size, in light, dark and high
// contrast: a fresh card, a stale one, one with estimates, and the case-open card. Windows only (GDI+ draws the bitmap).
[TestClass]
public sealed class CardDesignPixelTests
{
    public static IEnumerable<object[]> ThemesAndScales() => DesignPixels.ThemesAndScales();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static WidgetSnapshot Snapshot(TimeSpan age)
    {
        DateTimeOffset at = Now - age;
        return CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = at }, new PartReading(60, false, null) { ReadAt = at }, at) with
        {
            Case = new PartReading(90, false, null) { ReadAt = at },
        };
    }

    private static WidgetCardModel Model(WidgetSnapshot snapshot) => CardKit.MainModel(snapshot) with { Now = Now };

    private static (Bitmap Bitmap, WidgetCardLayout.Layout Layout, Color Surface, CardColours Colours) Draw(
        WidgetCard card, WidgetCardModel model, DesignTheme theme, int dpi)
    {
        card.Render(model, dpi);
        Bitmap bitmap = CardKit.Render(card);
        DesignTokens tokens = DesignPixels.Tokens(theme);
        return (bitmap, card.CurrentMainLayout, tokens.SolidSurface, CardColours.For(tokens, DesignPixels.Accent(theme)));
    }

    private static void AssertNear(Color expected, Color actual, int tolerance, string message) =>
        Assert.IsLessThanOrEqualTo(tolerance, DesignPixels.Distance(expected, actual), message + ": expected " + expected + ", drawn " + actual);

    private static int Closer(Color drawn, Color a, Color b) => DesignPixels.Distance(drawn, a).CompareTo(DesignPixels.Distance(drawn, b));

    // The surface is the solid surface token, the card is 360 wide at 100% and the layout is the table's at every scale and text size.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheCardIsTheSolidSurfaceAtTheTablesSizes(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, _) = Draw(card, Model(Snapshot(TimeSpan.FromSeconds(5))), theme, dpi);
            using Bitmap owned = bitmap;

            Assert.AreEqual(Earshot.Popup.CardPlacement.Scale(360, dpi), bitmap.Width);
            Assert.AreEqual(layout.Height, bitmap.Height);
            AssertNear(surface, bitmap.GetPixel(2, 2), 1, "The surface");
            AssertNear(surface, bitmap.GetPixel(bitmap.Width - 3, bitmap.Height - 3), 1, "The surface");
            Assert.AreEqual(WidgetCardLayout.Compute(dpi, showSwitch: false, textScale: t).Height, layout.Height, "The layout is the pure table's.");
        });
    }

    // Fresh: the value in Text primary, the bar's fill in the accent over a 1 px track in Progress track, the mark in Text primary, the
    // label in Text secondary, and the read-time line empty (reserved).
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void AFreshCardIsPrimaryValuesAccentBarsAndNoReadTime(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, Model(Snapshot(TimeSpan.FromSeconds(5))), theme, dpi);
            using Bitmap owned = bitmap;

            foreach (WidgetCardLayout.ColumnLayout column in new[] { layout.Left, layout.Right, layout.Case })
            {
                Color primary = DesignPixels.Over(c.Text, surface);
                AssertNear(primary, DesignPixels.Extreme(bitmap, Rectangle.Inflate(column.Glyph, -2, -2), surface), 3, "The mark is Text primary");
                AssertNear(primary, DesignPixels.Extreme(bitmap, Rectangle.Intersect(column.Percent, new Rectangle(column.Percent.X, column.Percent.Y, column.BoltSlot.X - column.Percent.X, column.Percent.Height)), surface), 40, "The value is Text primary");
                // The darkest pixel of 12 px text falls short of the ink by anti-aliasing, in proportion to the ink's contrast with the surface:
                // measured at 100% with pure black on white (high contrast) it is 81 of 255. So the allowance is 40% of the contrast, at least 60.
                Color secondary = DesignPixels.Over(c.TextSecondary, surface);
                int labelAllowance = Math.Max(60, (int)(DesignPixels.Distance(secondary, surface) * 0.4));
                AssertNear(secondary, DesignPixels.Extreme(bitmap, column.Label, surface), labelAllowance, "The label is Text secondary");
                DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(column.Bar.X + 1, column.Bar.Y), "The bar's fill is the accent, on its first row");
                DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(column.Bar.X + 1, column.Bar.Bottom - 1), "and its last row: the fill is the whole bar height");
                Assert.IsFalse(CardKit.HasInk(bitmap, column.ReadTime, surface), "A fresh value shows no read time, and the line's place is kept");
            }

            // A part at 70%: its track shows beyond the fill, 1 px high in the middle of the bar.
            WidgetCardLayout.ColumnLayout left = layout.Left;
            int trackRow = left.Bar.Y + ((left.Bar.Height - Math.Max(1, Earshot.Popup.CardPlacement.Scale(1, dpi))) / 2);
            AssertNear(DesignPixels.Over(c.Track, surface), bitmap.GetPixel(left.Bar.Right - 2, trackRow), 2, "The track, Progress track");
            AssertNear(surface, bitmap.GetPixel(left.Bar.Right - 2, left.Bar.Y), 1, "Above the 1 px track the surface shows: the track is not the bar's height");
        });
    }

    // Stale (a reading four minutes old): the values and the bars in Text tertiary, each column showing the read-time clock and "4 min".
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void AStaleCardIsTertiaryValuesTertiaryBarsAndAReadTimePerColumn(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, Model(Snapshot(TimeSpan.FromMinutes(4))), theme, dpi);
            using Bitmap owned = bitmap;

            Color tertiary = DesignPixels.Over(c.TextTertiary, surface);
            Color primary = DesignPixels.Over(c.Text, surface);
            foreach (WidgetCardLayout.ColumnLayout column in new[] { layout.Left, layout.Right, layout.Case })
            {
                Color value = DesignPixels.Extreme(bitmap, new Rectangle(column.Percent.X, column.Percent.Y, column.BoltSlot.X - column.Percent.X, column.Percent.Height), surface);
                Assert.IsLessThan(0, Closer(value, tertiary, primary), "The value is nearer Text tertiary than primary: " + value);
                AssertNear(tertiary, bitmap.GetPixel(column.Bar.X + 1, column.Bar.Y), 2, "The bar's fill is Text tertiary (its top row, above the track)");
                Assert.IsTrue(CardKit.HasInk(bitmap, column.ReadTime, surface), "The read-time line shows the clock and the age");
                Color read = DesignPixels.Extreme(bitmap, column.ReadTime, surface);
                Assert.IsLessThan(0, Closer(read, tertiary, primary), "The read time is Text tertiary: " + read);
            }

            Assert.AreEqual("4 min", WidgetCopy.StaleAgeAmount(TimeSpan.FromMinutes(4)), "The age is the table's example.");
            Assert.AreNotEqual(c.Accent, bitmap.GetPixel(layout.Left.Bar.X + 1, layout.Left.Bar.Y), "A stale bar is not the accent");
        });
    }

    // Estimates: "≈" before the value and the age of the reading it grew from, in the stale style.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void AnEstimateIsTertiaryHasAnExtraMarkBeforeItsValueAndShowsTheAgeOfItsReading(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            DateTimeOffset read = Now - TimeSpan.FromMinutes(4);
            ShownBattery estimates = new(
                new ShownPart(66, true, ReadingKind.Estimated, read) { ReadPercent = 62 },
                new ShownPart(66, true, ReadingKind.Last, read),
                new ShownPart(94, false, ReadingKind.Estimated, Now - TimeSpan.FromHours(2)) { ReadPercent = 90 },
                null, null, null);
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, Model(Snapshot(TimeSpan.FromMinutes(4))) with { Parts = estimates }, theme, dpi);
            using Bitmap owned = bitmap;

            Assert.StartsWith("≈", WidgetCopy.PercentText(66, estimated: true));
            WidgetCardLayout.ColumnLayout estimate = layout.Left;
            WidgetCardLayout.ColumnLayout last = layout.Right;
            (int First, int Last) a = DesignPixels.InkColumns(bitmap, new Rectangle(estimate.Percent.X, estimate.Percent.Y, estimate.BoltSlot.X - estimate.Percent.X, estimate.Percent.Height), surface)!.Value;
            (int First, int Last) b = DesignPixels.InkColumns(bitmap, new Rectangle(last.Percent.X, last.Percent.Y, last.BoltSlot.X - last.Percent.X, last.Percent.Height), surface)!.Value;
            Assert.IsGreaterThan(b.Last - b.First, a.Last - a.First, "66% as an estimate is wider than 66% as a last reading: it has the ≈");
            Assert.IsTrue(CardKit.HasInk(bitmap, estimate.ReadTime, surface), "The age of the reading it grew from");
            Assert.IsTrue(CardKit.HasInk(bitmap, layout.Case.ReadTime, surface), "The case's estimate shows its age too");
            Color tertiary = DesignPixels.Over(c.TextTertiary, surface);
            Color value = DesignPixels.Extreme(bitmap, new Rectangle(estimate.Percent.X, estimate.Percent.Y, estimate.BoltSlot.X - estimate.Percent.X, estimate.Percent.Height), surface);
            Assert.IsLessThan(0, Closer(value, tertiary, DesignPixels.Over(c.Text, surface)), "The estimate is Text tertiary: " + value);
        });
    }

    // A charging part shows the bolt in its reserved slot, right of the value; the value stays centred without one.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void AChargingPartShowsTheBoltInItsReservedSlotAndOthersLeaveItEmpty(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, _) = Draw(card, Model(Snapshot(TimeSpan.FromSeconds(5))), theme, dpi);
            using Bitmap owned = bitmap;

            Assert.IsTrue(CardKit.HasInk(bitmap, layout.Left.BoltSlot, surface), "The left bud is charging: a bolt");
            Assert.IsFalse(CardKit.HasInk(bitmap, layout.Right.BoltSlot, surface), "The right bud is not: the slot is empty");
        });
    }

    // The case-open card: the same columns on the same surface, "Case open" in the status row and the close button where the gear is.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheCaseOpenCardIsTheSameSurfaceWithCaseOpenInTheStatusRowAndACloseButton(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t, notice: true);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, Model(Snapshot(TimeSpan.FromSeconds(5))), theme, dpi);
            using Bitmap owned = bitmap;

            AssertNear(surface, bitmap.GetPixel(2, 2), 1, "The surface");
            Assert.AreEqual(WidgetCopy.CaseOpen, card.StatusRow.Text);
            Assert.AreEqual(c.Text, card.StatusRow.Ink, "The status row is Text primary");
            Assert.IsTrue(CardKit.HasInk(bitmap, layout.WhereLine, surface), "Case open is drawn");
            Assert.IsTrue(CardKit.HasInk(bitmap, layout.Gear, surface), "The close button is where the gear is");
            AssertNear(DesignPixels.Over(c.Text, surface), DesignPixels.Extreme(bitmap, Rectangle.Inflate(layout.Left.Glyph, -2, -2), surface), 3, "The mark is Text primary");
            DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(layout.Left.Bar.X + 1, layout.Left.Bar.Y), "A fresh value has the accent bar");
        });
    }

    // The status row's states: On this PC (primary), nothing heard (caution), Bluetooth off (its own glyph and a chevron).
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheStatusRowIsPrimaryCautionOrBluetoothOff(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, Model(Snapshot(TimeSpan.FromSeconds(5))), theme, dpi);
            using (bitmap)
            {
                Assert.AreEqual(FluentGlyphs.OnThisPc, card.StatusRow.Glyph);
                Assert.AreEqual("On this PC", card.StatusRow.Text);
                Assert.AreEqual(c.Text, card.StatusRow.Ink);
            }

            WidgetCardModel nothing = Model(Snapshot(TimeSpan.FromSeconds(5))) with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.NothingHeard) };
            (Bitmap cautionBitmap, _, _, _) = Draw(card, nothing, theme, dpi);
            using (cautionBitmap)
            {
                Assert.AreEqual(FluentGlyphs.Warning, card.StatusRow.Glyph);
                Assert.AreEqual("Open the case", card.StatusRow.Text);
                Assert.AreEqual(c.Caution, card.StatusRow.Ink, "Caution");
                AssertNear(DesignPixels.Over(c.Caution, surface), DesignPixels.Extreme(cautionBitmap, layout.WhereLine, surface), 90, "The row's ink is caution");
            }

            WidgetCardModel off = Model(Snapshot(TimeSpan.FromSeconds(5))) with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.BluetoothOff) };
            (Bitmap offBitmap, _, _, _) = Draw(card, off, theme, dpi);
            using (offBitmap)
            {
                Assert.AreEqual(FluentGlyphs.BluetoothOff, card.StatusRow.Glyph);
                Assert.AreEqual("Bluetooth off", card.StatusRow.Text);
                Assert.IsFalse(card.ButtonUsable, "Connect is disabled");
                Assert.IsTrue(CardKit.HasInk(offBitmap, new Rectangle(layout.WhereLine.Right - 16, layout.WhereLine.Y, 16, layout.WhereLine.Height), surface), "The chevron to Bluetooth settings");
            }
        });
    }

    // The button: accent with the text on accent when it connects, the standard control with Text primary when it disconnects.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void ConnectIsTheAccentWithTextOnAccentAndDisconnectIsTheStandardControl(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            WidgetCardModel connect = Model(Snapshot(TimeSpan.FromSeconds(5))) with { ConnectIntent = true };
            (Bitmap bitmap, WidgetCardLayout.Layout layout, Color surface, CardColours c) = Draw(card, connect, theme, dpi);
            using (bitmap)
            {
                Rectangle b = layout.Button;
                DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(b.X + 6, b.Y + (b.Height / 2)), "Connect is the accent");
                AssertNear(c.OnAccent, DesignPixels.Extreme(bitmap, Rectangle.Inflate(b, -12, -8), c.Accent), 60, "The text on it is the token for text on accent");
            }

            (Bitmap standard, _, _, _) = Draw(card, connect with { ConnectIntent = false }, theme, dpi);
            using (standard)
            {
                Rectangle b = layout.Button;
                AssertNear(DesignPixels.Over(c.ControlFill, surface), standard.GetPixel(b.X + 6, b.Y + (b.Height / 2)), 2, "Disconnect is the control fill");
            }
        });
    }
}
