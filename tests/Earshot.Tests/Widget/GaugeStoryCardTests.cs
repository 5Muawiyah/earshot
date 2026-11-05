using System.Drawing;
using System.Windows.Forms;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The gauge tells a toggle only while no card is on screen, so a card says so when it comes on screen, begins to leave, comes
// back, is hidden or is disposed: closing a card hands the story to the gauge at once, at the start of its exit.
[TestClass]
public sealed class GaugeStoryCardTests
{
    private static (WidgetCard Card, FakeVBlankClock Clock, Rectangle Place) Make()
    {
        WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0);
        var clock = new FakeVBlankClock(60);
        card.AttachMotion(clock, () => true);
        card.Render(CardKit.MainModel(), 96);
        Size size = card.ClientSize;
        return (card, clock, new Rectangle(200, 900 - size.Height, size.Width, size.Height));
    }

    [TestMethod]
    public void ACardTellsWhenItComesOnScreenWhenItBeginsToLeaveAndWhenItIsGone()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, FakeVBlankClock clock, Rectangle rest) = Make();
            using (card)
            {
                var states = new List<(bool Visible, bool Exiting)>();
                card.OnScreenChanged += (_, _) => states.Add((card.Visible, card.IsExiting));

                card.PresentAnimated(rest, 40);
                Application.DoEvents();
                Assert.IsTrue(states.Count > 0 && states[^1] == (true, false), "On screen.");

                int before = states.Count;
                card.HideAnimated();
                Assert.IsGreaterThan(before, states.Count, "Told at the start of the exit, not at its end.");
                Assert.AreEqual((true, true), states[^1], "Still visible, but leaving: the gauge takes over now.");

                clock.RunUntilIdle();
                Application.DoEvents();
                Assert.AreEqual((false, false), states[^1], "Gone.");
            }
        });
    }

    [TestMethod]
    public void ACardBroughtBackFromItsExitTellsSoToo()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, FakeVBlankClock clock, Rectangle rest) = Make();
            using (card)
            {
                card.PresentAnimated(rest, 40);
                Application.DoEvents();
                clock.RunUntilIdle();
                card.HideAnimated();
                clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(50));
                var states = new List<(bool Visible, bool Exiting)>();
                card.OnScreenChanged += (_, _) => states.Add((card.Visible, card.IsExiting));

                card.PresentAnimated(rest, 40);

                Assert.IsTrue(states.Count > 0 && states[^1] == (true, false), "A card that was leaving and is shown again is on screen again.");
            }
        });
    }

    [TestMethod]
    public void ADisposedCardThatWasOnScreenTellsSoWithItsStateAlreadyGone()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, FakeVBlankClock _, Rectangle rest) = Make();
            card.PresentAnimated(rest, 40);
            Application.DoEvents();
            bool? disposedWhenTold = null;
            card.OnScreenChanged += (_, _) => disposedWhenTold = card.IsDisposed;

            card.Dispose();

            Assert.AreEqual(true, disposedWhenTold, "So a presenter reading it sees no card.");
        });
    }
}
