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

    // The design's table for the default order, one row per scale.
    private sealed record Table(int Dpi, Size Window, int LeftPad, int Ring, int Gap, int Number, int Bolt, int RightPad);

    private static readonly Table[] Tables =
    [
        new Table(Dpi: 96, Window: new Size(74, 40), LeftPad: 7, Ring: 24, Gap: 4, Number: 22, Bolt: 10, RightPad: 7),
        new Table(Dpi: 120, Window: new Size(93, 50), LeftPad: 9, Ring: 30, Gap: 5, Number: 28, Bolt: 12, RightPad: 9),
        new Table(Dpi: 144, Window: new Size(111, 60), LeftPad: 10, Ring: 36, Gap: 6, Number: 33, Bolt: 15, RightPad: 11),
    ];

    [TestMethod]
    public void TheDefaultOrderIsTheLiteralTable()
    {
        foreach (Table t in Tables)
        {
            foreach (GaugeLayout l in new[] { GaugeLayout.For(t.Dpi), GaugeLayout.For(t.Dpi, GaugeOrder.RingNumberBolt) })
            {
                string where = " at " + t.Dpi + " dpi";
                Assert.AreEqual(t.Window, new Size(l.Width, l.Height), "Window" + where);
                Assert.AreEqual(t.LeftPad, l.RingBox.X, "Left pad" + where);
                Assert.AreEqual(t.Ring, l.RingBox.Width, "Ring" + where);
                Assert.AreEqual(t.LeftPad + t.Ring + t.Gap, l.NumberSlot.X, "Number slot" + where);
                Assert.AreEqual(t.Number, l.NumberSlot.Width);
                Assert.AreEqual(t.LeftPad + t.Ring + t.Gap + t.Number, l.ChargingSlot.X, "Bolt slot" + where);
                Assert.AreEqual(t.Bolt, l.ChargingSlot.Width);
                Assert.AreEqual(t.Window.Width - t.RightPad, l.ChargingSlot.Right, "Right pad" + where);
                Assert.IsFalse(l.NumberAlignRight, "The default order keeps the digits at the left of their slot, as before.");
            }
        }
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
    public void EveryOrderKeepsTheWindowAndEveryPiecesWidthAndOrdersThePiecesAsAsked()
    {
        foreach (int dpi in Dpis)
        {
            GaugeLayout baseline = GaugeLayout.For(dpi);
            int leftPad = baseline.RingBox.X;
            int rightPad = baseline.Width - baseline.ChargingSlot.Right;
            int gap = baseline.NumberSlot.X - baseline.RingBox.Right;
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                GaugeLayout l = GaugeLayout.For(dpi, order);
                string where = " (" + order + ", dpi " + dpi + ")";
                Assert.AreEqual(new Size(baseline.Width, baseline.Height), new Size(l.Width, l.Height), "The window never changes size" + where);
                Assert.AreEqual(baseline.RingBox.Size, l.RingBox.Size, "Ring box" + where);
                Assert.AreEqual(baseline.NumberSlot.Size, l.NumberSlot.Size, "Number slot" + where);
                Assert.AreEqual(baseline.ChargingSlot.Size, l.ChargingSlot.Size, "The bolt's slot is always reserved" + where);
                Assert.AreEqual(baseline.Bolt, l.Bolt, "Bolt" + where);
                Assert.AreEqual(baseline.RingBox.Y, l.RingBox.Y, "Ring height" + where);
                Assert.AreEqual(baseline.NumberSlot.Y, l.NumberSlot.Y);
                Assert.AreEqual(baseline.ChargingSlot.Y, l.ChargingSlot.Y);

                (GaugePiece first, GaugePiece second, GaugePiece third) = GaugeOrders.Sequence(order);
                Rectangle a = SlotOf(l, first);
                Rectangle b = SlotOf(l, second);
                Rectangle c = SlotOf(l, third);
                Assert.AreEqual(leftPad, a.Left, "The first piece starts after the left padding" + where);
                Assert.AreEqual(baseline.Width - rightPad, c.Right, "The last piece ends before the right padding" + where);
                Assert.IsLessThanOrEqualTo(b.Left, a.Right, "No overlap, first and second" + where);
                Assert.IsLessThanOrEqualTo(c.Left, b.Right, "No overlap, second and third" + where);

                // The gap budget: all of it next to the ring when the ring is at an end; half each side, the left half
                // rounded down, when it is in the middle. The number and the bolt touch.
                int ringIndex = first == GaugePiece.Ring ? 0 : second == GaugePiece.Ring ? 1 : 2;
                switch (ringIndex)
                {
                    case 0:
                        Assert.AreEqual(gap, b.Left - a.Right, "Ring first: the whole gap after it" + where);
                        Assert.AreEqual(0, c.Left - b.Right, "The number and the bolt touch" + where);
                        break;
                    case 2:
                        Assert.AreEqual(0, b.Left - a.Right, "The number and the bolt touch" + where);
                        Assert.AreEqual(gap, c.Left - b.Right, "Ring last: the whole gap before it" + where);
                        break;
                    default:
                        Assert.AreEqual(gap / 2, b.Left - a.Right, "Ring in the middle: half the gap before it, rounded down" + where);
                        Assert.AreEqual(gap - (gap / 2), c.Left - b.Right, "And the rest after it" + where);
                        break;
                }

                Assert.IsTrue(new Rectangle(0, 0, l.Width, l.Height).Contains(l.RingBox), "Ring inside the window" + where);
                Assert.IsTrue(l.RingBox.Contains(l.Mark), "The earbud mark stays inside its ring" + where);
                Assert.AreEqual(baseline.Mark.X - baseline.RingBox.X, l.Mark.X - l.RingBox.X, "The mark keeps its place in the ring" + where);
            }
        }
    }

    // Toward the ring when the number is next to it, otherwise toward the window's outer edge.
    [TestMethod]
    [DataRow(GaugeOrder.RingNumberBolt, false)]
    [DataRow(GaugeOrder.RingBoltNumber, true)]
    [DataRow(GaugeOrder.NumberRingBolt, true)]
    [DataRow(GaugeOrder.NumberBoltRing, false)]
    [DataRow(GaugeOrder.BoltRingNumber, false)]
    [DataRow(GaugeOrder.BoltNumberRing, true)]
    public void TheNumberSitsTowardTheRingWhenNextToItAndTowardTheOuterEdgeOtherwise(GaugeOrder order, bool alignRight)
    {
        foreach (int dpi in Dpis)
        {
            Assert.AreEqual(alignRight, GaugeLayout.For(dpi, order).NumberAlignRight, order + " at " + dpi + " dpi");
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
