using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every coordinate of the ring-and-number gauge at the three scales the design states, and the rule for any
// other scale. The figures are the design's own table.
[TestClass]
public sealed class GaugeLayoutTests
{
    [TestMethod]
    public void At100PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(96, width: 74, height: 40, ringX: 7, ringSize: 24, radius: 10.5f, stroke: 2f,
            mark: new Rectangle(13, 14, 12, 12), numberX: 35, numberWidth: 22, chargeX: 57, chargeWidth: 10, bolt: new Size(8, 12));

    [TestMethod]
    public void At125PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(120, width: 93, height: 50, ringX: 9, ringSize: 30, radius: 13f, stroke: 2.5f,
            mark: new Rectangle(16, 17, 15, 15), numberX: 44, numberWidth: 28, chargeX: 72, chargeWidth: 12, bolt: new Size(10, 15));

    [TestMethod]
    public void At150PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(144, width: 111, height: 60, ringX: 10, ringSize: 36, radius: 15.75f, stroke: 3f,
            mark: new Rectangle(19, 21, 18, 18), numberX: 52, numberWidth: 33, chargeX: 85, chargeWidth: 15, bolt: new Size(12, 18));

    private static void AssertTable(
        int dpi, int width, int height, int ringX, int ringSize, float radius, float stroke,
        Rectangle mark, int numberX, int numberWidth, int chargeX, int chargeWidth, Size bolt)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        Assert.AreEqual(new Size(width, height), new Size(l.Width, l.Height));
        Assert.AreEqual(ringX, l.RingBox.X);
        Assert.AreEqual(ringSize, l.RingBox.Width);
        Assert.AreEqual(ringSize, l.RingBox.Height);
        Assert.AreEqual((height - ringSize) / 2, l.RingBox.Y, "The ring is centred on the window's vertical midline.");
        Assert.AreEqual(radius, l.RingRadius);
        Assert.AreEqual(stroke, l.RingStroke);
        Assert.AreEqual(mark, l.Mark);
        Assert.AreEqual(numberX, l.NumberSlot.X);
        Assert.AreEqual(numberWidth, l.NumberSlot.Width);
        Assert.AreEqual(chargeX, l.ChargingSlot.X);
        Assert.AreEqual(chargeWidth, l.ChargingSlot.Width);
        Assert.AreEqual(bolt, l.Bolt);
    }

    // The right padding the design states (7, 9, 11) is what is left after the charging slot.
    [TestMethod]
    [DataRow(96, 7)]
    [DataRow(120, 9)]
    [DataRow(144, 11)]
    public void TheRightPaddingIsWhatIsLeftAfterTheChargingSlot(int dpi, int rightPadding)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        Assert.AreEqual(rightPadding, l.Width - l.ChargingSlot.Right);
    }

    // The gap after the ring (4, 5, 6) is the distance from the ring box to the number slot.
    [TestMethod]
    [DataRow(96, 4)]
    [DataRow(120, 5)]
    [DataRow(144, 6)]
    public void TheGapAfterTheRingIsTheDistanceToTheNumberSlot(int dpi, int gap)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        Assert.AreEqual(gap, l.NumberSlot.X - l.RingBox.Right);
    }

    // The window is 40 px in a 48 px taskbar, so its top offset is 4, and 5 and 6 at the larger scales.
    [TestMethod]
    [DataRow(96, 48, 4)]
    [DataRow(120, 60, 5)]
    [DataRow(144, 72, 6)]
    public void TheTopOffsetInTheTaskbarMatchesTheDesign(int dpi, int taskbarHeight, int topOffset)
    {
        GaugeLayout l = GaugeLayout.For(dpi);
        var taskbar = new Rectangle(0, 1000, 1920, taskbarHeight);

        Rectangle bounds = l.BoundsAt(500, taskbar);

        Assert.AreEqual(1000 + topOffset, bounds.Top);
        Assert.AreEqual(500, bounds.Left);
        Assert.AreEqual(new Size(l.Width, l.Height), bounds.Size);
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void TheNumberSlotFitsThreeDigitsSoTheWidthNeverChanges(int dpi)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        float cell = GaugeRenderer.DigitCell("Segoe UI", l.TypePixels);

        Assert.IsLessThanOrEqualTo(l.NumberSlot.Width, (int)Math.Ceiling(cell * 3), "Three digits ('100') must fit the slot; cell " + cell);
    }

    // Any other scale scales the 100% figures and keeps the gauge in one piece: the parts run left to right
    // with no overlap and the window holds them all.
    [TestMethod]
    [DataRow(110)]
    [DataRow(168)]
    [DataRow(192)]
    [DataRow(240)]
    public void AnotherScaleKeepsThePartsInOrderInsideTheWindow(int dpi)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        Assert.IsGreaterThan(0, l.RingBox.Left);
        Assert.IsLessThanOrEqualTo(l.NumberSlot.Left, l.RingBox.Right);
        Assert.IsLessThanOrEqualTo(l.ChargingSlot.Left, l.NumberSlot.Right);
        Assert.IsLessThanOrEqualTo(l.Width, l.ChargingSlot.Right);
        Assert.IsTrue(new Rectangle(0, 0, l.Width, l.Height).Contains(l.RingBox));
        Assert.IsTrue(l.RingBox.Contains(l.Mark));
        Assert.IsGreaterThanOrEqualTo(1f, l.RingStroke);
        Assert.AreEqual((double)l.Height / 40, dpi / 96.0, 0.02);
    }
}
