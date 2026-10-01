using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The maths of a scrolling page: what an offset can be, how far the wheel and a page key move it, what scrolls a control into
// view, and where the thin indicator is. No window.
[TestClass]
public sealed class CardScrollTests
{
    // A body 400 px tall under a header 48 px high, on a page whose body holds 1000 px.
    private static readonly Rectangle Viewport = new(0, 48, 360, 400);
    private const int Content = 1000;

    [TestMethod]
    public void AnOffsetIsKeptBetweenTheTopAndTheLastRowInView()
    {
        Assert.AreEqual(0, CardScroll.MaxOffset(300, 400), "A page that fits does not scroll.");
        Assert.AreEqual(600, CardScroll.MaxOffset(Content, 400));
        Assert.AreEqual(0, CardScroll.Clamp(-50, Content, 400));
        Assert.AreEqual(250, CardScroll.Clamp(250, Content, 400));
        Assert.AreEqual(600, CardScroll.Clamp(9000, Content, 400));
    }

    [TestMethod]
    public void AWheelNotchMovesTheRowsByTheLinesWindowsIsSetToScrollAndNotchesAddUp()
    {
        Assert.AreEqual(48, CardScroll.WheelPixels(-120, 3, 96, 400), "Down is a negative delta; three lines of 16.");
        Assert.AreEqual(-48, CardScroll.WheelPixels(120, 3, 96, 400));
        Assert.AreEqual(16, CardScroll.WheelPixels(-120, 1, 96, 400));
        Assert.AreEqual(24, CardScroll.WheelPixels(-60, 3, 96, 400), "A smooth wheel's part of a notch is the same part of the distance.");
        Assert.AreEqual(72, CardScroll.WheelPixels(-120, 3, 144, 400), "A line is as long as everything else at this scale.");
        Assert.AreEqual(96, CardScroll.WheelPixels(-240, 3, 96, 400));
    }

    [TestMethod]
    public void AWheelSetToAPageMovesAPageAndOneSetToNothingMovesNothing()
    {
        Assert.AreEqual(384, CardScroll.WheelPixels(-120, -1, 96, 400), "A page, less a line.");
        Assert.AreEqual(0, CardScroll.WheelPixels(-120, 0, 96, 400));
        Assert.AreEqual(0, CardScroll.WheelPixels(0, 3, 96, 400));
    }

    [TestMethod]
    public void ARowBelowTheViewportIsScrolledUpJustFarEnoughAndOneAboveJustFarEnoughDown()
    {
        // Body positions run from 48 (the header's height).
        var below = new Rectangle(16, 48 + 700, 328, 36);
        Assert.AreEqual(700 + 36 + 4 - 400, CardScroll.EnsureVisible(0, below, 48, 400, Content, 4), "Its bottom, and the margin, at the viewport's bottom.");

        var above = new Rectangle(16, 48 + 100, 328, 36);
        Assert.AreEqual(100 - 4, CardScroll.EnsureVisible(500, above, 48, 400, Content, 4), "Its top, and the margin, at the viewport's top.");
    }

    [TestMethod]
    public void ARowAlreadyInViewDoesNotMoveTheRows()
    {
        var inView = new Rectangle(16, 48 + 300, 328, 36);
        Assert.AreEqual(250, CardScroll.EnsureVisible(250, inView, 48, 400, Content, 4));
    }

    [TestMethod]
    public void AnOffsetNeverGoesPastEitherEndWhenAControlIsAtTheVeryTopOrBottomOfThePage()
    {
        var first = new Rectangle(16, 48, 328, 36);
        Assert.AreEqual(0, CardScroll.EnsureVisible(300, first, 48, 400, Content, 4), "The margin above the first row is not scrolled into.");

        var last = new Rectangle(16, 48 + Content - 36, 328, 36);
        Assert.AreEqual(600, CardScroll.EnsureVisible(0, last, 48, 400, Content, 4), "Nor the margin under the last.");
    }

    [TestMethod]
    public void ARowTallerThanTheViewportIsShownFromItsTop()
    {
        var tall = new Rectangle(16, 48 + 200, 328, 500);
        Assert.AreEqual(200 - 4, CardScroll.EnsureVisible(0, tall, 48, 400, Content, 4));
    }

    [TestMethod]
    public void TheIndicatorIsNotThereWhenThePageFits()
    {
        Assert.AreEqual(Rectangle.Empty, CardScroll.Thumb(Viewport, 400, 0, 96, wide: false));
        Assert.AreEqual(Rectangle.Empty, CardScroll.Thumb(Viewport, 300, 0, 96, wide: true));
    }

    [TestMethod]
    public void TheIndicatorIsAThinBarAtTheRightEdgeThatTravelsWithTheOffset()
    {
        Rectangle top = CardScroll.Thumb(Viewport, Content, 0, 96, wide: false);
        Rectangle end = CardScroll.Thumb(Viewport, Content, 600, 96, wide: false);

        Assert.AreEqual(2, top.Width, "Thin.");
        Assert.AreEqual(Viewport.Right - 2, top.Right, "Two pixels in from the edge.");
        Assert.AreEqual(Viewport.Top + 4, top.Top, "Starts a little below the header.");
        Assert.AreEqual(Viewport.Bottom - 4, end.Bottom, "Ends a little above the card's bottom.");
        Assert.AreEqual(top.Height, end.Height);
        Assert.IsGreaterThan(top.Top, CardScroll.Thumb(Viewport, Content, 300, 96, wide: false).Top, "Half way is further down than the top.");
        Assert.IsLessThan(end.Top, CardScroll.Thumb(Viewport, Content, 300, 96, wide: false).Top);
    }

    [TestMethod]
    public void TheIndicatorsLengthIsTheShareOfThePageInViewButNeverTooShortToGrab()
    {
        Rectangle half = CardScroll.Thumb(Viewport, 800, 0, 96, wide: false);
        Assert.AreEqual(Math.Round((400 - 8) * 400 / 800.0), half.Height, 1, "Half the page in view, half the track.");

        Rectangle tiny = CardScroll.Thumb(Viewport, 100000, 0, 96, wide: false);
        Assert.AreEqual(24, tiny.Height, "A very long page still leaves a bar to hold.");
    }

    [TestMethod]
    public void TheIndicatorIsWiderNearThePointerAndScalesWithTheScreen()
    {
        Assert.AreEqual(6, CardScroll.Thumb(Viewport, Content, 0, 96, wide: true).Width);
        Assert.AreEqual(3, CardScroll.Thumb(Viewport, Content, 0, 144, wide: false).Width);
        Assert.AreEqual(Viewport.Right - 2 - 6, CardScroll.Thumb(Viewport, Content, 0, 96, wide: true).X, "Wider toward the page, not off the card.");
    }

    [TestMethod]
    public void DraggingTheBarMapsItsPositionBackToAnOffsetThatPutsItThere()
    {
        for (int offset = 0; offset <= 600; offset += 50)
        {
            Rectangle bar = CardScroll.Thumb(Viewport, Content, offset, 96, wide: true);
            int back = CardScroll.OffsetForThumbTop(Viewport, Content, bar.Top, bar.Height, 96);

            Rectangle again = CardScroll.Thumb(Viewport, Content, back, 96, wide: true);
            Assert.AreEqual(bar.Top, again.Top, 1, "The bar lands where it was held, for offset " + offset);
        }

        Assert.AreEqual(0, CardScroll.OffsetForThumbTop(Viewport, Content, -500, 100, 96), "Dragged past the top.");
        Assert.AreEqual(600, CardScroll.OffsetForThumbTop(Viewport, Content, 5000, 100, 96), "Dragged past the bottom.");
    }

    [TestMethod]
    public void ThePointerIsOnTheIndicatorInAStripAlongTheRightEdgeWiderThanTheBar()
    {
        Rectangle zone = CardScroll.Zone(Viewport, 96);

        Assert.AreEqual(Viewport.Right, zone.Right);
        Assert.AreEqual(14, zone.Width);
        Assert.AreEqual(Viewport.Top, zone.Top);
        Assert.AreEqual(Viewport.Height, zone.Height);
        Assert.IsTrue(zone.Contains(CardScroll.Thumb(Viewport, Content, 0, 96, wide: true)));
    }

    [TestMethod]
    public void APageKeyMovesAPageLessALine()
    {
        Assert.AreEqual(384, CardScroll.PagePixels(400, 96));
        Assert.AreEqual(1, CardScroll.PagePixels(5, 96));
    }
}
