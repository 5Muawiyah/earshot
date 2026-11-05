using System.Drawing;
using System.Windows.Forms;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What a press on the compact row counts for when the row changes under it: a press that began on one row is not answered on another,
// and a card that was hidden holds no press for the next time it is shown. The windows these tests show are on a private desktop.
[TestClass]
public sealed class WidgetCardCompactPressTests
{
    private static readonly TimeSpan Six = TimeSpan.FromSeconds(6);

    private static CompactContent MovedTo(string text) => new('', InkRole.Primary, text, new CompactAction("Take back", '', "Taking back"), Six)
    {
        Kind = UnaskedKind.MovedTo,
    };

    private static CompactContent Charged { get; } = new('', InkRole.Accent, "Fully charged", null, Six) { Kind = UnaskedKind.ChargedFallback };

    private static WidgetCardModel Compact(CompactContent content) =>
        CardKit.MainModel() with { Form = CardForm.Compact, Compact = content, CompactState = CompactActionState.Idle };

    private sealed class Counts
    {
        public int Action { get; set; }

        public int Body { get; set; }
    }

    private static (WidgetCard Card, Counts Counts, Steady Time) Shown(CompactContent content)
    {
        WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
        var counts = new Counts();
        var time = new Steady();
        card.PressClock = time.Now;
        card.CompactActionRequested += (_, _) => counts.Action++;
        card.CompactBodyClicked += (_, _) => counts.Body++;
        card.Render(Compact(content), 96);
        card.Show();
        Application.DoEvents();

        // A fresh show is guarded like a change of form, so these tests start past it.
        time.Ms = 100_000;
        return (card, counts, time);
    }

    private static nint At(Point p) => (nint)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));

    private static Point Centre(Rectangle r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));

    private static void Down(WidgetCard card, Point p) => TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONDOWN, 0, At(p));

    private static void Up(WidgetCard card, Point p) => TestWindows.Send(card.Handle, TestWindows.WM_LBUTTONUP, 0, At(p));

    [TestMethod]
    public void APressOnTheButtonOfOneRowReleasedOverTheRowThatReplacedItRunsNothing()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Counts counts, Steady time) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Point button = Centre(card.CurrentCompactLayout!.Action);
                Down(card, button);

                card.Render(Compact(MovedTo("Moved to another device")), 96);
                Point replacement = Centre(card.CurrentCompactLayout!.Action);
                time.Ms += DoubleClick + 1;
                Up(card, replacement);

                Assert.AreEqual(0, counts.Action, "Take back is not run by a press that began on a row that has gone");
            }
        });
    }

    [TestMethod]
    public void APressOnTheBodyOfOneRowReleasedOverTheRowThatReplacedItGrowsNothing()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Counts counts, Steady time) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Rectangle text = card.CurrentCompactLayout!.Text;
                var body = new Point(text.X + 4, text.Y + 4);
                Down(card, body);

                card.Render(Compact(Charged), 96);
                time.Ms += DoubleClick + 1;
                Up(card, body);

                Assert.AreEqual(0, counts.Body, "A press that began on a row that has gone does not grow the card");
            }
        });
    }

    [TestMethod]
    public void APressLeftOnTheButtonWhenTheCardWentIsNotAnsweredByTheNextUpOnTheReusedCard()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Counts counts, Steady time) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Point button = Centre(card.CurrentCompactLayout!.Action);
                Down(card, button);

                card.HideAnimated();
                Assert.IsFalse(card.Visible, "A card with no motion is hidden at once");
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);
                card.Show();
                Application.DoEvents();
                time.Ms += DoubleClick + 1;
                Up(card, Centre(card.CurrentCompactLayout!.Action));

                Assert.AreEqual(0, counts.Action, "An up with no down on this showing is no click");
            }
        });
    }

    // ---- A press just after a form change is a double-click's second press, not a choice

    private sealed class Steady
    {
        public long Ms { get; set; }

        public long Now() => Ms;
    }

    private static int DoubleClick => SystemInformation.DoubleClickTime;

    private static (WidgetCard Card, Counts Counts, Steady Time, Counts Toggles) ShownOnAClock(CompactContent content, WidgetCardModel? first = null)
    {
        var time = new Steady();
        WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
        card.PressClock = time.Now;
        var counts = new Counts();
        var toggles = new Counts();
        card.CompactActionRequested += (_, _) => counts.Action++;
        card.CompactBodyClicked += (_, _) => counts.Body++;
        card.ToggleRequested += (_, _) => toggles.Action++;
        card.Render(first ?? Compact(content), 96);
        card.Show();
        Application.DoEvents();
        return (card, counts, time, toggles);
    }

    [TestMethod]
    public void AFullCardsConnectIgnoresAPressWithinTheDoubleClickTimeOfTheRowGrowingAndTakesOneAfterIt()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, _, Steady time, Counts toggles) = ShownOnAClock(MovedTo("Moved to iPhone"));
            using (card)
            {
                time.Ms = 100_000;
                card.Render(CardKit.MainModel(), 96);

                time.Ms = 100_000 + DoubleClick - 1;
                CardKit.Click(card, card.CurrentMainLayout.Button);
                Assert.AreEqual(0, toggles.Action, "The second press of a double-click on the row is not Connect");

                time.Ms = 100_000 + DoubleClick;
                CardKit.Click(card, card.CurrentMainLayout.Button);
                Assert.AreEqual(1, toggles.Action, "A press the double-click time after is a choice");
            }
        });
    }

    [TestMethod]
    public void TheRowsButtonAndBodyIgnoreAPressWithinTheDoubleClickTimeOfTheFullCardFoldingBack()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Counts counts, Steady time, _) = ShownOnAClock(MovedTo("Moved to iPhone"), CardKit.MainModel());
            using (card)
            {
                time.Ms = 100_000;
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);
                WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;

                time.Ms = 100_000 + DoubleClick - 1;
                CardKit.Click(card, layout.Action);
                CardKit.Click(card, layout.Text);
                Assert.AreEqual((0, 0), (counts.Action, counts.Body), "Neither Take back nor a grow");

                time.Ms = 100_000 + DoubleClick;
                CardKit.Click(card, layout.Action);
                CardKit.Click(card, layout.Text);
                Assert.AreEqual((1, 1), (counts.Action, counts.Body), "Both after the time");
            }
        });
    }

    [TestMethod]
    public void ARowReplacedByAnotherRowIgnoresAPressWithinTheDoubleClickTime()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, Counts counts, Steady time, _) = ShownOnAClock(Charged);
            using (card)
            {
                time.Ms = 100_000;
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);

                time.Ms = 100_000 + DoubleClick - 1;
                CardKit.Click(card, card.CurrentCompactLayout!.Action);
                Assert.AreEqual(0, counts.Action, "Take back is not pressed by the second press of a double-click");

                time.Ms = 100_000 + DoubleClick;
                CardKit.Click(card, card.CurrentCompactLayout!.Action);
                Assert.AreEqual(1, counts.Action);
            }
        });
    }

    // A promoted Moved to row appears where his first click of a double click was: with animation effects off it is there at once, and
    // its Take back must not take the second press.
    [TestMethod]
    public void AFreshlyShownRowIgnoresAPressWithinTheDoubleClickTimeOfShowingWithAnimationEffectsOff()
    {
        CardDesktop.Run(() =>
        {
            var time = new Steady { Ms = 100_000 };
            WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            using (card)
            {
                var counts = new Counts();
                card.PressClock = time.Now;
                card.CompactActionRequested += (_, _) => counts.Action++;
                card.AttachMotion(new FakeVBlankClock(60), () => false);
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);
                card.Show();
                Application.DoEvents();
                WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;

                time.Ms = 100_000 + DoubleClick - 1;
                CardKit.Click(card, layout.Action);
                Assert.AreEqual(0, counts.Action, "The second press of a double click is not Take back");

                time.Ms = 100_000 + DoubleClick;
                CardKit.Click(card, layout.Action);
                Assert.AreEqual(1, counts.Action, "A press the double-click time after the show is a choice");
            }
        });
    }

    [TestMethod]
    public void TheGuardDoesNotDependOnMotionSoItHoldsWithAnimationEffectsOnAndOff()
    {
        foreach (bool animations in new[] { false, true })
        {
            CardDesktop.Run(() =>
            {
                (WidgetCard card, _, Steady time, Counts toggles) = ShownOnAClock(MovedTo("Moved to iPhone"));
                using (card)
                {
                    var clock = new FakeVBlankClock(60);
                    card.AttachMotion(clock, () => animations);
                    time.Ms = 100_000;
                    card.Render(CardKit.MainModel(), 96);
                    clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(400));

                    time.Ms = 100_000 + DoubleClick - 1;
                    CardKit.Click(card, card.CurrentMainLayout.Button);
                    Assert.AreEqual(0, toggles.Action, "Ignored, with animation effects " + (animations ? "on" : "off"));

                    time.Ms = 100_000 + DoubleClick;
                    CardKit.Click(card, card.CurrentMainLayout.Button);
                    Assert.AreEqual(1, toggles.Action, "Taken after the time, with animation effects " + (animations ? "on" : "off"));
                }
            });
        }
    }

    // ---- An ignored press takes its release with it

    [TestMethod]
    public void APressIgnoredInsideTheDoubleClickTimeTakesItsReleaseWithItHoweverLateItComes()
    {
        CardDesktop.Run(() =>
        {
            (WidgetCard card, _, Steady time, Counts toggles) = ShownOnAClock(MovedTo("Moved to iPhone"));
            using (card)
            {
                var closes = new List<WidgetCardCloseReason>();
                card.CloseRequested += (_, reason) => closes.Add(reason);
                time.Ms = 100_000;
                card.Render(CardKit.MainModel(), 96);
                var empty = new Point(3, 3);

                time.Ms = 100_000 + DoubleClick - 50;
                Down(card, empty);
                time.Ms = 100_000 + DoubleClick + 50;
                Up(card, empty);

                Assert.IsEmpty(closes, "The press was ignored, so its release past the time does not close the grown card.");
                Assert.AreEqual(0, toggles.Action);
                Assert.IsTrue(card.Visible);

                time.Ms += DoubleClick;
                Down(card, empty);
                Up(card, empty);
                Assert.HasCount(1, closes, "A whole click after the time is a click outside, as before.");
            }
        });
    }

    // ---- A card that is fading out answers no press

    [TestMethod]
    public void ACardFadingOutAnswersNoPressOnItsButtonItsBodyOrItsControls()
    {
        CardDesktop.Run(() =>
        {
            var time = new Steady();
            var clock = new FakeVBlankClock(60);
            WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
            using (card)
            {
                var counts = new Counts();
                card.CompactActionRequested += (_, _) => counts.Action++;
                card.CompactBodyClicked += (_, _) => counts.Body++;
                card.PressClock = time.Now;
                card.AttachMotion(clock, () => true);
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);
                Size size = card.ClientSize;
                card.PresentAnimated(new Rectangle(200, 900 - size.Height, size.Width, size.Height), 40);
                Application.DoEvents();
                clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(300));
                time.Ms = 100_000;

                card.HideAnimated();
                clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(40));
                Assert.IsTrue(card.IsExiting, "Sanity: the card is fading out.");
                WidgetCardLayout.CompactLayout layout = card.CurrentCompactLayout!;

                CardKit.Click(card, layout.Action);
                CardKit.Click(card, layout.Text);
                card.CurrentControls().First(c => c.Role == CardControlRole.PushButton).Activate();

                Assert.AreEqual((0, 0), (counts.Action, counts.Body), "A press on a card that is leaving is not answered, so no fold can be cancelled by one.");
            }
        });
    }
}
