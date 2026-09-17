using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase5.Desktops;

namespace Earshot.Tests.Phase5;

// Placement maths over synthetic desktops. A 300x80 card at 96 DPI keeps a 12 px margin.
[TestClass]
public sealed class CardPlacementTests
{
    private static readonly Size Card = new(300, 80);

    private static Rectangle PlaceAt96(CardAnchor anchor, PlacementScene scene, Size? card = null) =>
        PlaceAt(anchor, scene, 96, card ?? Card);

    private static Rectangle PlaceAt(CardAnchor anchor, PlacementScene scene, int dpi, Size card)
    {
        CardTarget target = CardPlacement.TargetFor(anchor, scene);
        Rectangle placed = CardPlacement.Place(anchor, scene, target, card, dpi);
        Assert.AreEqual(card, placed.Size, "Placement never resizes the card.");
        Assert.IsTrue(target.Area.Contains(placed), "The card stays inside the usable area: " + placed);
        if (!target.TaskbarBand.IsEmpty)
        {
            Assert.IsFalse(placed.IntersectsWith(target.TaskbarBand), "The card never covers the taskbar: " + placed);
        }

        return placed;
    }

    [TestMethod]
    public void ABottomTaskbar()
    {
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, BottomTaskbar());
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge);
        Assert.AreEqual(Rectangle.FromLTRB(0, 1032, 1920, 1080), target.TaskbarBand);
        Assert.AreEqual(new Rectangle(0, 0, 1920, 1032), target.Area);

        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, BottomTaskbar()));

        // A click on the notification area: above the taskbar, pushed in from the right edge.
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, BottomTaskbar(new Point(1800, 1056))));

        // A click in the middle of the taskbar: centred on the click, above the taskbar.
        Assert.AreEqual(new Rectangle(810, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, BottomTaskbar(new Point(960, 1056))));
    }

    [TestMethod]
    public void ATopTaskbar()
    {
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, TopTaskbar());
        Assert.AreEqual(TaskbarEdge.Top, target.Edge);
        Assert.AreEqual(Rectangle.FromLTRB(0, 48, 1920, 1080), target.Area);

        Assert.AreEqual(new Rectangle(1608, 60, 300, 80), PlaceAt96(CardAnchor.NearTray, TopTaskbar()));
        Assert.AreEqual(new Rectangle(1608, 60, 300, 80), PlaceAt96(CardAnchor.NearCursor, TopTaskbar(new Point(1800, 24))));
        Assert.AreEqual(new Rectangle(810, 60, 300, 80), PlaceAt96(CardAnchor.NearCursor, TopTaskbar(new Point(960, 24))));
    }

    [TestMethod]
    public void ALeftTaskbar()
    {
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, LeftTaskbar());
        Assert.AreEqual(TaskbarEdge.Left, target.Edge);

        // The notification area is at the bottom of a vertical taskbar.
        Assert.AreEqual(new Rectangle(74, 988, 300, 80), PlaceAt96(CardAnchor.NearTray, LeftTaskbar()));

        // Beside the taskbar, vertically centred on the click.
        Assert.AreEqual(new Rectangle(74, 960, 300, 80), PlaceAt96(CardAnchor.NearCursor, LeftTaskbar(new Point(31, 1000))));
    }

    [TestMethod]
    public void ARightTaskbar()
    {
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, RightTaskbar());
        Assert.AreEqual(TaskbarEdge.Right, target.Edge);

        Assert.AreEqual(new Rectangle(1546, 988, 300, 80), PlaceAt96(CardAnchor.NearTray, RightTaskbar()));
        Assert.AreEqual(new Rectangle(1546, 960, 300, 80), PlaceAt96(CardAnchor.NearCursor, RightTaskbar(new Point(1889, 1000))));
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarIsKeptClearEvenThoughTheWorkAreaCoversIt()
    {
        // Hidden: the reported rectangle lies almost entirely below the display; the work area is the whole display.
        var hidden = new PlacementScene(
            new Point(1800, 1079),
            [new DisplayArea(Primary, Primary, IsPrimary: true)],
            Taskbar: Rectangle.FromLTRB(0, 1078, 1920, 1126),
            TaskbarAutoHide: true);
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, hidden);
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge);
        Assert.AreEqual(new Rectangle(0, 0, 1920, 1032), target.Area);
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, hidden));
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, hidden));

        // Revealed: the taskbar is on the display, the work area still covers it.
        PlacementScene revealed = hidden with { Cursor = new Point(1800, 1056), Taskbar = Rectangle.FromLTRB(0, 1032, 1920, 1080) };
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, revealed));

        // Hidden at the top.
        PlacementScene top = hidden with { Cursor = new Point(1800, 1), Taskbar = Rectangle.FromLTRB(0, -46, 1920, 2) };
        Assert.AreEqual(TaskbarEdge.Top, CardPlacement.TargetFor(CardAnchor.NearTray, top).Edge);
        Assert.AreEqual(new Rectangle(1608, 60, 300, 80), PlaceAt96(CardAnchor.NearTray, top));
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarAlsoReservesItsEdgeOnAnotherDisplay()
    {
        var scene = new PlacementScene(
            new Point(3000, 1070),
            [new DisplayArea(Primary, Primary, IsPrimary: true), new DisplayArea(Secondary, Secondary, IsPrimary: false)],
            Taskbar: Rectangle.FromLTRB(0, 1078, 1920, 1126),
            TaskbarAutoHide: true);

        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearCursor, scene);
        Assert.AreEqual(Secondary, target.Display.Bounds);
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge);
        Assert.AreEqual(new Rectangle(2850, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, scene));

        // Without auto-hide nothing is reserved on a display that has no taskbar of its own.
        PlacementScene shown = scene with { Taskbar = Rectangle.FromLTRB(0, 1032, 1920, 1080), TaskbarAutoHide = false };
        CardTarget plain = CardPlacement.TargetFor(CardAnchor.NearCursor, shown);
        Assert.IsTrue(plain.TaskbarBand.IsEmpty);
        Assert.AreEqual(new Rectangle(2850, 988, 300, 80), PlaceAt96(CardAnchor.NearCursor, shown));
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarOnTheEdgeSharedWithADisplayBelowStaysOnThePrimaryDisplay()
    {
        var below = new Rectangle(0, 1080, 1920, 1080);
        DisplayArea[] displays = [new(Primary, Primary, IsPrimary: true), new(below, below, IsPrimary: false)];

        // Whether the rectangle is where the taskbar slid to (mostly on the display below) or where it shows,
        // the card goes to the bottom right of the primary display.
        foreach (Rectangle taskbar in new[] { Rectangle.FromLTRB(0, 1078, 1920, 1126), Rectangle.FromLTRB(0, 1032, 1920, 1080) })
        {
            var scene = new PlacementScene(new Point(1800, 1079), displays, taskbar, TaskbarAutoHide: true);
            CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
            Assert.AreEqual(Primary, target.Display.Bounds, "Taskbar " + taskbar);
            Assert.AreEqual(TaskbarEdge.Bottom, target.Edge, "Taskbar " + taskbar);
            Assert.AreEqual(Rectangle.FromLTRB(0, 1032, 1920, 1080), target.TaskbarBand);
            Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, scene));
            Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, scene));
        }

        // The same hidden rectangle without auto-hide goes by overlap, as a shown taskbar does.
        Assert.AreEqual(1, CardPlacement.TaskbarDisplayFor(Rectangle.FromLTRB(0, 1078, 1920, 1126), autoHide: false, displays));
        Assert.AreEqual(0, CardPlacement.TaskbarDisplayFor(Rectangle.FromLTRB(0, 1078, 1920, 1126), autoHide: true, displays));
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarOnTheEdgeSharedWithADisplayAboveStaysOnThePrimaryDisplay()
    {
        var above = new Rectangle(0, -1080, 1920, 1080);
        var scene = new PlacementScene(
            new Point(1800, 0),
            [new DisplayArea(above, above, IsPrimary: false), new DisplayArea(Primary, Primary, IsPrimary: true)],
            Taskbar: Rectangle.FromLTRB(0, -46, 1920, 2),
            TaskbarAutoHide: true);

        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
        Assert.AreEqual(Primary, target.Display.Bounds);
        Assert.AreEqual(TaskbarEdge.Top, target.Edge);
        Assert.AreEqual(new Rectangle(1608, 60, 300, 80), PlaceAt96(CardAnchor.NearTray, scene));
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarAwayFromThePrimaryDisplayGoesByOverlap()
    {
        var scene = new PlacementScene(
            new Point(10, 10),
            [new DisplayArea(Primary, Primary, IsPrimary: true), new DisplayArea(Secondary, Secondary, IsPrimary: false)],
            Taskbar: Rectangle.FromLTRB(1920, 1078, 3840, 1126),
            TaskbarAutoHide: true);

        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
        Assert.AreEqual(Secondary, target.Display.Bounds);
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge);
        Assert.AreEqual(new Rectangle(3528, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, scene));
    }

    [TestMethod]
    public void APointIsOnTheTaskbarOnlyInsideTheTaskbarBandOfItsDisplay()
    {
        Assert.IsTrue(CardPlacement.IsOnTaskbar(BottomTaskbar(), new Point(1800, 1056)));
        Assert.IsTrue(CardPlacement.IsOnTaskbar(BottomTaskbar(), new Point(0, 1079)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(BottomTaskbar(), new Point(1800, 1031)), "A menu just above the taskbar is not on it.");
        Assert.IsFalse(CardPlacement.IsOnTaskbar(BottomTaskbar(), new Point(750, 0)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(BottomTaskbar(), new Point(1800, 1080)), "Off every display.");
        Assert.IsTrue(CardPlacement.IsOnTaskbar(TopTaskbar(), new Point(750, 0)));
        Assert.IsTrue(CardPlacement.IsOnTaskbar(LeftTaskbar(), new Point(31, 500)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(LeftTaskbar(), new Point(62, 500)));
        Assert.IsTrue(CardPlacement.IsOnTaskbar(RightTaskbar(), new Point(1858, 500)));

        // A secondary display's own taskbar, known from its work area.
        Assert.IsTrue(CardPlacement.IsOnTaskbar(TwoDisplays(Point.Empty), new Point(3700, 1056)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(TwoDisplays(Point.Empty), new Point(3700, 1000)));

        // An auto-hidden taskbar keeps its band even though the work area covers it.
        var hidden = new PlacementScene(Point.Empty, [new DisplayArea(Primary, Primary, IsPrimary: true)], Rectangle.FromLTRB(0, 1078, 1920, 1126), TaskbarAutoHide: true);
        Assert.IsTrue(CardPlacement.IsOnTaskbar(hidden, new Point(1800, 1079)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(hidden, new Point(1800, 1000)));

        // No taskbar anywhere.
        var bare = new PlacementScene(Point.Empty, [new DisplayArea(Primary, Primary, IsPrimary: true)], null, false);
        Assert.IsFalse(CardPlacement.IsOnTaskbar(bare, new Point(1800, 1079)));
        Assert.IsFalse(CardPlacement.IsOnTaskbar(new PlacementScene(Point.Empty, [], null, false), Point.Empty));
    }

    [TestMethod]
    public void AClickOnASecondaryDisplayPlacesTheCardOnThatDisplay()
    {
        PlacementScene scene = TwoDisplays(new Point(3700, 1056));

        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearCursor, scene);
        Assert.AreEqual(Secondary, target.Display.Bounds);
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge, "Taken from the secondary display's inset work area.");
        Rectangle placed = PlaceAt96(CardAnchor.NearCursor, scene);
        Assert.AreEqual(new Rectangle(3528, 940, 300, 80), placed);
        Assert.IsTrue(Secondary.Contains(placed));

        // A card nobody clicked for goes to the display with the notification area.
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, scene));
    }

    [TestMethod]
    public void ASecondaryDisplayLeftOfThePrimaryHasNegativeCoordinates()
    {
        var left = new Rectangle(-1920, 0, 1920, 1080);
        var scene = new PlacementScene(
            new Point(-100, 1056),
            [
                new DisplayArea(Primary, new Rectangle(0, 0, 1920, 1032), IsPrimary: true),
                new DisplayArea(left, Rectangle.FromLTRB(-1920, 0, 0, 1032), IsPrimary: false),
            ],
            Taskbar: Rectangle.FromLTRB(0, 1032, 1920, 1080),
            TaskbarAutoHide: false);

        Assert.AreEqual(new Rectangle(-312, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, scene));
    }

    [TestMethod]
    public void ASecondaryDisplayAtAHigherDpiGetsTheScaledMargin()
    {
        PlacementScene scene = TwoDisplays(new Point(3700, 1056));
        var card = new Size(450, 120);

        Rectangle placed = PlaceAt(CardAnchor.NearCursor, scene, 144, card);

        Assert.AreEqual(new Rectangle(3372, 894, 450, 120), placed);
        Assert.AreEqual(3840 - 18, placed.Right, "18 px is 12 px at 150 %.");
        Assert.AreEqual(1032 - 18, placed.Bottom);
    }

    [TestMethod]
    public void TheCardIsClampedIntoTheWorkArea()
    {
        // Clicked at the far left: pushed right to the margin.
        Assert.AreEqual(new Rectangle(12, 940, 300, 80), PlaceAt96(CardAnchor.NearCursor, BottomTaskbar(new Point(5, 1056))));

        // Clicked at the top of a left taskbar: pushed down to the margin.
        Assert.AreEqual(new Rectangle(74, 12, 300, 80), PlaceAt96(CardAnchor.NearCursor, LeftTaskbar(new Point(31, 10))));

        // A card wider or taller than the area is aligned to its left or top.
        var area = new Rectangle(0, 0, 1920, 1032);
        Assert.AreEqual(new Rectangle(0, 0, 3000, 2000), CardPlacement.Clamp(new Rectangle(500, 500, 3000, 2000), area));

        // An area too short for the margin keeps its size along that axis.
        Assert.AreEqual(new Rectangle(0, 12, 20, 976), CardPlacement.Deflate(new Rectangle(0, 0, 20, 1000), 12));
    }

    [TestMethod]
    [DataRow(96, 12)]
    [DataRow(120, 15)]
    [DataRow(144, 18)]
    [DataRow(192, 24)]
    [DataRow(0, 12)]
    public void TheMarginScalesWithDpi(int dpi, int expected)
    {
        Assert.AreEqual(expected, CardPlacement.Scale(CardPlacement.MarginAt96, dpi));
    }

    [TestMethod]
    public void NearTrayAt150PercentUsesTheScaledSizeAndMargin()
    {
        Assert.AreEqual(new Rectangle(1452, 894, 450, 120), PlaceAt(CardAnchor.NearTray, BottomTaskbar(), 144, new Size(450, 120)));

        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, BottomTaskbar());
        Assert.AreEqual(1920 - 36, CardPlacement.AvailableWidth(target, 144));
    }

    [TestMethod]
    public void WithoutTheTaskbarRectangleTheWorkAreaInsetGivesTheEdge()
    {
        PlacementScene scene = BottomTaskbar() with { Taskbar = null };
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
        Assert.AreEqual(TaskbarEdge.Bottom, target.Edge);
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), PlaceAt96(CardAnchor.NearTray, scene));

        // Nothing to go on at all: the bottom right corner of the display.
        var bare = new PlacementScene(new Point(10, 10), [new DisplayArea(Primary, Primary, IsPrimary: true)], null, false);
        Assert.AreEqual(new Rectangle(1608, 988, 300, 80), PlaceAt96(CardAnchor.NearTray, bare));
    }

    [TestMethod]
    [DataRow(0, 1032, 1920, 1080, "Bottom")]
    [DataRow(0, 0, 1920, 48, "Top")]
    [DataRow(0, 0, 62, 1080, "Left")]
    [DataRow(1858, 0, 1920, 1080, "Right")]
    [DataRow(0, 1078, 1920, 1126, "Bottom")]
    [DataRow(0, -46, 1920, 2, "Top")]
    [DataRow(-60, 0, 2, 1080, "Left")]
    [DataRow(1918, 0, 1980, 1080, "Right")]
    public void TheEdgeComesFromGeometry(int left, int top, int right, int bottom, string expected)
    {
        Assert.AreEqual(Enum.Parse<TaskbarEdge>(expected), CardPlacement.EdgeOf(Rectangle.FromLTRB(left, top, right, bottom), Primary));
    }

    [TestMethod]
    public void TheEdgeOnAnOffsetDisplayIsMeasuredAgainstThatDisplay()
    {
        Assert.AreEqual(TaskbarEdge.Bottom, CardPlacement.EdgeOf(Rectangle.FromLTRB(1920, 1032, 3840, 1080), Secondary));
        Assert.AreEqual(TaskbarEdge.Left, CardPlacement.EdgeOf(Rectangle.FromLTRB(1920, 0, 1982, 1080), Secondary));
    }

    [TestMethod]
    public void TheDisplayIsChosenByOverlapOrDistance()
    {
        DisplayArea[] displays = [new(Primary, Primary, true), new(Secondary, Secondary, false)];

        Assert.AreEqual(1, CardPlacement.DisplayFor(new Point(1920, 0), displays));
        Assert.AreEqual(0, CardPlacement.DisplayFor(new Point(1919, 1079), displays));
        Assert.AreEqual(1, CardPlacement.DisplayFor(new Point(5000, 500), displays), "Off every display: the nearest.");
        Assert.AreEqual(0, CardPlacement.DisplayFor(new Point(-10, -10), displays));

        Assert.AreEqual(1, CardPlacement.DisplayFor(Rectangle.FromLTRB(1800, 0, 2500, 48), displays), "Most of it is on the secondary display.");
        Assert.AreEqual(1, CardPlacement.DisplayFor(Rectangle.FromLTRB(4000, 0, 4100, 48), displays));
    }

    [TestMethod]
    public void TheCursorFlagsFollowTheTaskbarEdge()
    {
        Assert.AreEqual(Shell.TPM_CENTERALIGN | Shell.TPM_BOTTOMALIGN | Shell.TPM_VERTICAL, CardPlacement.CursorFlags(TaskbarEdge.Bottom));
        Assert.AreEqual(Shell.TPM_CENTERALIGN | Shell.TPM_TOPALIGN | Shell.TPM_VERTICAL, CardPlacement.CursorFlags(TaskbarEdge.Top));
        Assert.AreEqual(Shell.TPM_LEFTALIGN | Shell.TPM_VCENTERALIGN | Shell.TPM_HORIZONTAL, CardPlacement.CursorFlags(TaskbarEdge.Left));
        Assert.AreEqual(Shell.TPM_RIGHTALIGN | Shell.TPM_VCENTERALIGN | Shell.TPM_HORIZONTAL, CardPlacement.CursorFlags(TaskbarEdge.Right));
    }

    [TestMethod]
    public void APopupWithNoRoomOnThePreferredSideGoesToTheOtherSide()
    {
        // A bottom-aligned popup under a band at the very top of the area has no room above it.
        var area = new Rectangle(0, 0, 1000, 1000);
        var band = new Rectangle(0, 0, 1000, 48);
        Rectangle placed = CardPlacement.CalculatePopup(new Point(500, 40), Card, Shell.TPM_CENTERALIGN | Shell.TPM_BOTTOMALIGN | Shell.TPM_VERTICAL, band, area);
        Assert.AreEqual(new Rectangle(350, 48, 300, 80), placed);

        // A popup that does not touch the exclude rectangle stays where the alignment put it.
        Rectangle free = CardPlacement.CalculatePopup(new Point(500, 500), Card, Shell.TPM_CENTERALIGN | Shell.TPM_BOTTOMALIGN | Shell.TPM_VERTICAL, band, area);
        Assert.AreEqual(new Rectangle(350, 420, 300, 80), free);
    }

    // The Microsoft page does not say how CalculatePopupWindowPosition resolves an overlap with the exclude
    // rectangle, so the managed calculation is compared with the real one over the combinations Earshot uses:
    // each taskbar edge with that edge's band, on this machine's primary work area. The call is a pure
    // calculation and moves no window.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-calculatepopupwindowposition
    [TestMethod]
    public void TheManagedPopupMatchesTheRealCalculationForEveryTaskbarEdge()
    {
        if (Screen.PrimaryScreen is not { } screen)
        {
            Assert.Inconclusive("This session has no display to read a work area from.");
            return;
        }

        Rectangle work = screen.WorkingArea;
        const int Thickness = 48;
        foreach (TaskbarEdge edge in new[] { TaskbarEdge.Bottom, TaskbarEdge.Top, TaskbarEdge.Left, TaskbarEdge.Right })
        {
            Rectangle band = edge switch
            {
                TaskbarEdge.Bottom => Rectangle.FromLTRB(work.Left, work.Bottom - Thickness, work.Right, work.Bottom),
                TaskbarEdge.Top => Rectangle.FromLTRB(work.Left, work.Top, work.Right, work.Top + Thickness),
                TaskbarEdge.Left => Rectangle.FromLTRB(work.Left, work.Top, work.Left + Thickness, work.Bottom),
                _ => Rectangle.FromLTRB(work.Right - Thickness, work.Top, work.Right, work.Bottom),
            };
            uint flags = CardPlacement.CursorFlags(edge);
            foreach (Point anchor in Anchors(work, band))
            {
                Rectangle managed = CardPlacement.CalculatePopup(anchor, Card, flags, band, work);
                Assert.AreEqual(Real(anchor, Card, flags, band), managed, edge + " at " + anchor);
            }
        }
    }

    private static IEnumerable<Point> Anchors(Rectangle work, Rectangle band)
    {
        yield return new Point(band.Left + (band.Width / 2), band.Top + (band.Height / 2));
        yield return new Point(work.Left + 5, work.Top + 5);
        yield return new Point(work.Right - 5, work.Bottom - 5);
        yield return new Point(work.Left + (work.Width / 2), work.Top + (work.Height / 2));
    }

    private static Rectangle Real(Point anchor, Size card, uint flags, Rectangle exclude)
    {
        var point = new POINT { x = anchor.X, y = anchor.Y };
        var size = new SIZE { cx = card.Width, cy = card.Height };
        var excluded = new RECT { left = exclude.Left, top = exclude.Top, right = exclude.Right, bottom = exclude.Bottom };
        Assert.IsTrue(
            Shell.CalculatePopupWindowPosition(point, size, flags | Shell.TPM_WORKAREA, excluded, out RECT placed),
            "CalculatePopupWindowPosition failed.");
        return Rectangle.FromLTRB(placed.left, placed.top, placed.right, placed.bottom);
    }

    [TestMethod]
    public void ASceneWithNoDisplayIsRejected()
    {
        var empty = new PlacementScene(Point.Empty, [], null, false);
        Assert.ThrowsExactly<ArgumentException>(() => CardPlacement.TargetFor(CardAnchor.NearCursor, empty));
    }
}
