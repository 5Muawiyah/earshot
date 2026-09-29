using System.Drawing;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// GaugePlacement over synthetic taskbars: pure rectangles in, a rectangle or a reason out. No window, no
// taskbar read.
[TestClass]
public sealed class GaugePlacementTests
{
    private static readonly Rectangle Screen1080 = new(0, 0, 1920, 1080);

    // A bottom taskbar of the given thickness on the 1920 x 1080 screen, with the Start button and
    // appButtons task buttons starting at appsLeft (each buttonWidth wide), and a notification area whose
    // left edge is trayLeft.
    private static TaskbarLayout Layout(
        int dpi, int thickness, int appsLeft, int appButtons, int buttonWidth, int? trayLeft, bool chevron = true,
        TaskbarEdge edge = TaskbarEdge.Bottom, bool autoHide = false, int? taskbarTop = null, IEnumerable<Rectangle>? extra = null)
    {
        int top = taskbarTop ?? (1080 - thickness);
        var bar = new Rectangle(0, top, 1920, thickness);
        var occupied = new List<Rectangle>();
        var start = new Rectangle(appsLeft, top, buttonWidth, thickness);
        occupied.Add(start);
        for (int i = 0; i < appButtons; i++)
        {
            occupied.Add(new Rectangle(appsLeft + ((i + 1) * buttonWidth), top, buttonWidth, thickness));
        }

        Rectangle? area = null;
        if (trayLeft is { } tl)
        {
            // The notification area's own buttons, each a little apart, the way the shell lays them out.
            int x = tl;
            int step = 4 * thickness / 48;
            if (chevron)
            {
                occupied.Add(new Rectangle(x, top, 32 * thickness / 48, thickness));
                x += (32 * thickness / 48) + step;
            }

            occupied.Add(new Rectangle(x, top, 24 * thickness / 48, thickness));
            occupied.Add(new Rectangle(x + (28 * thickness / 48), top, 70 * thickness / 48, thickness));
            occupied.Add(new Rectangle(1920 - (12 * thickness / 48), top, 12 * thickness / 48, thickness));
            area = Rectangle.FromLTRB(tl, top, 1920, top + thickness);
        }

        if (extra is not null)
        {
            occupied.AddRange(extra);
        }

        return new TaskbarLayout(
            0, bar, edge, autoHide, Screen1080, occupied, start, dpi, Shell.QUNS_ACCEPTS_NOTIFICATIONS,
            Covered: false, GaugeCentreIsGauge: null, NotificationArea: area);
    }

    private static Rectangle PlacedRect(PlacementResult result)
    {
        Assert.IsNotNull(result.Bounds, "Expected a placement, got " + result.Failure);
        Assert.AreEqual(PlacementFailure.None, result.Failure);
        return result.Bounds.Value;
    }

    // ---- Right end (the default) ----

    [TestMethod]
    public void TheRightEndPutsTheGaugeEightPixelsLeftOfTheChevron()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738);

        Rectangle r = PlacedRect(GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd));

        Assert.AreEqual(new Rectangle(1738 - 8 - 74, 1032 + 4, 74, 40), r);
    }

    [TestMethod]
    public void WithoutTheChevronTheGaugeSitsBesideTheFirstTrayIcon()
    {
        TaskbarLayout withChevron = Layout(96, 48, 806, 6, 44, trayLeft: 1738, chevron: true);
        TaskbarLayout without = Layout(96, 48, 806, 6, 44, trayLeft: 1770, chevron: false);

        Rectangle a = PlacedRect(GaugePlacement.Place(withChevron, GaugeLayout.For(96), GaugePosition.RightEnd));
        Rectangle b = PlacedRect(GaugePlacement.Place(without, GaugeLayout.For(96), GaugePosition.RightEnd));

        Assert.AreEqual(1738 - 8 - 74, a.Left);
        Assert.AreEqual(1770 - 8 - 74, b.Left);
        Assert.AreEqual(8, 1770 - b.Right, "Eight pixels between the gauge and the first tray icon.");
    }

    [TestMethod]
    public void ANotificationAreaThatGrowsMovesTheGaugeLeftWithIt()
    {
        Rectangle before = PlacedRect(GaugePlacement.Place(Layout(96, 48, 806, 6, 44, trayLeft: 1738), GaugeLayout.For(96), GaugePosition.RightEnd));
        Rectangle after = PlacedRect(GaugePlacement.Place(Layout(96, 48, 806, 6, 44, trayLeft: 1690), GaugeLayout.For(96), GaugePosition.RightEnd));

        Assert.AreEqual(before.Left - 48, after.Left);
        Assert.AreEqual(1690 - 8, after.Right);
    }

    [TestMethod]
    public void TaskButtonsThatGrowIntoTheGaugeLeaveNoRoom()
    {
        // Eight buttons from 806 end at 806 + 9 x 44 = 1202; twelve more make them reach 1730, over the gauge.
        TaskbarLayout fits = Layout(96, 48, 806, 8, 44, trayLeft: 1738);
        TaskbarLayout grown = Layout(96, 48, 806, 21, 44, trayLeft: 1738);

        Assert.AreEqual(PlacementFailure.None, GaugePlacement.Place(fits, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
        PlacementResult result = GaugePlacement.Place(grown, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.IsNull(result.Bounds);
        Assert.AreEqual(PlacementFailure.NoRoom, result.Failure);
    }

    [TestMethod]
    public void ButtonsThatJustTouchTheGaugeAreOverlapAndButtonsOnePixelShortAreNot()
    {
        GaugeLayout gauge = GaugeLayout.For(96);
        int gaugeLeft = 1738 - 8 - 74; // 1656

        // The last button ends exactly at the gauge's left edge: no overlap.
        var touching = new Rectangle(gaugeLeft - 44, 1032, 44, 48);
        // One pixel further: overlap.
        var overlapping = new Rectangle(gaugeLeft - 43, 1032, 44, 48);

        Assert.AreEqual(PlacementFailure.None, GaugePlacement.Place(Layout(96, 48, 806, 0, 44, trayLeft: 1738, extra: [touching]), gauge, GaugePosition.RightEnd).Failure);
        Assert.AreEqual(PlacementFailure.NoRoom, GaugePlacement.Place(Layout(96, 48, 806, 0, 44, trayLeft: 1738, extra: [overlapping]), gauge, GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    public void WithNoNotificationAreaIdentifiedTheRightEndHasNothingToMeasureFrom()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: null);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.IsNull(result.Bounds);
        Assert.AreEqual(PlacementFailure.NoAnchor, result.Failure);
    }

    // ---- Next to apps ----

    [TestMethod]
    public void NextToAppsPutsTheGaugeFourPixelsAfterTheLastButton()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738);
        int lastRight = 806 + (7 * 44);

        Rectangle r = PlacedRect(GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.NextToApps));

        Assert.AreEqual(new Rectangle(lastRight + 4, 1036, 74, 40), r);
    }

    [TestMethod]
    public void NextToAppsFollowsAButtonBeingAddedAndRemoved()
    {
        Rectangle six = PlacedRect(GaugePlacement.Place(Layout(96, 48, 806, 6, 44, trayLeft: 1738), GaugeLayout.For(96), GaugePosition.NextToApps));
        Rectangle seven = PlacedRect(GaugePlacement.Place(Layout(96, 48, 806, 7, 44, trayLeft: 1738), GaugeLayout.For(96), GaugePosition.NextToApps));
        Rectangle five = PlacedRect(GaugePlacement.Place(Layout(96, 48, 806, 5, 44, trayLeft: 1738), GaugeLayout.For(96), GaugePosition.NextToApps));

        Assert.AreEqual(six.Left + 44, seven.Left);
        Assert.AreEqual(six.Left - 44, five.Left);
    }

    [TestMethod]
    public void NextToAppsWithNoRoomBeforeTheNotificationAreaHasNoPlacement()
    {
        // The last button ends at 1686; the gauge would run from 1690 to 1764, across the tray at 1738.
        TaskbarLayout layout = Layout(96, 48, 806, 19, 44, trayLeft: 1738);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.NextToApps);

        Assert.IsNull(result.Bounds);
        Assert.AreEqual(PlacementFailure.NoRoom, result.Failure);
    }

    [TestMethod]
    public void TheTwoPositionsGiveDifferentRectanglesThatDoNotOverlap()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738);

        Rectangle apps = PlacedRect(GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.NextToApps));
        Rectangle right = PlacedRect(GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd));

        Assert.IsLessThan(right.Left, apps.Left);
        Assert.IsFalse(apps.IntersectsWith(right));
    }

    // ---- Three scales ----

    [TestMethod]
    [DataRow(96, 48, 1738, 8, 4, 4)]
    [DataRow(120, 60, 1690, 10, 5, 5)]
    [DataRow(144, 72, 1650, 12, 6, 6)]
    public void BothPositionsAreRightAtEveryScale(int dpi, int thickness, int trayLeft, int gapToTray, int topOffset, int gapAfterApps)
    {
        GaugeLayout gauge = GaugeLayout.For(dpi);
        TaskbarLayout layout = Layout(dpi, thickness, 806, 6, thickness - 4, trayLeft);
        int top = 1080 - thickness;
        int lastRight = 806 + (7 * (thickness - 4));

        Rectangle right = PlacedRect(GaugePlacement.Place(layout, gauge, GaugePosition.RightEnd));
        Rectangle apps = PlacedRect(GaugePlacement.Place(layout, gauge, GaugePosition.NextToApps));

        Assert.AreEqual(new Rectangle(trayLeft - gapToTray - gauge.Width, top + topOffset, gauge.Width, gauge.Height), right);
        Assert.AreEqual(new Rectangle(lastRight + gapAfterApps, top + topOffset, gauge.Width, gauge.Height), apps);
    }

    // ---- Refusals ----

    [TestMethod]
    public void NoOccupantsMeansNothingToMeasureFrom()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 0, 44, trayLeft: 1738) with { Occupied = [] };

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.AreEqual(PlacementFailure.NoAnchor, result.Failure);
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarThatHasSlidAwayHasNoPlacementAndOneThatSlidInDoes()
    {
        // The bar's top is at the bottom of the screen with only 4 px showing.
        TaskbarLayout away = Layout(96, 48, 806, 6, 44, trayLeft: 1738, autoHide: true, taskbarTop: 1076);
        TaskbarLayout slidIn = Layout(96, 48, 806, 6, 44, trayLeft: 1738, autoHide: true);

        Assert.AreEqual(PlacementFailure.AutoHiddenAway, GaugePlacement.Place(away, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
        Assert.AreEqual(PlacementFailure.None, GaugePlacement.Place(slidIn, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AVerticalTaskbarIsNotSupported(bool onTheLeft)
    {
        TaskbarEdge edge = onTheLeft ? TaskbarEdge.Left : TaskbarEdge.Right;
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738, edge: edge);

        PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd);

        Assert.IsNull(result.Bounds);
        Assert.AreEqual(PlacementFailure.UnsupportedEdge, result.Failure);
    }

    [TestMethod]
    public void ATaskbarTooNarrowForTheGaugeHasNoPlacement()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738) with { Taskbar = new Rectangle(0, 1032, 60, 48) };

        Assert.AreEqual(PlacementFailure.OutsideTaskbar, GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd).Failure);
    }

    [TestMethod]
    public void ATopTaskbarPlacesTheGaugeOnItsOwnShortAxis()
    {
        TaskbarLayout layout = Layout(96, 48, 806, 6, 44, trayLeft: 1738, edge: TaskbarEdge.Top, taskbarTop: 0);

        Rectangle r = PlacedRect(GaugePlacement.Place(layout, GaugeLayout.For(96), GaugePosition.RightEnd));

        Assert.AreEqual(4, r.Top);
    }

    // The invariant over random layouts: a result never touches an occupant, never leaves the taskbar and
    // never crosses into the notification area.
    [TestMethod]
    public void ARandomLayoutNeverGivesARectangleThatTouchesAnythingOrLeavesTheTaskbar()
    {
        var random = new Random(20260929);
        int placed = 0;
        for (int i = 0; i < 400; i++)
        {
            int dpi = new[] { 96, 120, 144 }[random.Next(3)];
            int thickness = 48 * dpi / 96;
            int buttonWidth = thickness - 4;
            int trayLeft = random.Next(1200, 1800);
            TaskbarLayout layout = Layout(dpi, thickness, random.Next(0, 900), random.Next(0, 24), buttonWidth, trayLeft, chevron: random.Next(2) == 0);
            GaugePosition position = random.Next(2) == 0 ? GaugePosition.RightEnd : GaugePosition.NextToApps;

            PlacementResult result = GaugePlacement.Place(layout, GaugeLayout.For(dpi), position);

            if (result.Bounds is not { } rect)
            {
                Assert.AreNotEqual(PlacementFailure.None, result.Failure);
                continue;
            }

            placed++;
            Assert.IsTrue(layout.Taskbar.Contains(rect), "Left the taskbar: " + rect);
            Assert.IsFalse(layout.Occupied.Any(o => o.IntersectsWith(rect)), "Touches an occupant: " + rect);
            Assert.IsLessThanOrEqualTo(layout.NotificationArea!.Value.Left, rect.Right, "Crosses into the notification area: " + rect);
        }

        Assert.IsGreaterThan(100, placed, "The random layouts must place often enough for the invariant to mean something.");
    }

    [TestMethod]
    public void GapsAreScaledWithCardPlacementScale()
    {
        Assert.AreEqual(12, CardPlacement.Scale(GaugePlacement.GapToNotificationAreaAt96, 144));
        Assert.AreEqual(6, CardPlacement.Scale(GaugePlacement.GapAfterAppsAt96, 144));
    }
}
