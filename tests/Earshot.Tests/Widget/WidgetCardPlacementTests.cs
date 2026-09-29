using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card follows the gauge: right-aligned 12 px from the screen edge when the gauge is at the right end,
// centred on it when it is next to the apps, and kept 12 px inside the screen either way.
[TestClass]
public sealed class WidgetCardPlacementTests
{
    private static readonly Size Card = new(360, 300);

    // A 1920 x 1080 screen with a 48 px bottom taskbar: the work area ends at the taskbar's top.
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1032);

    private static Rectangle GaugeAt(int x) => new(x, 1036, 74, 40);

    [TestMethod]
    public void AtTheRightEndTheCardIsTwelvePixelsFromTheScreenEdge()
    {
        Rectangle card = WidgetCardPlacement.Above(GaugeAt(1656), Card, WorkArea, 96, GaugePosition.RightEnd);

        Assert.AreEqual(1920 - 12, card.Right);
        Assert.AreEqual(Card, card.Size);
    }

    [TestMethod]
    public void TheCardsBottomIsTwelvePixelsAboveTheTaskbar()
    {
        Rectangle card = WidgetCardPlacement.Above(GaugeAt(1656), Card, WorkArea, 96, GaugePosition.RightEnd);

        Assert.AreEqual(1032 - 12, card.Bottom, "60 px from the screen bottom on a 48 px taskbar.");
        Assert.AreEqual(1080 - 60, card.Bottom);
    }

    [TestMethod]
    public void NextToAppsTheCardIsCentredOnTheGauge()
    {
        Rectangle gauge = GaugeAt(900);

        Rectangle card = WidgetCardPlacement.Above(gauge, Card, WorkArea, 96, GaugePosition.NextToApps);

        Assert.AreEqual(gauge.X + (gauge.Width / 2), card.X + (card.Width / 2));
    }

    [TestMethod]
    public void NextToAppsTheCardIsKeptTwelvePixelsInsideTheScreen()
    {
        Rectangle nearRight = WidgetCardPlacement.Above(GaugeAt(1880), Card, WorkArea, 96, GaugePosition.NextToApps);
        Rectangle nearLeft = WidgetCardPlacement.Above(GaugeAt(20), Card, WorkArea, 96, GaugePosition.NextToApps);

        Assert.AreEqual(1920 - 12, nearRight.Right);
        Assert.AreEqual(12, nearLeft.Left);
    }

    [TestMethod]
    [DataRow(96, 48, 12)]
    [DataRow(120, 60, 15)]
    [DataRow(144, 72, 18)]
    public void TheTwelvePixelsScaleWithTheDisplay(int dpi, int taskbar, int gap)
    {
        var work = new Rectangle(0, 0, 1920, 1080 - taskbar);
        int gaugeHeight = 40 * dpi / 96;
        var gauge = new Rectangle(1600, 1080 - taskbar + ((taskbar - gaugeHeight) / 2), 74 * dpi / 96, gaugeHeight);

        Rectangle right = WidgetCardPlacement.Above(gauge, Card, work, dpi, GaugePosition.RightEnd);

        Assert.AreEqual(1920 - gap, right.Right);
        Assert.AreEqual(work.Bottom - gap, right.Bottom);
    }

    [TestMethod]
    public void ACardTallerThanTheRoomAboveIsKeptInsideTheWorkArea()
    {
        Rectangle card = WidgetCardPlacement.Above(GaugeAt(1656), new Size(360, 2000), WorkArea, 96, GaugePosition.RightEnd);

        Assert.AreEqual(0, card.Top);
    }

    // With the gauge hidden the card falls back to a point: centred on it, the gap above, clamped as before.
    [TestMethod]
    public void AZeroSizeAnchorCentresTheCardOnThePointWithTheGapAbove()
    {
        Rectangle card = WidgetCardPlacement.Above(new Rectangle(900, 700, 0, 0), Card, WorkArea, 96);

        Assert.AreEqual(900, card.X + (card.Width / 2));
        Assert.AreEqual(700 - 12, card.Bottom);
    }

    [TestMethod]
    public void AnAutoHiddenTaskbarLeavesTheGaugeTheOnlyEdgeToStandAbove()
    {
        var fullScreen = new Rectangle(0, 0, 1920, 1080);

        Rectangle card = WidgetCardPlacement.Above(new Rectangle(1656, 1036, 74, 40), Card, fullScreen, 96, GaugePosition.RightEnd);

        Assert.AreEqual(1036 - 12, card.Bottom);
    }
}
