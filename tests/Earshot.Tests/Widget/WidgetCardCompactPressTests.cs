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

    private static (WidgetCard Card, Counts Counts) Shown(CompactContent content)
    {
        WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, 1.0, notice: true);
        var counts = new Counts();
        card.CompactActionRequested += (_, _) => counts.Action++;
        card.CompactBodyClicked += (_, _) => counts.Body++;
        card.Render(Compact(content), 96);
        card.Show();
        Application.DoEvents();
        return (card, counts);
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
            (WidgetCard card, Counts counts) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Point button = Centre(card.CurrentCompactLayout!.Action);
                Down(card, button);

                card.Render(Compact(MovedTo("Moved to another device")), 96);
                Point replacement = Centre(card.CurrentCompactLayout!.Action);
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
            (WidgetCard card, Counts counts) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Rectangle text = card.CurrentCompactLayout!.Text;
                var body = new Point(text.X + 4, text.Y + 4);
                Down(card, body);

                card.Render(Compact(Charged), 96);
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
            (WidgetCard card, Counts counts) = Shown(MovedTo("Moved to iPhone"));
            using (card)
            {
                Point button = Centre(card.CurrentCompactLayout!.Action);
                Down(card, button);

                card.HideAnimated();
                Assert.IsFalse(card.Visible, "A card with no motion is hidden at once");
                card.Render(Compact(MovedTo("Moved to iPhone")), 96);
                card.Show();
                Application.DoEvents();
                Up(card, Centre(card.CurrentCompactLayout!.Action));

                Assert.AreEqual(0, counts.Action, "An up with no down on this showing is no click");
            }
        });
    }
}
