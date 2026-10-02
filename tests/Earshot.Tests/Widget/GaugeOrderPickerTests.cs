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

    // The work area of a 1024 by 768 screen with a taskbar, the smallest the tests run on (a hosted runner's): the settings page does
    // not fit above the taskbar there, so the card scrolls, and a press has to be made where the row is drawn after the scroll.
    private static readonly Rectangle RunnerWorkArea = new(0, 0, 1024, 728);
    private static readonly int[] Dpis = [96, 120, 144];

    // The order row is an expander (CardKit.Row is its header, with the icon, the label and the gauge as it is now) and, open, the grid
    // of six pictures as a second item of the same row on the same surface.
    private static SettingsItem OrderRow(WidgetCard card) =>
        card.CurrentSettingsLayout!.Items.Single(i => i.Kind == SettingsItemKind.Row && i.Row == SettingsRowId.GaugeOrder && i.Tiles.Count == 6);

    // Opens the order expander on a shown page the way a person does: a press on its header.
    private static void OpenOrder(WidgetCard card)
    {
        CardKit.ClickPart(card, SettingsRowId.GaugeOrder, SettingsPart.Expand);
        Assert.IsTrue(card.CurrentSettingsLayout!.Items.Any(i => i.Row == SettingsRowId.GaugeOrder && i.Tiles.Count == 6), "The press opened the pictures.");
    }

    [TestMethod]
    public void ThePicturesAreAGridOfThreeAcrossAndTwoDownFillingTheRowsWidth()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, order: true);
                SettingsItem row = OrderRow(card);
                IReadOnlyList<Rectangle> tiles = row.Tiles;
                Assert.AreEqual(6, tiles.Count);
                // The design: the pictures are a 3 by 2 grid of tiles 52 high, a gap of 8 and a padding of 12 inside the
                // surface, which is itself 12 in from each side of the 360 wide card.
                int gap = CardPlacement.Scale(8, dpi);
                int surfaceLeft = CardPlacement.Scale(12, dpi);
                int pad = CardPlacement.Scale(12, dpi);
                int side = surfaceLeft + pad;
                int surfaceWidth = CardPlacement.Scale(360, dpi) - (2 * surfaceLeft);
                Assert.AreEqual(surfaceLeft, row.Bounds.Left, "The surface is 12 in from the card's side (dpi " + dpi + ").");
                Assert.AreEqual(surfaceWidth, row.Bounds.Width, "And as wide as the card allows.");
                Assert.AreEqual(side, tiles[0].Left, "The grid starts at the surface's padding (dpi " + dpi + ").");
                Assert.AreEqual(surfaceLeft + surfaceWidth - pad, tiles[2].Right, "And ends at the other padding: the spare pixels of the division go to the first columns (dpi " + dpi + ").");
                for (int i = 0; i < 6; i++)
                {
                    Assert.AreEqual(CardPlacement.Scale(52, dpi), tiles[i].Height, "Tile height at " + dpi + " dpi.");
                    Assert.IsLessThanOrEqualTo(1, Math.Abs(tiles[0].Width - tiles[i].Width), "The columns differ by one pixel at most.");
                    Assert.AreEqual(tiles[i % 3].X, tiles[i].X, "Columns line up.");
                    Assert.AreEqual(tiles[i % 3].Width, tiles[i].Width, "A column is as wide in both rows.");
                    Assert.AreEqual(tiles[(i / 3) * 3].Y, tiles[i].Y, "Rows line up.");
                }

                Assert.AreEqual(gap, tiles[1].Left - tiles[0].Right, "Gap between columns.");
                Assert.AreEqual(gap, tiles[3].Top - tiles[0].Bottom, "Gap between rows.");
                if (dpi == 96)
                {
                    Assert.AreEqual(new Size(99, 52), tiles[0].Size, "(336 - 2 x 12 - 2 x 8) / 3 is 98 and two over: 99 at 100%.");
                }

                Assert.IsTrue(row.Bounds.Contains(tiles[0]) && row.Bounds.Contains(tiles[5]), "Every picture is inside its row.");
                SettingsItem header = CardKit.Row(card, SettingsRowId.GaugeOrder);
                Assert.IsTrue(header.Bounds.Contains(header.IconRect), "The icon is inside the header above them.");
                Assert.AreEqual(header.Bounds.Bottom, row.Bounds.Top, "The pictures sit right under the header, on the same surface.");
                Assert.AreEqual(1, card.CurrentSettingsLayout!.Surfaces.Count(r => r.Contains(header.Bounds) && r.Contains(row.Bounds)), "One surface holds both.");
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
                CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugeOrder = chosen }), 96, order: true);
                SettingsTarget[] stops = card.CurrentSettingsLayout!.Targets.Where(t => t.Row == SettingsRowId.GaugeOrder).ToArray();
                Assert.AreEqual(2, stops.Length, "The expander is a stop, and the six pictures are one more.");
                Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Expand), stops[0], "The header first.");
                Assert.AreEqual(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, (int)chosen), stops[1], "Then one stop for six pictures, at the chosen one.");
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
                CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, order: true);
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
                                if (bitmap.GetPixel(x, y).ToArgb() != tileFill.ToArgb())
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
                CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { GaugeOrder = chosen }), 96, order: true);
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
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host,
                workAreaFor: _ => RunnerWorkArea);
            Rectangle gauge = new(RunnerWorkArea.Right - 200, RunnerWorkArea.Bottom, 74, 40);
            presenter.RequestShow(gauge, gauge.Location);
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
            OpenOrder(page.Card);
            Rectangle tile = OrderRow(page.Card).Tiles[4];

            CardKit.ClickOnPage(page.Card, tile);

            CardKit.AssertCalls(page.Host, "order:BoltRingNumber");
            Assert.AreEqual(GaugeOrder.BoltRingNumber, page.Host.Values.GaugeOrder);
            Assert.AreEqual(
                new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, 4), page.Card.CurrentSettingsLayout!.Targets.Single(t => t.Row == SettingsRowId.GaugeOrder && t.Part == SettingsPart.Tile),
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
            OpenOrder(card);

            // Tab to the pictures: the back button, then each row in turn, the order's header, and then its pictures.
            int guard = 0;
            while (card.SettingsFocusTarget.Part != SettingsPart.Tile && guard++ < 20)
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
            OpenOrder(page.Card);
            int guard = 0;
            while (page.Card.SettingsFocusTarget.Part != SettingsPart.Tile && guard++ < 20)
            {
                Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            }

            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Right, 0);
            Phase5.TestWindows.Send(page.Card.Handle, WM_KEYDOWN, (nint)Keys.Space, 0);
            CardKit.AssertCalls(page.Host, "order:RingBoltNumber");
        });
    }
}
