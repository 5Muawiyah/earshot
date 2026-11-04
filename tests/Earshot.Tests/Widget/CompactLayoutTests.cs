using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WidgetCardLayout.ForCompact: the compact card's pure layout. 360 wide, 12 padding, a row of 20t + 12, 16 at the sides, a 16
// glyph, a gap of 12, Body text, a 32 x 32 icon button; a text that wraps adds 20t a line, up to three lines.
[TestClass]
public sealed class CompactLayoutTests
{
    [TestMethod]
    [DataRow(96, 1.0, 1, 56)]
    [DataRow(96, 1.5, 1, 66)]
    [DataRow(96, 1.0, 2, 76)]
    [DataRow(96, 1.0, 3, 96)]
    [DataRow(96, 1.5, 2, 96)]
    [DataRow(96, 1.5, 3, 126)]
    [DataRow(144, 1.0, 1, 84)]
    [DataRow(144, 1.5, 1, 99)]
    public void TheHeightIsTwelvePaddingTheRowAndTwentyTAnotherLineEach(int dpi, double t, int lines, int expected)
    {
        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(dpi, t, lines);

        Assert.AreEqual(expected, layout.Height);
        Assert.AreEqual(CardPlacement.Scale(360, dpi), layout.Width);
    }

    [TestMethod]
    public void TheOneRowHeightIsTheRowsOwnHeightAndItsPadding()
    {
        foreach (double t in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            Assert.AreEqual(
                (2 * 12) + WidgetCardLayout.RowHeight(96, t), WidgetCardLayout.ForCompact(96, t, 1).Height, "12 above, the row, 12 below at text size " + t);
        }
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-4, 1)]
    [DataRow(4, 3)]
    [DataRow(40, 3)]
    public void LinesAreHeldToOneToThree(int asked, int held)
    {
        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(96, 1.0, asked);

        Assert.AreEqual(held, layout.Lines);
        Assert.AreEqual(WidgetCardLayout.ForCompact(96, 1.0, held).Height, layout.Height);
    }

    [TestMethod]
    public void AGlyphAnIconButtonAndTheTextSitWhereTheDesignPutsThem()
    {
        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(96, 1.0, 1, hasGlyph: true, WidgetCardLayout.CompactButton.Icon);

        Assert.AreEqual(new Rectangle(16, 20, 16, 16), layout.Glyph, "16 px glyph, 16 in, centred in the 32 row that starts 12 down");
        Assert.AreEqual(new Rectangle(360 - 16 - 32, 12, 32, 32), layout.Action, "A 32 x 32 button, 16 from the right edge");
        Assert.AreEqual(16 + 16 + 12, layout.Text.X, "The text starts after the glyph and a gap of 12");
        Assert.AreEqual(layout.Action.X - 12, layout.Text.Right, "The text stops a gap short of the button");
        Assert.AreEqual(layout.Text.Width, WidgetCardLayout.CompactTextWidth(96, 1.0, true, WidgetCardLayout.CompactButton.Icon));
    }

    [TestMethod]
    public void WithNoGlyphTheTextStartsAtTheSidePadding()
    {
        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(96, 1.0, 1, hasGlyph: false);

        Assert.IsTrue(layout.Glyph.IsEmpty);
        Assert.IsTrue(layout.Action.IsEmpty);
        Assert.AreEqual(16, layout.Text.X);
        Assert.AreEqual(360 - 32, layout.Text.Width);
    }

    [TestMethod]
    public void ATextActionStandsRightAlignedOnTheLastLine()
    {
        const int actionWidth = 90;
        WidgetCardLayout.CompactLayout one = WidgetCardLayout.ForCompact(96, 1.0, 1, true, WidgetCardLayout.CompactButton.Text, actionWidth);
        WidgetCardLayout.CompactLayout two = WidgetCardLayout.ForCompact(96, 1.0, 2, true, WidgetCardLayout.CompactButton.Text, actionWidth);

        Assert.AreEqual(360 - 16, one.Action.Right, "Right-aligned to the side padding");
        Assert.AreEqual(actionWidth, one.Action.Width);
        Assert.AreEqual(12, one.Action.Y, "One line: the row");
        Assert.AreEqual(12 + 20, two.Action.Y, "Two lines: the row of the second");
        Assert.AreEqual(two.Height - 12, two.Action.Bottom, "The button ends 12 above the bottom edge");
        Assert.IsLessThanOrEqualTo(two.Action.X, two.Text.Right, "The text does not run under the button");
    }

    [TestMethod]
    public void TheGlyphAndAnIconButtonStayInTheMiddleOfAWrappedCard()
    {
        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(96, 1.0, 3, true, WidgetCardLayout.CompactButton.Icon);

        Assert.AreEqual(96, layout.Height);
        Assert.AreEqual(12 + ((72 - 32) / 2), layout.Action.Y, "A 32 button in the 72 between the paddings");
        Assert.AreEqual(12 + ((72 - 16) / 2), layout.Glyph.Y, "A 16 glyph the same");
    }

    [TestMethod]
    public void EverythingIsInsideTheCardAtEveryScaleTextSizeAndLineCount()
    {
        foreach (int dpi in new[] { 96, 120, 144, 192 })
        {
            foreach (double t in new[] { 1.0, 1.25, 1.5, 2.25 })
            {
                foreach (int lines in new[] { 1, 2, 3 })
                {
                    foreach (WidgetCardLayout.CompactButton button in Enum.GetValues<WidgetCardLayout.CompactButton>())
                    {
                        WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(dpi, t, lines, true, button, CardPlacement.Scale(80, dpi));
                        var card = new Rectangle(0, 0, layout.Width, layout.Height);
                        string where = " at " + dpi + " dpi, text " + t + ", " + lines + " lines, " + button;
                        Assert.IsTrue(card.Contains(layout.Glyph), "Glyph" + where);
                        Assert.IsTrue(card.Contains(layout.Text), "Text" + where);
                        Assert.IsTrue(layout.Action.IsEmpty || card.Contains(layout.Action), "Action" + where);
                        Assert.IsFalse(layout.Action.IntersectsWith(layout.Text), "Action over the text" + where);
                        Assert.IsFalse(layout.Action.IntersectsWith(layout.Glyph), "Action over the glyph" + where);
                    }
                }
            }
        }
    }
}
