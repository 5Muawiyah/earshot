using System.Drawing;
using Earshot.Popup;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The settings page (More expanded, the gauge order open) and the Updates page drawn with GDI+ and sampled against the tokens at
// 100% and 150% display scale and text size, in light, dark and high contrast. Row heights are the design's: a single line is the
// control (20t + 12) plus 8 above and below. Windows only (GDI+ draws the bitmap).
[TestClass]
public sealed class SettingsAndUpdatesDesignPixelTests
{
    public static IEnumerable<object[]> ThemesAndScales() => DesignPixels.ThemesAndScales();

    private static void AssertNear(Color expected, Color actual, int tolerance, string message) =>
        Assert.IsLessThanOrEqualTo(tolerance, DesignPixels.Distance(expected, actual), message + ": expected " + expected + ", drawn " + actual);

    private static Color RowOver(DesignTokens t, CardColours c) => DesignPixels.Over(c.RowFill, t.SolidSurface);

    // Every row sits on a surface: the settings row fill over the card's surface, a 1 px stroke along its top edge.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void EverySettingsSurfaceIsTheRowFillWithTheRowStroke(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, more: true, order: true);
            using Bitmap bitmap = CardKit.Render(card);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));
            Color fill = RowOver(tokens, c);
            SettingsLayout layout = card.CurrentSettingsLayout!;

            AssertNear(tokens.SolidSurface, bitmap.GetPixel(2, layout.Frame.Body.Y + 2), 1, "The card's surface");
            Assert.IsNotEmpty(layout.Surfaces);
            foreach (Rectangle surface in layout.Surfaces)
            {
                // Sampled at the middle of the surface's first row: the middle of a surface that holds several rows can be exactly
                // where one row's divider is, which is the stroke and not the fill.
                SettingsItem first = layout.Items.First(i => surface.Contains(i.Bounds));
                AssertNear(fill, bitmap.GetPixel(surface.X + 3, first.Bounds.Y + (first.Bounds.Height / 2)), 2, "The fill of " + surface);
                AssertNear(DesignPixels.Over(c.RowStroke, fill), bitmap.GetPixel(surface.X + (surface.Width / 2), surface.Y), 4, "The stroke along the top of " + surface);
            }
        });
    }

    // A single line is the control plus 8 above and below; the same at every scale: Scale(8) on each side.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void SingleLineRowsAreTheControlPlusTheRowPadding(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, more: true);
            int expected = WidgetCardLayout.RowHeight(dpi, t) + (2 * CardPlacement.Scale(8, dpi));

            Assert.AreEqual(expected, CardKit.Row(card, SettingsRowId.LeftClick).Bounds.Height);
            Assert.AreEqual(expected, CardKit.Row(card, SettingsRowId.GaugeDisplay).Bounds.Height);
            Assert.AreEqual(expected, CardKit.Row(card, SettingsRowId.More).Bounds.Height);
            Assert.AreEqual(WidgetCardLayout.RowHeight(dpi, t), CardKit.Row(card, SettingsRowId.GaugeDisplay).A.Height, "A combo is 20t + 12 high.");
            Assert.AreEqual(CardPlacement.Scale(360, dpi), card.ClientSize.Width);
        });
    }

    // The toggle: the accent track and the text-on-accent knob when on; no fill and the knob in the secondary ink when off.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void ATogglePaintsTheAccentWhenOnAndTheRowSurfaceWhenOff(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = true }), dpi, more: true);
            using (Bitmap on = CardKit.Render(card))
            {
                Rectangle track = CardKit.Row(card, SettingsRowId.LeftClick).A;
                DesignPixels.AssertSameColour(c.Accent, on.GetPixel(track.X + (track.Width / 5), track.Y + (track.Height / 2)), "On: an accent track");
                Assert.AreEqual(new Size(CardPlacement.Scale(40, dpi), CardPlacement.Scale(20, dpi)), track.Size, "40 by 20");
            }

            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { LeftClickConnects = false }), dpi, more: true);
            using (Bitmap off = CardKit.Render(card))
            {
                SettingsItem row = CardKit.Row(card, SettingsRowId.LeftClick);
                Rectangle track = row.A;
                AssertNear(RowOver(tokens, c), off.GetPixel(track.Right - 4, track.Y + (track.Height / 2)), 2, "Off: no fill, the row's own surface shows");
            }
        });
    }

    // The 1 px row stroke between an expander's header and what is under it.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheMoreExpanderSeparatesItsContentWithTheRowStroke(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, more: true);
            using Bitmap bitmap = CardKit.Render(card);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));

            SettingsItem first = CardKit.Row(card, SettingsRowId.GaugePosition);
            Assert.IsTrue(first.DividerAbove);
            AssertNear(DesignPixels.Over(c.RowStroke, RowOver(tokens, c)), bitmap.GetPixel(first.Bounds.X + 40, first.Bounds.Top), 4, "The stroke above the first row of More");
            Assert.AreEqual(1, card.CurrentSettingsLayout!.Surfaces.Count(s => s.Contains(first.Bounds) && s.Contains(CardKit.Row(card, SettingsRowId.More).Bounds)), "One surface holds More and what it opens");
        });
    }

    // The gauge order's open grid: the chosen picture has a 2 px accent stroke inside it, the others the 1 px control stroke.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheChosenOrderTileHasATwoPixelAccentStrokeAndTheOthersTheControlStroke(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugeOrder = GaugeOrder.NumberRingBolt }), dpi, more: false, order: true);
            using Bitmap bitmap = CardKit.Render(card);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));
            SettingsItem grid = card.CurrentSettingsLayout!.Items.Single(i => i.Tiles.Count == 6);
            Color underTile = DesignPixels.Over(c.ControlFill, RowOver(tokens, c));

            Rectangle chosen = grid.Tiles[(int)GaugeOrder.NumberRingBolt];
            int middle = chosen.Y + (chosen.Height / 2);
            DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(chosen.X + 1, middle), "The chosen picture: an accent stroke, its second pixel in");
            DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(chosen.X, middle), "and its first");

            Rectangle other = grid.Tiles[(int)GaugeOrder.RingNumberBolt];
            AssertNear(DesignPixels.Over(c.ControlStroke, RowOver(tokens, c)), bitmap.GetPixel(other.X, other.Y + (other.Height / 2)), 6, "Another picture: the 1 px control stroke");
            // Inside the stroke and clear of the rounded corner (radius 4 at 100%) and of the picture, which has a margin above it: a
            // point 3 in and 14 across, at the scale. A point 3 by 2 in sits on the corner's curve, where anti-aliasing mixes in the stroke.
            AssertNear(underTile, bitmap.GetPixel(other.X + CardPlacement.Scale(14, dpi), other.Y + CardPlacement.Scale(3, dpi)), 8, "and the control fill inside it");
            Assert.AreEqual(CardPlacement.Scale(52, dpi), other.Height, "Tiles are 52 high at 100%");
        });
    }

    // The Updates page: an accent tile with the earbud pair in text on accent, the surfaces, and the toggle.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheUpdatesPageHasAnAccentTileTheSurfacesAndTheTokens(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.UpToDate), autoCheck: true, showRepair: true), dpi);
            using Bitmap bitmap = CardKit.Render(card);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));
            WidgetCardLayout.SetupLayout layout = card.CurrentSetupLayout!;
            Color fill = RowOver(tokens, c);

            Assert.AreEqual(new Size(CardPlacement.Scale(40, dpi), CardPlacement.Scale(40, dpi)), layout.Tile.Size, "The tile is 40 by 40");
            DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(layout.Tile.X + CardPlacement.Scale(5, dpi), layout.Tile.Y + CardPlacement.Scale(5, dpi)), "An accent tile");
            AssertNear(c.OnAccent, DesignPixels.Extreme(bitmap, Rectangle.Inflate(layout.Tile, -4, -4), c.Accent), 4, "The earbud pair is the text on accent");
            AssertNear(fill, bitmap.GetPixel(layout.StatusSurface.X + 3, layout.StatusSurface.Y + (layout.StatusSurface.Height / 2)), 2, "The status surface");
            foreach (WidgetCardLayout.UpdatesRowLayout row in layout.UpdateRows)
            {
                AssertNear(fill, bitmap.GetPixel(row.Surface.X + 3, row.Surface.Y + (row.Surface.Height / 2)), 2, "Row " + row.Index + " surface");
                Assert.AreEqual(WidgetCardLayout.RowHeight(dpi, t) + (2 * CardPlacement.Scale(8, dpi)), row.Surface.Height, "Row " + row.Index + " height");
            }

            Rectangle toggle = layout.UpdateRows.Single(r => r.Index == 0).Control;
            DesignPixels.AssertSameColour(c.Accent, bitmap.GetPixel(toggle.X + (toggle.Width / 5), toggle.Y + (toggle.Height / 2)), "Check automatically is on: an accent track");
        });
    }

    // States: Up to date offers Check as a standard button, Ready to install offers Install (the page's Update) as the accent one, and
    // a download shows its progress in the accent over the track.
    [TestMethod]
    [DynamicData(nameof(ThemesAndScales))]
    public void TheUpdatesActionIsStandardForCheckAccentForUpdateAndADownloadShowsProgress(DesignTheme theme, int dpi, double t)
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = DesignPixels.NewCard(theme, t);
            DesignTokens tokens = DesignPixels.Tokens(theme);
            var c = CardColours.For(tokens, DesignPixels.Accent(theme));

            card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.UpToDate)), dpi);
            using (Bitmap standard = CardKit.Render(card))
            {
                Rectangle action = card.CurrentSetupLayout!.Action;
                Assert.IsFalse(action.IsEmpty, "Check sits in the status row");
                AssertNear(DesignPixels.Over(c.ControlFill, RowOver(tokens, c)), standard.GetPixel(action.X + 6, action.Y + (action.Height / 2)), 3, "Up to date: a standard button");
            }

            card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.Available)), dpi);
            using (Bitmap primary = CardKit.Render(card))
            {
                Rectangle action = card.CurrentSetupLayout!.Action;
                DesignPixels.AssertSameColour(c.Accent, primary.GetPixel(action.X + 6, action.Y + (action.Height / 2)), "Ready to install: the accent button");
                AssertNear(c.OnAccent, DesignPixels.Extreme(primary, Rectangle.Inflate(action, -10, -6), c.Accent), 60, "Its text is the token for text on accent");
            }

            card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.Downloading, percent: 60)), dpi);
            using (Bitmap progress = CardKit.Render(card))
            {
                Rectangle row = card.CurrentSetupLayout!.Progress;
                DesignPixels.AssertSameColour(c.Accent, progress.GetPixel(row.X + 4, row.Y + (row.Height / 2)), "Downloading: the fill is the accent from the left");
                DesignPixels.AssertNotSameColour(c.Accent, progress.GetPixel(row.Right - 60, row.Y + (row.Height / 2)), "and is not full at 60%");
            }
        });
    }
}
