using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every coordinate of the ring-and-number gauge at the three scales the design states, and the rule for any other scale.
// The figures are the design's own: 74 by 40 at 100%, ring 24, number slot 20, bolt slot 12, gap 4, round half away
// from zero, left padding floor((width - content) / 2). Pure: no drawing.
[TestClass]
public sealed class GaugeLayoutTests
{
    [TestMethod]
    public void At100PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(96, width: 74, height: 40, leftPad: 5, ring: 24, number: 20, bolt: 12, gap: 4, stroke: 2.5f, radius: 10.75f);

    [TestMethod]
    public void At125PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(120, width: 93, height: 50, leftPad: 6, ring: 30, number: 25, bolt: 15, gap: 5, stroke: 3.125f, radius: 13.4375f);

    [TestMethod]
    public void At150PercentEveryCoordinateMatchesTheDesignTable() =>
        AssertTable(144, width: 111, height: 60, leftPad: 7, ring: 36, number: 30, bolt: 18, gap: 6, stroke: 3.75f, radius: 16.125f);

    private static void AssertTable(int dpi, int width, int height, int leftPad, int ring, int number, int bolt, int gap, float stroke, float radius)
    {
        GaugeLayout l = GaugeLayout.For(dpi);

        Assert.AreEqual(new Size(width, height), new Size(l.Width, l.Height));
        Assert.AreEqual(new Size(width, height), GaugeLayout.SizeFor(dpi));
        Assert.AreEqual(leftPad, l.RingBox.X, "Left padding.");
        Assert.AreEqual(ring, l.RingBox.Width);
        Assert.AreEqual(ring, l.RingBox.Height);
        Assert.AreEqual((height - ring) / 2, l.RingBox.Y, "The ring is centred on the window's vertical midline.");
        Assert.AreEqual(stroke, l.RingStroke, 0.0001f, "Ring stroke 2.5 at 100%.");
        Assert.AreEqual(radius, l.RingRadius, 0.0001f, "The ring's centre line: 10.75 at 100%.");
        Assert.AreEqual(l.RingBox, l.Mark, "The mark's grid is the ring box.");
        Assert.AreEqual(leftPad + ring + gap, l.NumberSlot.X);
        Assert.AreEqual(number, l.NumberSlot.Width);
        Assert.AreEqual(leftPad + ring + gap + number + gap, l.ChargingSlot.X);
        Assert.AreEqual(bolt, l.ChargingSlot.Width);
        Assert.AreEqual(new Size(bolt, bolt), l.Bolt);
        Assert.AreEqual((height - bolt) / 2, l.ChargingSlot.Y, "Vertically centred.");
        Assert.AreEqual(gap, l.NumberSlot.X - l.RingBox.Right);
        Assert.AreEqual(gap, l.ChargingSlot.X - l.NumberSlot.Right);
    }

    // The design's earbud pair on the ring box's 24 unit grid: heads 5 by 5 at (6.5, 6) and (12.5, 6), stems 2 by 8 at (9.5, 9)
    // and (12.5, 9), radius 1; at 150% every figure is 1.5 times.
    [TestMethod]
    public void TheEarbudPairIsTheDesignsShapesScaledToTheRingBox()
    {
        (RectangleF[] shapes, float radius) = GaugeLayout.For(96).EarbudShapes();
        Rectangle box = GaugeLayout.For(96).RingBox;
        Assert.AreEqual(new RectangleF(box.X + 6.5f, box.Y + 6f, 5f, 5f), shapes[0]);
        Assert.AreEqual(new RectangleF(box.X + 9.5f, box.Y + 9f, 2f, 8f), shapes[1]);
        Assert.AreEqual(new RectangleF(box.X + 12.5f, box.Y + 6f, 5f, 5f), shapes[2]);
        Assert.AreEqual(new RectangleF(box.X + 12.5f, box.Y + 9f, 2f, 8f), shapes[3]);
        Assert.AreEqual(1f, radius);

        (RectangleF[] big, float bigRadius) = GaugeLayout.For(144).EarbudShapes();
        Rectangle bigBox = GaugeLayout.For(144).RingBox;
        Assert.AreEqual(bigBox.X + (6.5f * 1.5f), big[0].X, 0.0001f);
        Assert.AreEqual(7.5f, big[0].Width, 0.0001f);
        Assert.AreEqual(12f, big[1].Height, 0.0001f);
        Assert.AreEqual(1.5f, bigRadius, 0.0001f);
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

    // Any other scale follows the same rule: every figure is the 100% figure scaled and rounded half away from zero, the pieces
    // run left to right with one gap between neighbours and the window holds them all, the left padding being half of what the
    // content leaves, rounded down.
    [TestMethod]
    [DataRow(110)]
    [DataRow(132)]
    [DataRow(168)]
    [DataRow(192)]
    [DataRow(240)]
    public void AnotherScaleFollowsTheRule(int dpi)
    {
        GaugeLayout l = GaugeLayout.For(dpi);
        double s = dpi / 96.0;
        int R(double at96) => (int)Math.Round(at96 * s, MidpointRounding.AwayFromZero);

        Assert.AreEqual(R(74), l.Width);
        Assert.AreEqual(R(40), l.Height);
        Assert.AreEqual(R(24), l.RingBox.Width);
        Assert.AreEqual(R(20), l.NumberSlot.Width);
        Assert.AreEqual(R(12), l.ChargingSlot.Width);
        int content = R(24) + R(20) + R(12) + (2 * R(4));
        Assert.AreEqual((R(74) - content) / 2, l.RingBox.X);
        Assert.AreEqual(R(4), l.NumberSlot.X - l.RingBox.Right);
        Assert.IsLessThanOrEqualTo(l.Width, l.ChargingSlot.Right);
        Assert.IsTrue(new Rectangle(0, 0, l.Width, l.Height).Contains(l.RingBox));
        Assert.IsGreaterThanOrEqualTo(1f, l.RingStroke);
    }

    [TestMethod]
    public void RoundingIsHalfAwayFromZeroSo9250SixPercentIs93()
    {
        // 74 * 1.25 = 92.5, which the design's table gives as 93, not the banker's 92.
        Assert.AreEqual(93, GaugeLayout.Round(74, 120));
        Assert.AreEqual(50, GaugeLayout.Round(40, 120));
        Assert.AreEqual(111, GaugeLayout.Round(74, 144));
    }
}
