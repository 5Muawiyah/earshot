using System.Drawing;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// GaugePlacement pure maths. Fixtures mirror a real Windows 11 taskbar layout: Start 762..807, task
// buttons 44 px wide from 807, the notification area from 1678, bar 0,1032,1920,48 at 96 DPI.
[TestClass]
public sealed class GaugePlacementTests
{
    private static readonly Rectangle Bar96 = new(0, 1032, 1920, 48);
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static List<Rectangle> Buttons(int count, int startX = 807, int width = 44) =>
        Enumerable.Range(0, count).Select(i => new Rectangle(startX + (i * width), 1032, width, 48)).ToList();

    private static TaskbarLayout Layout(Rectangle bar, IReadOnlyList<Rectangle> occupied, Rectangle? start, int dpi = 96, bool autoHide = false, TaskbarEdge edge = TaskbarEdge.Bottom, Rectangle? monitor = null) =>
        new(0, bar, edge, autoHide, monitor ?? Monitor, occupied, start, dpi, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null);

    [TestMethod]
    public void TheMachineLayoutPlacesTheGaugeRightOfTheLastButton()
    {
        List<Rectangle> occupied = [Start, .. Buttons(8), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(Bar96, occupied, Start);
        Rectangle? placed = GaugePlacement.Place(layout, GaugeRenderer.WidthFor(96));
        Assert.AreEqual(new Rectangle(1183, 1032, GaugeRenderer.WidthFor(96), 48), placed);
    }

    [TestMethod]
    public void ALeftAlignedTaskbarPlacesTheGaugeAfterTheLastButton()
    {
        Rectangle start = new(0, 1032, 45, 48);
        List<Rectangle> occupied = [start, .. Buttons(8, startX: 45), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(Bar96, occupied, start);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        Assert.AreEqual(45 + (8 * 44) + 24, placed!.Value.X);
    }

    [TestMethod]
    public void NoFreeSpaceHidesTheGauge()
    {
        // Buttons run all the way to 1580, tray starts at 1678: the 98 px gap is narrower than 24+88+24.
        List<Rectangle> occupied = [Start, .. Buttons(count: (1580 - 807) / 44), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(Bar96, occupied, Start);
        Assert.IsNull(GaugePlacement.Place(layout, 88));
    }

    [TestMethod]
    public void MissingStartUsesTheFirstIntervalsEnd()
    {
        List<Rectangle> occupied = [.. Buttons(8), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(Bar96, occupied, start: null);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        Assert.AreEqual(807 + (8 * 44) + 24, placed!.Value.X);
    }

    [TestMethod]
    public void NoOccupantsAtAllStartsAfterTheEdgeMargin()
    {
        TaskbarLayout layout = Layout(Bar96, [], start: null);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.AreEqual(0 + 8 + 24, placed!.Value.X);
    }

    [TestMethod]
    public void ResultNeverIntersectsAnOccupant()
    {
        List<Rectangle> occupied = [Start, .. Buttons(8), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(Bar96, occupied, Start);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        foreach (Rectangle o in occupied)
        {
            Assert.IsFalse(placed!.Value.IntersectsWith(o), "The gauge must never cover an occupant.");
        }
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    public void HigherDpiScalesTheClearanceAndTheGaugeSize(int dpi)
    {
        int scale(int at96) => CardPlacement.Scale(at96, dpi);
        int barHeight = scale(48);
        Rectangle bar = new(0, 1032 - (barHeight - 48), scale(1920), barHeight);
        Rectangle start = new(scale(762), bar.Y, scale(45), barHeight);
        List<Rectangle> occupied = [start, .. Buttons(8, startX: scale(807), width: scale(44)).Select(r => new Rectangle(r.X, bar.Y, r.Width, barHeight)), new Rectangle(scale(1678), bar.Y, scale(242), barHeight)];
        TaskbarLayout layout = Layout(bar, occupied, start, dpi, monitor: new Rectangle(0, 0, scale(1920), 1080));
        int gaugeLong = GaugeRenderer.WidthFor(dpi);
        Rectangle? placed = GaugePlacement.Place(layout, gaugeLong);
        Assert.IsNotNull(placed);
        Assert.AreEqual(scale(1159) + scale(24), placed!.Value.X);
        Assert.AreEqual(gaugeLong, placed.Value.Width);
        Assert.AreEqual(bar.Height, placed.Value.Height);
    }

    [TestMethod]
    public void AVerticalLeftTaskbarPlacesAlongY()
    {
        Rectangle bar = new(0, 0, 62, 1080);
        Rectangle start = new(0, 0, 62, 45);
        Rectangle button = new(0, 45, 62, 300);
        Rectangle tray = new(0, 900, 62, 180);
        TaskbarLayout layout = Layout(bar, [start, button, tray], start, edge: TaskbarEdge.Left, monitor: new Rectangle(0, 0, 1920, 1080));
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        Assert.AreEqual(345 + 24, placed!.Value.Y);
        Assert.AreEqual(bar.Width, placed.Value.Width);
        Assert.AreEqual(88, placed.Value.Height);
    }

    [TestMethod]
    public void ATopTaskbarPlacesAlongX()
    {
        Rectangle bar = new(0, 0, 1920, 48);
        Rectangle start = new(762, 0, 45, 48);
        List<Rectangle> occupied = [start, .. Buttons(8).Select(r => new Rectangle(r.X, 0, r.Width, 48))];
        TaskbarLayout layout = Layout(bar, occupied, start, edge: TaskbarEdge.Top);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        Assert.AreEqual(1159 + 24, placed!.Value.X);
    }

    [TestMethod]
    public void AutoHiddenTaskbarSlidAwayHidesTheGauge()
    {
        // The reported rectangle lies almost entirely below the display, the way ABM_GETTASKBARPOS
        // reports an auto-hidden taskbar (CardPlacement's own comment on the same undocumented shape).
        Rectangle bar = Rectangle.FromLTRB(0, 1078, 1920, 1126);
        TaskbarLayout layout = Layout(bar, [], null, autoHide: true);
        Assert.IsNull(GaugePlacement.Place(layout, 88));
    }

    [TestMethod]
    public void AutoHiddenTaskbarSlidInPlacesNormally()
    {
        Rectangle bar = Bar96;
        List<Rectangle> occupied = [Start, .. Buttons(8), new Rectangle(1678, 1032, 242, 48)];
        TaskbarLayout layout = Layout(bar, occupied, Start, autoHide: true);
        Rectangle? placed = GaugePlacement.Place(layout, 88);
        Assert.IsNotNull(placed);
        Assert.AreEqual(1183, placed!.Value.X);
    }

    // Property test: over many random layouts, the result never intersects an occupant and never
    // leaves the taskbar rectangle. house rule: a static scan proves nothing; this runs the real
    // algorithm against generated input.
    [TestMethod]
    public void PropertyResultNeverIntersectsOrLeavesTheTaskbar()
    {
        var random = new Random(20260927);
        for (int trial = 0; trial < 500; trial++)
        {
            int barWidth = random.Next(400, 3000);
            Rectangle bar = new(0, 1032, barWidth, 48);
            var occupied = new List<Rectangle>();
            int cursor = random.Next(0, 60);
            int count = random.Next(0, 12);
            Rectangle? start = null;
            for (int i = 0; i < count && cursor < barWidth - 20; i++)
            {
                int width = random.Next(20, 60);
                var rect = new Rectangle(cursor, 1032, width, 48);
                occupied.Add(rect);
                if (i == 0 && random.Next(2) == 0)
                {
                    start = rect;
                }

                cursor += width + random.Next(0, 3);
            }

            TaskbarLayout layout = Layout(bar, occupied, start);
            Rectangle? placed = GaugePlacement.Place(layout, 88);
            if (placed is not { } rect2)
            {
                continue;
            }

            Assert.IsTrue(bar.Contains(rect2), "trial " + trial + ": result must stay inside the taskbar.");
            foreach (Rectangle o in occupied)
            {
                Assert.IsFalse(rect2.IntersectsWith(o), "trial " + trial + ": result must never intersect an occupant.");
            }
        }
    }
}
