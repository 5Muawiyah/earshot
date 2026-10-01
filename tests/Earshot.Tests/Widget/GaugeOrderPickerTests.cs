using System.Drawing;
using System.Windows.Forms;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The "Order" row of the settings page: six pictures of the gauge in a grid of three across, chosen with the mouse or
// the arrow keys and Enter, each drawn by the gauge's own renderer so the picture is the thing chosen.
[TestClass]
public sealed class GaugeOrderPickerTests
{
    private const int WM_KEYDOWN = 0x0100;
    private static readonly int[] Dpis = [96, 120, 144];

    private static SettingsItem OrderRow(WidgetCard card) => CardKit.Row(card, SettingsRowId.GaugeOrder);

    [TestMethod]
    public void ThePicturesAreAGridOfThreeAcrossAndTwoDownFillingTheRowsWidth()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), dpi);
                SettingsItem row = OrderRow(card);
                IReadOnlyList<Rectangle> tiles = row.Tiles;
                Assert.AreEqual(6, tiles.Count);
                int gap = CardPlacement.Scale(8, dpi);
                int side = CardPlacement.Scale(16, dpi);
                int content = CardPlacement.Scale(360, dpi) - (2 * side);
                Assert.AreEqual(side, tiles[0].Left, "The grid starts at the side padding (dpi " + dpi + ").");
                Assert.AreEqual(side + content, tiles[2].Right + ((content - (2 * gap)) % 3), "And ends at the other side (dpi " + dpi + ").");
                for (int i = 0; i < 6; i++)
                {
                    Assert.AreEqual(CardPlacement.Scale(52, dpi), tiles[i].Height, "Tile height at " + dpi + " dpi.");
                    Assert.AreEqual(tiles[0].Width, tiles[i].Width);
                    Assert.AreEqual(tiles[i % 3].X, tiles[i].X, "Columns line up.");
                    Assert.AreEqual(tiles[(i / 3) * 3].Y, tiles[i].Y, "Rows line up.");
                }

                Assert.AreEqual(gap, tiles[1].Left - tiles[0].Right, "Gap between columns.");
                Assert.AreEqual(gap, tiles[3].Top - tiles[0].Bottom, "Gap between rows.");
                if (dpi == 96)
                {
                    Assert.AreEqual(new Size(104, 52), tiles[0].Size, "104 by 52 at 100%.");
                }

                Assert.IsTrue(row.Bounds.Contains(tiles[0]) && row.Bounds.Contains(tiles[5]), "Every picture is inside its row.");
                Assert.IsTrue(row.Bounds.Contains(row.IconRect), "And so is the icon.");
                Assert.IsGreaterThanOrEqualTo(card.CurrentSettingsLayout!.Frame.Body.Top, tiles[0].Top, "Below the header, not under it.");
            }
        });
    }

    [TestMethod]
    public void TheGroupIsOneStopInTheKeyboardOrderAtTheChosenPicture()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (GaugeOrder chosen in Enum.GetValues<GaugeOrder>())
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugeOrder = chosen }), 96);
                SettingsTarget[] stops = card.CurrentSettingsLayout!.Targets.Where(t => t.Row == SettingsRowId.GaugeOrder).ToArray();
                Assert.AreEqual(1, stops.Length, "One stop for six pictures.");
                Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, (int)chosen), stops[0], "At the chosen picture.");
            }
        });
    }

    [TestMethod]
    public void EachPictureIsTheGaugeInItsOrderDrawnByTheGaugesOwnRenderer()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()), dpi);
                using Bitmap bitmap = CardKit.Render(card);
                SettingsItem row = OrderRow(card);
                for (int i = 0; i < 6; i++)
                {
                    var order = (GaugeOrder)i;
                    GaugeLayout layout = GaugeLayout.For(dpi, order);
                    Rectangle tile = row.Tiles[i];
                    int originX = tile.X + ((tile.Width - layout.Width) / 2);
                    int originY = tile.Y + ((tile.Height - layout.Height) / 2);
                    Color tileFill = bitmap.GetPixel(tile.X + 4, tile.Y + 4);

                    // The three pieces are all there (the mark, the bar that stands for the number, the bolt's outline),
                    // each in its own slot, and in the order the picture is of.
                    var ink = new List<(GaugePiece Piece, int Left)>();
                    foreach ((GaugePiece piece, Rectangle slot) in new[]
                    {
                        (GaugePiece.Ring, layout.RingBox), (GaugePiece.Number, layout.NumberSlot), (GaugePiece.Bolt, layout.ChargingSlot),
                    })
                    {
                        var area = new Rectangle(originX + slot.X, originY + slot.Y, slot.Width, slot.Height);
                        int first = -1;
                        for (int x = area.Left; x < area.Right && first < 0; x++)
                        {
                            for (int y = area.Top; y < area.Bottom; y++)
                            {
                                if (bitmap.GetPixel(x, y) != tileFill)
                                {
                                    first = x;
                                    break;
                                }
                            }
                        }

                        Assert.IsGreaterThanOrEqualTo(0, first, order + ": the " + piece + " is drawn at " + dpi + " dpi.");
                        ink.Add((piece, first));
                    }

                    (GaugePiece a, GaugePiece b, GaugePiece c) = GaugeOrders.Sequence(order);
                    CollectionAssert.AreEqual(
                        new[] { a, b, c }, ink.OrderBy(p => p.Left).Select(p => p.Piece).ToArray(), order + ": left to right at " + dpi + " dpi.");
                }
            }
        });
    }

    [TestMethod]
    public void TheChosenPictureHasAnAccentBorderAndTheOthersDoNot()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (GaugeOrder chosen in new[] { GaugeOrder.RingNumberBolt, GaugeOrder.BoltRingNumber })
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugeOrder = chosen }), 96);
                using Bitmap bitmap = CardKit.Render(card);
                Color accent = WidgetCard.AccentLight;
                SettingsItem row = OrderRow(card);
                for (int i = 0; i < 6; i++)
                {
                    Rectangle tile = row.Tiles[i];
                    Color edge = bitmap.GetPixel(tile.X + 1, tile.Y + (tile.Height / 2));
                    Color inner = bitmap.GetPixel(tile.X, tile.Y + (tile.Height / 2));
                    bool isChosen = i == (int)chosen;
                    Assert.AreEqual(isChosen, edge == accent || inner == accent, "Picture " + i + " with " + chosen + " chosen.");
                }
            }
        });
    }

    [TestMethod]
    public void EveryPictureHasItsOwnAccessibleNameAndTooltipAndTheRowHasItsOwn()
    {
        string[] expected =
            ["Ring, number, bolt", "Ring, bolt, number", "Number, ring, bolt", "Number, bolt, ring", "Bolt, ring, number", "Bolt, number, ring"];
        for (int i = 0; i < 6; i++)
        {
            Assert.AreEqual(expected[i], SettingsRows.NameOf(SettingsRowId.GaugeOrder, SettingsPart.Tile, i));
            Assert.AreEqual(expected[i], SettingsRows.TipOf(SettingsRowId.GaugeOrder, SettingsPart.Tile, i));
        }

        SettingsRowInfo info = SettingsRows.For(SettingsRowId.GaugeOrder);
        Assert.AreEqual(FluentGlyphs.Sort, info.Glyph);
        Assert.AreEqual("Gauge order", info.Name);
        Assert.AreEqual("Pick how the gauge's ring, number and bolt line up", info.Tip);
    }

    private sealed class Opened : IDisposable
    {
        public required FakeCardHost Host { get; init; }

        public required WidgetCardPresenter Presenter { get; init; }

        public required WidgetCard Card { get; init; }

        public static Opened Open(FakeCardHost host)
        {
            var log = new CapturingLog();
            WidgetCard? card = null;
            var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            Assert.AreEqual(WidgetCardView.Settings, presenter.ViewForTest, "The gear opened the settings page.");
            return new Opened { Host = host, Presenter = presenter, Card = card };
        }

        public void Dispose() => Presenter.Dispose();
    }

    [TestMethod]
    public void AClickOnAPictureChoosesItAndTheGaugeIsToldAtOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Opened page = Opened.Open(new FakeCardHost());
            Rectangle tile = OrderRow(page.Card).Tiles[4];

            CardKit.Click(page.Card, tile);

            CardKit.AssertCalls(page.Host, "order:BoltRingNumber");
            Assert.AreEqual(GaugeOrder.BoltRingNumber, page.Host.Values.GaugeOrder);
            Assert.AreEqual(
                new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, 4), page.Card.CurrentSettingsLayout!.Targets.Single(t => t.Row == SettingsRowId.GaugeOrder),
                "The page is drawn again from the saved choice.");
        });
    }

    [TestMethod]
    public void TheArrowKeysMoveTheFocusAmongThePicturesAndEnterChoosesOne()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Opened page = Opened.Open(new FakeCardHost());
            WidgetCard card = page.Card;

            // Tab to the pictures: the back button, then each row in turn until the order row.
            int guard = 0;
            while (card.SettingsFocusTarget.Row != SettingsRowId.GaugeOrder && guard++ < 20)
            {
                Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            }

            Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, 0), card.SettingsFocusTarget, "Tab lands on the chosen picture.");

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Right, 0);
            Assert.AreEqual(1, card.SettingsFocusTarget.Index);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Down, 0);
            Assert.AreEqual(4, card.SettingsFocusTarget.Index, "Down moves a row of three.");
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Down, 0);
            Assert.AreEqual(4, card.SettingsFocusTarget.Index, "It stops at the last row.");
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Left, 0);
            Assert.AreEqual(3, card.SettingsFocusTarget.Index);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Up, 0);
            Assert.AreEqual(0, card.SettingsFocusTarget.Index, "Up moves a row of three.");
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Left, 0);
            Assert.AreEqual(0, card.SettingsFocusTarget.Index, "It stops at the first.");
            CardKit.AssertCalls(page.Host);

            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Right, 0);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Right, 0);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Enter, 0);
            CardKit.AssertCalls(page.Host, "order:NumberRingBolt");
            Assert.AreEqual(2, card.SettingsFocusTarget.Index, "Choosing keeps the focus on the picture.");
        });
    }

    [TestMethod]
    public void SpaceChoosesToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Opened page = Opened.Open(new FakeCardHost());
            int guard = 0;
            while (page.Card.SettingsFocusTarget.Row != SettingsRowId.GaugeOrder && guard++ < 20)
            {
                Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            }

            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Right, 0);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Space, 0);
            CardKit.AssertCalls(page.Host, "order:RingBoltNumber");
        });
    }
}
