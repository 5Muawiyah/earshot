using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card follows Settings > Accessibility > Text size and the other parts of the system look live: at every
// display scale and text size the card keeps its width, its rows grow with the text and nothing is laid outside
// the card.
[TestClass]
public sealed class WidgetCardTextScaleTests
{
    private static readonly int[] Dpis = [96, 120, 144, 168];
    private static readonly double[] Scales = [1.0, 1.5, 2.25];

    private static IEnumerable<Rectangle> MainRectangles(WidgetCardLayout.Layout layout)
    {
        yield return layout.Title;
        yield return layout.Gear;
        yield return layout.Refresh;
        yield return layout.WhereLine;
        yield return layout.Button;
        yield return layout.Switch;
        yield return layout.UpdateLine;
        yield return layout.UpdateCaption;
        yield return layout.UpdateButton;
        foreach (WidgetCardLayout.ColumnLayout column in new[] { layout.Left, layout.Right, layout.Case })
        {
            yield return column.Label;
            yield return column.Glyph;
            yield return column.Bar;
            yield return column.Percent;
            yield return column.BoltSlot;
            yield return column.ReadTime;
        }
    }

    [TestMethod]
    public void TheMainViewKeepsItsWidthAndNothingLeavesTheCardAtAnyTextSize()
    {
        foreach (int dpi in Dpis)
        {
            foreach (double scale in Scales)
            {
                WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(
                    dpi, showSwitch: true, showGear: true, showUpdateLine: true, updateButtonWidth: 0, showRefresh: true, textScale: scale);
                string where = " (dpi " + dpi + ", text " + scale + ")";
                Assert.AreEqual(CardPlacement.Scale(360, dpi), layout.Width, "The card stays 360 epx wide" + where);
                var card = new Rectangle(0, 0, layout.Width, layout.Height);
                foreach (Rectangle rect in MainRectangles(layout))
                {
                    if (!rect.IsEmpty)
                    {
                        Assert.IsTrue(card.Contains(rect), "A row sits inside the card: " + rect + " in " + card + where);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void TheMainViewGrowsWithTheTextSizeAndAtOneHundredPercentIsWhatItWas()
    {
        foreach (int dpi in Dpis)
        {
            int previous = 0;
            foreach (double scale in Scales)
            {
                int height = WidgetCardLayout.Compute(dpi, showSwitch: true, showUpdateLine: true, textScale: scale).Height;
                Assert.IsGreaterThanOrEqualTo(previous, height, "Height never shrinks as text grows (dpi " + dpi + ", text " + scale + ").");
                previous = height;
            }

            Assert.AreEqual(
                WidgetCardLayout.Compute(dpi, showSwitch: true, showUpdateLine: true),
                WidgetCardLayout.Compute(dpi, showSwitch: true, showUpdateLine: true, textScale: 1.0),
                "No text size asked for is the same as 100%.");
            Assert.IsGreaterThan(
                WidgetCardLayout.Compute(dpi, showSwitch: true, showUpdateLine: true, textScale: 1.0).Height,
                WidgetCardLayout.Compute(dpi, showSwitch: true, showUpdateLine: true, textScale: 2.25).Height,
                "225% text makes the card taller.");
        }
    }

    [TestMethod]
    public void TheSettingsPageKeepsItsWidthItsControlsInsideAndItsLabelsClearOfThem()
    {
        using var probe = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(probe);
        CardSettingsValues values = FakeCardHost.Defaults() with { InstallExists = true };
        foreach (int dpi in Dpis)
        {
            int previousHeight = 0;
            foreach (double scale in Scales)
            {
                var measure = new GraphicsTextMeasure(graphics, new CardType(dpi, scale));
                SettingsLayout layout = SettingsPageLayout.Compute(values, dpi, measure, scale);
                string where = " (dpi " + dpi + ", text " + scale + ")";
                Assert.AreEqual(CardPlacement.Scale(360, dpi), layout.Frame.Width, "Width" + where);
                Assert.IsGreaterThanOrEqualTo(previousHeight, layout.Frame.Height, "Height never shrinks" + where);
                previousHeight = layout.Frame.Height;

                var page = new Rectangle(0, 0, layout.Frame.Width, layout.Frame.Height);
                foreach (SettingsItem item in layout.Items.Where(i => i.Kind == SettingsItemKind.Row))
                {
                    foreach (Rectangle control in new[] { item.A, item.B, item.Value })
                    {
                        if (control.IsEmpty)
                        {
                            continue;
                        }

                        Assert.IsTrue(page.Contains(control), item.Row + " control inside the page: " + control + where);
                        // A row that opens (the gauge order's header, Updates, History) takes a press anywhere on it: that part is the row
                        // itself, not a control beside the label. What is drawn beside the label in such a row, its gauge picture and its
                        // chevron, is checked against the label below instead.
                        if (control == item.Bounds)
                        {
                            continue;
                        }

                        Assert.IsFalse(control.IntersectsWith(item.LabelRect), item.Row + " control clear of its label" + where);
                        if (!item.SubRect.IsEmpty)
                        {
                            Assert.IsFalse(control.IntersectsWith(item.SubRect), item.Row + " control clear of its note" + where);
                        }
                    }

                    foreach ((string name, Rectangle part) in new[] { ("preview", item.Preview), ("chevron", item.Chevron) })
                    {
                        if (part.IsEmpty)
                        {
                            continue;
                        }

                        Assert.IsTrue(item.Bounds.Contains(part), item.Row + " " + name + " inside its row" + where);
                        Assert.IsFalse(part.IntersectsWith(item.LabelRect), item.Row + " " + name + " clear of its label" + where);
                    }

                    if (!item.Preview.IsEmpty && !item.Chevron.IsEmpty)
                    {
                        Assert.IsFalse(item.Preview.IntersectsWith(item.Chevron), item.Row + " preview clear of its chevron" + where);
                    }

                    // The label's words fit the rectangle it is given, wrapped in it: the rectangle is what stays clear of the controls,
                    // so text that needed more room than it would be drawn under them.
                    int fourteen = CardPlacement.Scale(14, dpi);
                    int labelLine = TextFit.Grow(SettingsPageLayout.LabelLineAt96, dpi, scale);
                    int lines = measure.Lines(item.Label, item.LabelRect.Width, fourteen, labelLine);
                    Assert.IsLessThanOrEqualTo(item.LabelRect.Height, lines * labelLine, item.Row + " label fits its rectangle" + where);
                    Assert.IsTrue(page.Contains(item.LabelRect), item.Row + " label inside the page" + where);
                    Assert.IsGreaterThan(CardPlacement.Scale(40, dpi) - 1, item.LabelRect.Width - 1, item.Row + " label has room to be read" + where);
                }
            }
        }
    }

    [TestMethod]
    public void ARowWhoseControlLeavesNoRoomForItsLabelStacksTheControlUnderIt()
    {
        using var probe = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(probe);
        var measure = new GraphicsTextMeasure(graphics, new CardType(96, 2.25));
        SettingsLayout layout = SettingsPageLayout.Compute(FakeCardHost.Defaults() with { MoreExpanded = true }, 96, measure, 2.25);
        SettingsItem position = layout.Items.Single(i => i.Row == SettingsRowId.GaugePosition);
        Assert.IsGreaterThanOrEqualTo(position.LabelRect.Bottom, position.A.Top, "The choice sits under the label at 225% text.");
        Assert.IsTrue(position.B.Right <= layout.Frame.Width);
    }

    [TestMethod]
    public void ASubPageFrameGrowsItsHeaderAndFooterWithTheTextAndStaysTheSameAtOneHundredPercent()
    {
        foreach (int dpi in Dpis)
        {
            SubPageFrame.FrameLayout plain = SubPageFrame.Compute(dpi, 100, 2);
            SubPageFrame.FrameLayout same = SubPageFrame.Compute(dpi, 100, 2, 1.0);
            Assert.AreEqual(plain.Header, same.Header);
            Assert.AreEqual(plain.Footer, same.Footer);
            Assert.AreEqual(CardPlacement.Scale(48, dpi), plain.Header.Height);
            Assert.AreEqual(CardPlacement.Scale(64, dpi), plain.Footer.Height);
            SubPageFrame.FrameLayout big = SubPageFrame.Compute(dpi, 100, 2, 2.25);
            Assert.IsGreaterThan(plain.Header.Height, big.Header.Height);
            Assert.IsGreaterThan(plain.Footer.Height, big.Footer.Height);
            Assert.IsTrue(big.Header.Contains(big.Back));
            foreach (Rectangle button in big.Buttons)
            {
                Assert.IsTrue(big.Footer.Contains(button), "A footer button is inside the footer at 225% text.");
            }
        }
    }

    [TestMethod]
    public void ACardRendersAtEveryScaleAndTextSizeAndDrawsInsideItself()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                foreach (double scale in Scales)
                {
                    using var card = new WidgetCard(new CapturingLog());
                    card.AttachLook(() => new SystemLook(scale, Transparency: false, HighContrast: false));
                    card.SetTheme(Color.White, highContrast: false);
                    card.Render(WidgetCardModel.Empty with { ShowSwitch = true }, dpi);
                    Assert.AreEqual(scale, card.TextScale);
                    Assert.AreEqual(CardPlacement.Scale(360, dpi), card.ClientSize.Width);

                    using var bitmap = new Bitmap(card.ClientSize.Width, card.ClientSize.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        card.RenderContent(g);
                    }

                    Assert.AreEqual(255, bitmap.GetPixel(0, 0).A, "A card with transparency off paints its own opaque background.");
                }
            }
        });
    }

    [TestMethod]
    public void WithTransparencyEffectsOffOrHighContrastTheBackgroundIsOpaqueAndOtherwiseNot()
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach ((bool transparency, bool highContrast, bool opaque) in new[] { (true, false, false), (false, false, true), (true, true, true) })
            {
                using var card = new WidgetCard(new CapturingLog());
                card.AttachLook(() => new SystemLook(1.0, transparency, highContrast));
                card.SetTheme(Color.White, highContrast);
                card.Render(WidgetCardModel.Empty, 96);
                card.Location = new Point(50, 50);
                card.Show();
                Application.DoEvents();

                using var bitmap = new Bitmap(card.ClientSize.Width, card.ClientSize.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    card.RenderContent(g);
                }

                int alpha = bitmap.GetPixel(2, 2).A;
                if (opaque)
                {
                    Assert.AreEqual(255, alpha, "Opaque when transparency is " + transparency + " and high contrast " + highContrast + ".");
                }
                else if (card.HasTranslucentBackdrop)
                {
                    // The design (Acrylic): with a system backdrop the card paints the tint over it, and the backdrop shows through
                    // by the tint's own alpha, so the pixel is the tint's, not clear. (White ink tells the card the theme is dark.)
                    // GDI+ stores 210 for the 209 (82%) the dark tint asks for, so the allowance is the one level its conversion adds.
                    int tint = DesignTokens.For(dark: true, highContrast: false).AcrylicTint.A;
                    Assert.IsLessThanOrEqualTo(1, Math.Abs(tint - alpha), "The tint of the theme over the system backdrop when DWM gave it one: " + tint + ", drawn " + alpha + ".");
                    Assert.IsGreaterThan(0, alpha, "Not cleared.");
                }
            }
        });
    }

    // The look read at the last Render is what a re-render uses, so the text size shows without reopening.
    [TestMethod]
    public void AShownCardIsLaidOutAgainAndKeepsItsBottomWhenTheLookChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            SystemLook look = SystemLook.Default;
            Color ink = Color.Black;
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            WidgetCard? card = null;
            WidgetCardPresenterCallbacks callbacks = CardKit.Callbacks() with { Ink = () => ink };
            using var presenter = new WidgetCardPresenter(
                () =>
                {
                    card = new WidgetCard(log);
                    card.AttachLook(() => look);
                    return card;
                },
                callbacks, CardKit.Inline, time, log);

            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            Assert.IsNotNull(card);
            Rectangle before = card!.Bounds;
            Assert.AreEqual(1.0, card.TextScale);
            Assert.AreEqual(0, presenter.LookReappliesForTest);

            look = new SystemLook(1.5, Transparency: false, HighContrast: false);
            ink = Color.White;
            presenter.ReapplyLook();
            Application.DoEvents();

            Assert.AreEqual(1, presenter.LookReappliesForTest, "One re-apply for one change.");
            Assert.AreEqual(1.5, card.TextScale, "The text size is read again.");
            Assert.IsGreaterThan(before.Height, card.Bounds.Height, "Larger text makes the card taller.");
            Assert.AreEqual(before.Bottom, card.Bounds.Bottom, "It grows upward from the taskbar.");

            using var bitmap = new Bitmap(card.ClientSize.Width, card.ClientSize.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                card.RenderContent(g);
            }

            Assert.IsLessThan(0.5f, bitmap.GetPixel(2, 2).GetBrightness(), "The dark theme is applied to the card already open.");
        });
    }

    [TestMethod]
    public void ACardThatIsNotShownIsLeftAlone()
    {
        Phase5.CardSta.Run(() =>
        {
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log);
            presenter.ReapplyLook();
            Assert.AreEqual(0, presenter.LookReappliesForTest);
        });
    }
}
