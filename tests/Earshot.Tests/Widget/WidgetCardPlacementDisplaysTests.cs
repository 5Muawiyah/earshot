using System.Drawing;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A card opens on the display its gauge is on, in that display's corner, 12 px (scaled) from the screen edge and
// above the taskbar, as Windows' own flyouts sit.
[TestClass]
public sealed class WidgetCardPlacementDisplaysTests
{
    private static readonly Size Card = new(360, 300);

    // Two displays side by side: the main one on the left, the second on the right with its own taskbar thickness.
    private static (DisplayArea Main, DisplayArea Second) Displays(int dpi)
    {
        int taskbar = 48 * dpi / 96;
        var mainBounds = new Rectangle(0, 0, 1920, 1080);
        var secondBounds = new Rectangle(1920, 0, 2560, 1440);
        return (
            new DisplayArea(mainBounds, new Rectangle(0, 0, 1920, 1080 - taskbar), IsPrimary: true),
            new DisplayArea(secondBounds, new Rectangle(1920, 0, 2560, 1440 - taskbar), IsPrimary: false));
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void AGaugeOnTheSecondDisplayOpensItsCardOnTheSecondDisplay(int dpi)
    {
        (DisplayArea main, DisplayArea second) = Displays(dpi);
        int taskbar = 48 * dpi / 96;
        int gaugeHeight = 40 * dpi / 96;
        var gauge = new Rectangle(4300, 1440 - taskbar + ((taskbar - gaugeHeight) / 2), 74 * dpi / 96, gaugeHeight);
        DisplayArea[] displays = [main, second];

        Rectangle workArea = WidgetCardPlacement.WorkAreaFor(gauge, displays, fallback: new Rectangle(0, 0, 800, 600));
        Assert.AreEqual(second.WorkArea, workArea, "The gauge's own display.");

        Rectangle card = WidgetCardPlacement.Above(gauge, Card, workArea, dpi, GaugePosition.RightEnd);
        int gap = CardPlacement.Scale(12, dpi);
        Assert.AreEqual(second.Bounds.Right - gap, card.Right, "12 px (scaled) from the second display's right edge.");
        Assert.AreEqual(second.WorkArea.Bottom - gap, card.Bottom, "12 px (scaled) above the second display's taskbar.");
        Assert.IsTrue(second.WorkArea.Contains(card), "Wholly on the second display.");
        Assert.IsFalse(card.IntersectsWith(main.Bounds), "None of it on the main display.");
    }

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    public void AGaugeOnTheMainDisplayStillOpensItsCardThere(int dpi)
    {
        (DisplayArea main, DisplayArea second) = Displays(dpi);
        int taskbar = 48 * dpi / 96;
        int gaugeHeight = 40 * dpi / 96;
        var gauge = new Rectangle(1700, 1080 - taskbar + ((taskbar - gaugeHeight) / 2), 74 * dpi / 96, gaugeHeight);
        DisplayArea[] displays = [main, second];

        Rectangle workArea = WidgetCardPlacement.WorkAreaFor(gauge, displays, fallback: Rectangle.Empty);
        Rectangle card = WidgetCardPlacement.Above(gauge, Card, workArea, dpi, GaugePosition.RightEnd);

        Assert.AreEqual(main.WorkArea, workArea);
        Assert.IsTrue(main.WorkArea.Contains(card));
    }
}
