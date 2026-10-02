using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The six orders of the gauge's ring, number and bolt: the window never changes size, the three slots sit in the
// order asked for with the gap budget where the rule puts it, and the default order is the layout the gauge has
// always had, to the pixel.
[TestClass]
public sealed class GaugeOrderLayoutTests
{
    private static readonly int[] Dpis = [96, 120, 144, 110, 168, 192];

    private static readonly string[] ExpectedNames =
        ["Ring, number, bolt", "Ring, bolt, number", "Number, ring, bolt", "Number, bolt, ring", "Bolt, ring, number", "Bolt, number, ring"];

    // The design's table of left edges, one row per order: ring (R), number (N) and bolt (B) at 100%, 125% and 150%.
    private sealed record Row(GaugeOrder Order, (int R, int N, int B) At100, (int R, int N, int B) At125, (int R, int N, int B) At150);

    private static readonly Row[] Table =
    [
        new(GaugeOrder.RingNumberBolt, (5, 33, 57), (6, 41, 71), (7, 49, 85)),
        new(GaugeOrder.RingBoltNumber, (5, 49, 33), (6, 61, 41), (7, 73, 49)),
        new(GaugeOrder.NumberRingBolt, (29, 5, 57), (36, 6, 71), (43, 7, 85)),
        new(GaugeOrder.NumberBoltRing, (45, 5, 29), (56, 6, 36), (67, 7, 43)),
        new(GaugeOrder.BoltRingNumber, (21, 49, 5), (26, 61, 6), (31, 73, 7)),
        new(GaugeOrder.BoltNumberRing, (45, 21, 5), (56, 26, 6), (67, 31, 7)),
    ];

    [TestMethod]
    public void EveryOrderAtEveryDesignScaleIsTheTablesLeftEdges()
    {
        foreach (Row row in Table)
        {
            Check(row.Order, 96, row.At100);
            Check(row.Order, 120, row.At125);
            Check(row.Order, 144, row.At150);
        }
    }

    private static void Check(GaugeOrder order, int dpi, (int R, int N, int B) expected)
    {
        GaugeLayout l = GaugeLayout.For(dpi, order);
        string where = " (" + order + " at " + dpi + " dpi)";
        Assert.AreEqual(expected.R, l.RingBox.X, "Ring left edge" + where);
        Assert.AreEqual(expected.N, l.NumberSlot.X, "Number left edge" + where);
        Assert.AreEqual(expected.B, l.ChargingSlot.X, "Bolt left edge" + where);
    }

    [TestMethod]
    public void TheDefaultOrderIsExactlyWhatForWithNoOrderGives()
    {
        foreach (int dpi in Dpis)
        {
            Assert.AreEqual(GaugeLayout.For(dpi), GaugeLayout.For(dpi, GaugeOrder.RingNumberBolt));
        }
    }

    [TestMethod]
    public void AnOrderThatNamesNoneIsTheDefault()
    {
        foreach (int dpi in Dpis)
        {
            Assert.AreEqual(GaugeLayout.For(dpi), GaugeLayout.For(dpi, (GaugeOrder)42));
            Assert.AreEqual(GaugeLayout.For(dpi), GaugeLayout.For(dpi, (GaugeOrder)(-1)));
        }
    }

    private static Rectangle SlotOf(GaugeLayout l, GaugePiece piece) => piece switch
    {
        GaugePiece.Ring => l.RingBox,
        GaugePiece.Number => l.NumberSlot,
        _ => l.ChargingSlot,
    };

    [TestMethod]
    public void EveryOrderKeepsTheWindowAndEveryPiecesSizeAndOrdersThePiecesAsAskedWithOneGapBetweenNeighbours()
    {
        foreach (int dpi in Dpis)
        {
            GaugeLayout baseline = GaugeLayout.For(dpi);
            int gap = baseline.NumberSlot.X - baseline.RingBox.Right;
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                GaugeLayout l = GaugeLayout.For(dpi, order);
                string where = " (" + order + ", dpi " + dpi + ")";
                Assert.AreEqual(new Size(baseline.Width, baseline.Height), new Size(l.Width, l.Height), "The window never changes size" + where);
                Assert.AreEqual(baseline.RingBox.Size, l.RingBox.Size, "Ring box" + where);
                Assert.AreEqual(baseline.NumberSlot.Size, l.NumberSlot.Size, "Number slot" + where);
                Assert.AreEqual(baseline.ChargingSlot.Size, l.ChargingSlot.Size, "The bolt's slot is always reserved" + where);
                Assert.AreEqual(baseline.RingBox.Y, l.RingBox.Y, "Ring height" + where);

                (GaugePiece first, GaugePiece second, GaugePiece third) = GaugeOrders.Sequence(order);
                Rectangle a = SlotOf(l, first);
                Rectangle b = SlotOf(l, second);
                Rectangle c = SlotOf(l, third);
                Assert.AreEqual(baseline.RingBox.X, a.Left, "The first piece starts after the left padding" + where);
                Assert.AreEqual(gap, b.Left - a.Right, "One gap between the first and second" + where);
                Assert.AreEqual(gap, c.Left - b.Right, "One gap between the second and third" + where);
                Assert.IsTrue(new Rectangle(0, 0, l.Width, l.Height).Contains(l.RingBox), "Ring inside the window" + where);
                Assert.IsLessThanOrEqualTo(l.Width, c.Right, "The pieces fit the window" + where);
                Assert.AreEqual(l.RingBox, l.Mark, "The mark's grid is the ring box" + where);
            }
        }
    }

    [TestMethod]
    public void EveryOrderHasItsOwnNameForScreenReaders()
    {
        var names = Enum.GetValues<GaugeOrder>().Select(GaugeOrders.AccessibleName).ToList();
        CollectionAssert.AreEqual(ExpectedNames, names);
        Assert.AreEqual(6, names.Distinct().Count());
    }
}
