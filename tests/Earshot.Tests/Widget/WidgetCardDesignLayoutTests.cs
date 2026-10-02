using System.Drawing;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The design's card figures (its "Row heights" table), at 100% and 150% text size and at 100% and 150% display scale, from the
// pure layout: no window, no drawing. Lengths that hold text grow with the text size t (an icon button is 16t + 16, a row or button
// 20t + 12); padding, gaps and the width of 360 do not.
[TestClass]
public sealed class WidgetCardDesignLayoutTests
{
    // The table's figures at one display scale and text size: title row, icon button, label, mark, value, bar, read-time line, status row,
    // button.
    public sealed record Figure(
        int Dpi, double TextScale, int Title, int Icon, int Label, Size Mark, int Value, Size Bar, int ReadTime, int Status, int Button);

    public static IEnumerable<object[]> Figures =>
    [
        // 100% display, 100% text
        [new Figure(96, 1.0, Title: 32, Icon: 32, Label: 16, new Size(36, 32), Value: 28, new Size(64, 3), ReadTime: 16, Status: 32, Button: 32)],

        // 100% display, 150% text: 40 icon, 24 label, 54 by 48 mark, 42 value, the bar unchanged, 24 read, 42 row
        [new Figure(96, 1.5, Title: 40, Icon: 40, Label: 24, new Size(54, 48), Value: 42, new Size(64, 3), ReadTime: 24, Status: 42, Button: 42)],

        // 150% display, 100% text: every figure 1.5 times, the bar's 3 rounding to 5
        [new Figure(144, 1.0, Title: 48, Icon: 48, Label: 24, new Size(54, 48), Value: 42, new Size(96, 5), ReadTime: 24, Status: 48, Button: 48)],

        // 150% display, 150% text: the two multiply
        [new Figure(144, 1.5, Title: 60, Icon: 60, Label: 36, new Size(81, 72), Value: 63, new Size(96, 5), ReadTime: 36, Status: 63, Button: 63)],
    ];

    [TestMethod]
    [DynamicData(nameof(Figures))]
    public void EveryFigureOfTheRowHeightsTableHolds(Figure f)
    {
        WidgetCardLayout.Layout l = WidgetCardLayout.Compute(f.Dpi, showSwitch: false, textScale: f.TextScale);
        string at = " at " + f.Dpi + " dpi, text " + f.TextScale;

        Assert.AreEqual(f.Title, l.Title.Height, "Title row" + at);
        Assert.AreEqual(new Size(f.Icon, f.Icon), l.Gear.Size, "Icon button" + at);
        Assert.AreEqual(new Size(f.Icon, f.Icon), l.Refresh.Size, "Refresh icon button" + at);
        foreach (WidgetCardLayout.ColumnLayout c in new[] { l.Left, l.Right, l.Case })
        {
            Assert.AreEqual(f.Label, c.Label.Height, "Label" + at);
            Assert.AreEqual(f.Mark, c.Glyph.Size, "Mark" + at);
            Assert.AreEqual(f.Value, c.Percent.Height, "Value" + at);
            Assert.AreEqual(f.Bar, c.Bar.Size, "Bar" + at);
            Assert.AreEqual(f.ReadTime, c.ReadTime.Height, "Read-time line (always reserved)" + at);
        }

        Assert.AreEqual(f.Status, l.WhereLine.Height, "Status row" + at);
        Assert.AreEqual(f.Button, l.Button.Height, "Button" + at);
    }

    // The bar is 64 wide at 100% display whatever the text size: it does not grow with t.
    [TestMethod]
    public void TheBarIs64WideAtEveryTextSize()
    {
        foreach (double t in new[] { 1.0, 1.25, 1.5, 2.0, 2.25 })
        {
            Assert.AreEqual(64, WidgetCardLayout.Compute(96, false, textScale: t).Left.Bar.Width, "text " + t);
        }
    }

    // Padding 12 top and 16 on the sides and the bottom, a 12 section gap, 8 between columns, 6 inside one: positions at 100%.
    [TestMethod]
    public void SpacingIsTheDesignsAt100Percent()
    {
        WidgetCardLayout.Layout l = WidgetCardLayout.Compute(96, showSwitch: false);

        Assert.AreEqual(360, l.Width);
        Assert.AreEqual(12, l.Title.Top, "12 top padding.");
        Assert.AreEqual(16, l.Title.Left, "16 side padding.");
        Assert.AreEqual(l.Title.Bottom + 12, l.Left.Label.Top, "A 12 section gap under the title row.");
        Assert.AreEqual(l.Left.Label.Right + 8, l.Right.Label.Left, "8 between columns.");
        Assert.AreEqual(l.Right.Label.Right + 8, l.Case.Label.Left);
        Assert.AreEqual(360 - 16, l.Case.Label.Right, "16 on the right.");
        WidgetCardLayout.ColumnLayout c = l.Left;
        Assert.AreEqual(c.Label.Bottom + 6, c.Glyph.Top, "6 inside a column: label to mark.");
        Assert.AreEqual(c.Glyph.Bottom + 6, c.Percent.Top, "mark to value.");
        Assert.AreEqual(c.Percent.Bottom + 6, c.Bar.Top, "value to bar.");
        Assert.AreEqual(c.Bar.Bottom + 6, c.ReadTime.Top, "bar to the read-time line.");
        Assert.AreEqual(c.ReadTime.Bottom + 12, l.WhereLine.Top, "A 12 gap to the status row.");
        Assert.AreEqual(l.WhereLine.Bottom + 12, l.Button.Top, "And to the button.");
        Assert.AreEqual(l.Button.Bottom + 16, l.Height, "16 bottom padding.");
    }

    // The card's total height: 279 at 100% and 353 at 150% text without the switch row; 323 and 407 with it. The design says "about 300"
    // and "about 410", which the figures above give once the auto-pause switch row is counted at 150%.
    [TestMethod]
    [DataRow(1.0, false, 279)]
    [DataRow(1.0, true, 323)]
    [DataRow(1.5, false, 353)]
    [DataRow(1.5, true, 407)]
    public void TheCardsTotalHeightIsTheSumOfTheTable(double t, bool withSwitch, int expected) =>
        Assert.AreEqual(expected, WidgetCardLayout.Compute(96, withSwitch, textScale: t).Height);

    [TestMethod]
    public void TheCardGrowsInHeightOnlyTheWidthStays360AtEveryTextSize()
    {
        foreach (double t in new[] { 1.0, 1.5, 2.25 })
        {
            Assert.AreEqual(360, WidgetCardLayout.Compute(96, false, textScale: t).Width, "text " + t);
        }

        Assert.IsGreaterThan(WidgetCardLayout.Compute(96, false, textScale: 1.0).Height, WidgetCardLayout.Compute(96, false, textScale: 2.25).Height);
    }

    // The bolt's slot is reserved to the right of the value inside its column, so the value stays centred whether or not a bolt shows.
    [TestMethod]
    public void TheBoltSlotIsReservedInsideTheColumnAtTheValuesRight()
    {
        WidgetCardLayout.Layout l = WidgetCardLayout.Compute(96, false);
        foreach (WidgetCardLayout.ColumnLayout c in new[] { l.Left, l.Right, l.Case })
        {
            Assert.AreEqual(new Size(12, 12), c.BoltSlot.Size);
            Assert.IsTrue(c.Percent.Contains(c.BoltSlot), "Inside the value's own row.");
            Assert.IsGreaterThan(c.Percent.X + (c.Percent.Width / 2), c.BoltSlot.X, "To the right of the middle.");
        }
    }

    [TestMethod]
    public void IconButtonAndRowSizesFollowTheTextSize()
    {
        Assert.AreEqual(32, WidgetCardLayout.IconButtonSize(96, 1.0));
        Assert.AreEqual(40, WidgetCardLayout.IconButtonSize(96, 1.5));
        Assert.AreEqual(32, WidgetCardLayout.RowHeight(96, 1.0));
        Assert.AreEqual(42, WidgetCardLayout.RowHeight(96, 1.5));
        Assert.AreEqual(CardPlacement.Scale(32, 144), WidgetCardLayout.RowHeight(144, 1.0));
    }
}
