using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// The status card flashing. The owner's log showed the card shown for "Connecting" and shown again for "Allowing" 43 ms
// later (the same card, the same place), then for "Connected" about five seconds after. Each Show asked the window manager
// to place and show the card again, and the first state was on screen for a frame before the second replaced it. Two things
// are held here: a new status on a card that is already on screen is drawn in place (no placement, no show or hide) unless the
// card's size changes. The hold before a card first appears is StatusCardHoldTests.
[TestClass]
public sealed class StatusCardInPlaceTests
{
    private const string Device = "Jonathan’s AirPods Pro";

    // Lets everything the first show set going (the system's own messages for the new window) finish before anything is counted.
    private static void Settle()
    {
        for (int i = 0; i < 15; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    // Pumps until the card has painted the given text, or two seconds have passed: a slower machine
    // delivers the paint after more than one pass of the message loop.
    private static void PumpUntilPainted(ConnectCard card, string text)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            IReadOnlyList<string> painted = card.LastPaintedText();
            if (painted.Count > 0 && painted[^1] == text)
            {
                return;
            }
            Thread.Sleep(10);
        }
    }

    // A screen no larger than a hosted test machine's (about 1024x768), so the card is placed where that
    // machine's screen can show it: a window wholly off the screen is never sent a paint.
    private static readonly Rectangle SmallScreen = new(0, 0, 1024, 768);

    private static readonly Point Click = new(900, 748);

    private static PlacementScene SmallScene() => new(
        Click,
        [new DisplayArea(SmallScreen, new Rectangle(0, 0, 1024, 728), IsPrimary: true)],
        Taskbar: Rectangle.FromLTRB(0, 728, 1024, 768),
        TaskbarAutoHide: false);

    private static CardPresenter RealCardPresenter(ConnectCard card, CapturingLog log) =>
        new(log, static action => action(), new FakeCardEnvironment { Scene = SmallScene() }, () => card, static () => new FakeCardTimer(), TimeProvider.System);

    // The real card on a private desktop: a new text of the same size is a repaint and nothing else.
    [TestMethod]
    public void ANewStatusOnACardAlreadyOnScreenIsDrawnInPlaceWithNoPlacementShowOrHide()
    {
        CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var card = new ConnectCard(log);
            using CardPresenter presenter = RealCardPresenter(card, log);
            Point click = Click;

            presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor, click);
            Settle();
            Assert.IsTrue(card.IsShownOnScreen(), "Sanity: the first card is on screen.");
            Rectangle placed = card.Bounds;
            var visibleChanges = new List<bool>();
            card.VisibleChanged += (_, _) => visibleChanges.Add(card.Visible);
            using var counter = new WindowMessageCounter(card.Handle);

            presenter.Show(new CardContent(Device, "Allowing"), CardAnchor.NearCursor, click);
            PumpUntilPainted(card, "Allowing");
            Settle();

            Assert.AreEqual("Allowing", card.LastPaintedText()[^1], "The new status was drawn.");
            Assert.AreEqual(placed, card.Bounds, "Same size, same place.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmShowWindow), "Not shown again.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging), "Not placed again: the window manager was not asked.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanged));
            Assert.IsEmpty(visibleChanges, "Never hidden or shown.");
        });
    }

    // A status of another size moves and resizes the card, which is a placement, but still never a hide or a show.
    [TestMethod]
    public void ANewStatusOfAnotherSizeResizesTheCardOnScreenWithoutHidingOrShowingIt()
    {
        CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var card = new ConnectCard(log);
            using CardPresenter presenter = RealCardPresenter(card, log);
            Point click = Click;
            presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor, click);
            Settle();
            Size before = card.Size;
            var visibleChanges = new List<bool>();
            card.VisibleChanged += (_, _) => visibleChanges.Add(card.Visible);
            using var counter = new WindowMessageCounter(card.Handle);

            presenter.Show(new CardContent(Device, "The AirPods did not come back in time. Try again."), CardAnchor.NearCursor, click);
            Application.DoEvents();

            Assert.AreNotEqual(before, card.Size, "Sanity: a longer status is a wider card.");
            Assert.IsGreaterThanOrEqualTo(1, counter.Count(WindowMessageCounter.WmWindowPosChanged), "The size changed, so the window was placed.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmShowWindow), "Still not shown again.");
            Assert.IsEmpty(visibleChanges, "Never hidden or shown.");
            Assert.IsTrue(card.IsShownOnScreen());
        });
    }

    // The same text again on a card on screen draws nothing.
    [TestMethod]
    public void TheSameStatusAgainOnACardOnScreenIsNotRepainted()
    {
        CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var card = new ConnectCard(log);
            using CardPresenter presenter = RealCardPresenter(card, log);
            Point click = Click;
            presenter.Show(new CardContent(Device, "Connected"), CardAnchor.NearCursor, click);
            Settle();
            using var counter = new WindowMessageCounter(card.Handle);

            presenter.Show(new CardContent(Device, "Connected"), CardAnchor.NearCursor, click);
            Settle();

            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmPaint), "Nothing changed, so nothing was drawn.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging));
        });
    }
}
