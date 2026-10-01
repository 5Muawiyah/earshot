using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What connects the card to the rest of the widget: the accent colour the gauge already uses, the position of the
// gauge, a press that is cancelled, and the card's one width. Each drives the real card or presenter, and paints
// only into off-screen bitmaps or shows on a private desktop.
[TestClass]
public sealed class WidgetCardWiringTests
{
    private const int WM_CAPTURECHANGED = 0x0215;
    private const int WM_CANCELMODE = 0x001F;

    private static readonly string[] Controls = ["Connect", "Gear", "Update", "Switch"];

    // ---- Accent

    private static Bitmap RenderWithReading(WidgetCard card, int leftPercent)
    {
        card.Render(CardKit.MainModel(CardKit.Snapshot(left: new PartReading(leftPercent, null, false) { ReadAt = DateTimeOffset.UtcNow }, readAt: DateTimeOffset.UtcNow)), 96);
        return CardKit.Render(card);
    }

    private static Color BarFill(WidgetCard card, Bitmap bitmap)
    {
        Rectangle bar = card.CurrentMainLayout.Left.Bar;
        return bitmap.GetPixel(bar.X + 2, bar.Y + (bar.Height / 2));
    }

    [TestMethod]
    public void ACardMadeByTheTrayTakesItsBarsFromTheSystemAccent()
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = new FakeAccent();
            using WidgetCard light = TrayContext.CreateWidgetCard(new CapturingLog(), notice: false, accent);
            light.SetTheme(Color.Black, highContrast: false);
            using (Bitmap bitmap = RenderWithReading(light, 60))
            {
                Assert.AreEqual(accent.Light, BarFill(light, bitmap), "The light card uses the light shade the system gave.");
                Assert.AreNotEqual(WidgetCard.AccentLight, BarFill(light, bitmap));
            }

            using WidgetCard dark = TrayContext.CreateWidgetCard(new CapturingLog(), notice: false, accent);
            dark.SetTheme(Color.White, highContrast: false);
            using (Bitmap bitmap = RenderWithReading(dark, 60))
            {
                Assert.AreEqual(accent.Dark, BarFill(dark, bitmap), "The dark card uses the dark shade.");
            }
        });
    }

    [TestMethod]
    public void ACardMadeForTheCaseOpenNoticeTakesTheSystemAccentToo()
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = new FakeAccent();
            using WidgetCard notice = TrayContext.CreateWidgetCard(new CapturingLog(), notice: true, accent);
            notice.SetTheme(Color.Black, highContrast: false);

            using Bitmap bitmap = RenderWithReading(notice, 60);

            Assert.AreEqual(accent.Light, BarFill(notice, bitmap));
        });
    }

    [TestMethod]
    public void AChangedAccentReachesTheBarsAndAsksTheCardToRepaint()
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = new FakeAccent();
            using WidgetCard card = TrayContext.CreateWidgetCard(new CapturingLog(), notice: false, accent);
            card.SetTheme(Color.Black, highContrast: false);
            Color before;
            using (Bitmap bitmap = RenderWithReading(card, 60))
            {
                before = BarFill(card, bitmap);
            }

            accent.Light = Color.FromArgb(0x10, 0x90, 0x30);
            accent.Raise();

            Assert.AreEqual(1, card.AccentRepaintsForTest, "The change asked the card to repaint.");
            using Bitmap after = CardKit.Render(card);
            Assert.AreNotEqual(before, BarFill(card, after));
            Assert.AreEqual(Color.FromArgb(0x10, 0x90, 0x30), BarFill(card, after), "The next paint uses the new colour.");
        });
    }

    [TestMethod]
    public void AnOpenCardRepaintsWhenTheAccentChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var accent = new FakeAccent();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = TrayContext.CreateWidgetCard(log, notice: false, accent), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);
            Assert.AreEqual(1, accent.Subscribers, "The open card listens for the accent.");
            Assert.AreEqual(0, card!.AccentRepaintsForTest);

            accent.Raise();

            Assert.AreEqual(1, card.AccentRepaintsForTest, "The open card was asked to repaint.");
        });
    }

    [TestMethod]
    public void ADisposedCardStopsListeningForTheAccent()
    {
        Phase5.CardSta.Run(() =>
        {
            var accent = new FakeAccent();
            WidgetCard card = TrayContext.CreateWidgetCard(new CapturingLog(), notice: false, accent);
            Assert.AreEqual(1, accent.Subscribers);

            card.Dispose();

            Assert.AreEqual(0, accent.Subscribers, "A closed card is not kept alive by the accent service.");
        });
    }

    // ---- Position

    [TestMethod]
    public void NextToAppsTheCardIsCentredOnTheGaugeWithinTwelvePixelsOfTheScreenEdges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Rectangle work = Screen.PrimaryScreen!.WorkingArea;
            var gauge = new Rectangle(work.Left + (work.Width / 2) - 37, work.Bottom + 4, 74, 40);
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(GaugePosition.NextToApps), CardKit.Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            int gaugeCentre = gauge.X + (gauge.Width / 2);
            Assert.AreEqual(gaugeCentre, card!.Bounds.X + (card.Bounds.Width / 2), 1, "Centred on the gauge.");
            Assert.IsGreaterThanOrEqualTo(work.Left + 12, card.Bounds.Left);
            Assert.IsLessThanOrEqualTo(work.Right - 12, card.Bounds.Right);
            Assert.IsLessThanOrEqualTo(gauge.Top - 12, card.Bounds.Bottom, "Twelve pixels above the taskbar.");
        });
    }

    [TestMethod]
    public void NextToAppsAGaugeNearTheEdgeKeepsTheCardTwelvePixelsInsideTheScreen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Rectangle work = Screen.PrimaryScreen!.WorkingArea;
            var gauge = new Rectangle(work.Left + 6, work.Bottom + 4, 74, 40);
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(GaugePosition.NextToApps), CardKit.Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();

            Assert.IsGreaterThanOrEqualTo(work.Left + 12, card!.Bounds.Left, "Not closer than 12 px to the screen's edge.");
        });
    }

    [TestMethod]
    public void AtTheRightEndTheCardIsTwelvePixelsFromTheScreenEdgeWhereverTheGaugeIs()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Rectangle work = Screen.PrimaryScreen!.WorkingArea;
            var gauge = new Rectangle(work.Left + (work.Width / 2) - 37, work.Bottom + 4, 74, 40);
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(GaugePosition.RightEnd), CardKit.Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();

            Assert.AreEqual(work.Right - 12, card!.Bounds.Right);
        });
    }

    [TestMethod]
    public void TheCaseOpenCardFollowsTheGaugePositionToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Rectangle work = Screen.PrimaryScreen!.WorkingArea;
            var gauge = new Rectangle(work.Left + (work.Width / 2) - 37, work.Bottom + 4, 74, 40);
            var log = new CapturingLog();
            WidgetCard? card = null;
            var gate = new CaseOpenCardGate(() => true, () => false, () => false, () => false, () => false);
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), CardKit.Callbacks(GaugePosition.NextToApps), gate,
                new Phase5.FakeCardEnvironment(), CardKit.Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gauge);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.AreEqual(gauge.X + (gauge.Width / 2), card!.Bounds.X + (card.Bounds.Width / 2), 1, "Centred on the gauge, as the main card is.");
        });
    }

    // ---- A cancelled press

    [TestMethod]
    [DataRow(WM_CAPTURECHANGED)]
    [DataRow(WM_CANCELMODE)]
    public void APressThatLostCaptureDoesNotClickWhenTheButtonComesUp(int message)
    {
        Phase5.CardDesktop.Run(() =>
        {
            foreach (string control in Controls)
            {
                using var card = new WidgetCard(new CapturingLog());
                card.SetTheme(Color.Black, highContrast: false);
                card.Render(CardKit.MainModel(updateVersion: control == "Update" ? "1.2.0" : null) with { ShowSwitch = control == "Switch" }, 96);
                card.Location = new Point(50, 50);
                card.Show();
                card.Activate();
                Application.DoEvents();
                var activated = new List<string>();
                card.ToggleRequested += (_, _) => activated.Add("Connect");
                card.SettingsRequested += (_, _) => activated.Add("Gear");
                card.UpdateRequested += (_, _) => activated.Add("Update");
                card.AutoPauseChanged += (_, _) => activated.Add("Switch");
                WidgetCardLayout.Layout layout = card.CurrentMainLayout;
                Rectangle rect = control switch
                {
                    "Connect" => layout.Button,
                    "Gear" => layout.Gear,
                    "Update" => layout.UpdateButton,
                    _ => layout.Switch,
                };
                nint lParam = (nint)((((rect.Y + (rect.Height / 2)) & 0xFFFF) << 16) | ((rect.X + (rect.Width / 2)) & 0xFFFF));

                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
                Phase5.TestWindows.Send(card.Handle, message, 0, 0);
                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);

                Assert.IsEmpty(activated, control + ": a press whose capture was taken away must not answer the later button-up as a click.");

                // And the same press, left alone, does click, so the test is not passing because nothing works.
                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);
                CollectionAssert.AreEqual(new[] { control }, activated, control + ": an ordinary click still works.");
            }
        });
    }

    [TestMethod]
    public void APressOnASettingsControlThatLostCaptureDoesNotChangeTheSetting()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            Rectangle toggle = CardKit.Part(card, SettingsRowId.LeftClick, SettingsPart.Toggle);
            nint lParam = (nint)((((toggle.Y + (toggle.Height / 2)) & 0xFFFF) << 16) | ((toggle.X + (toggle.Width / 2)) & 0xFFFF));

            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
            Phase5.TestWindows.Send(card.Handle, WM_CAPTURECHANGED, 0, 0);
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);

            Assert.IsEmpty(host.Calls);
        });
    }

    // ---- The main view: one width, a title row, and a switch that shows on the dark card

    [TestMethod]
    public void TheMainCardIsThreeHundredAndSixtyWideInLightAndDark()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (bool dark in CardKit.Themes)
            {
                using WidgetCard card = CardKit.NewCard(dark);
                card.Render(CardKit.MainModel(), 96);
                using Bitmap bitmap = CardKit.Render(card);

                Assert.AreEqual(360, bitmap.Width);
                Assert.AreEqual(360, card.ClientSize.Width);
            }
        });
    }

    [TestMethod]
    public void TheTitleRowHoldsTheTitleAndA32PixelGearWhoseEdgeIsEightPixelsIntoThePadding()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (bool dark in CardKit.Themes)
            {
                using WidgetCard card = CardKit.NewCard(dark);
                card.Render(CardKit.MainModel(), 96);
                using Bitmap bitmap = CardKit.Render(card);
                WidgetCardLayout.Layout layout = card.CurrentMainLayout;
                Color background = bitmap.GetPixel(0, 0);

                Assert.AreEqual(12, layout.Title.Top, "Twelve pixels of padding above the title row.");
                Assert.AreEqual(32, layout.Title.Height);
                Assert.AreEqual(new Size(32, 32), layout.Gear.Size);
                Assert.AreEqual(360 - 16 + 8, layout.Gear.Right, "The gear's right edge is 8 px into the 16 px padding.");
                Assert.AreEqual(layout.Title.Top, layout.Gear.Top);
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.Title, background), "The title.");
                Assert.IsTrue(CardKit.HasInk(bitmap, layout.Gear, background), "The gear.");
                Assert.IsLessThanOrEqualTo(layout.Gear.Left, layout.Title.Right, "The title does not run under the gear.");
            }
        });
    }

    [TestMethod]
    public void TheGearOpensSettingsFromTheKeyboardToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(CardKit.MainModel(), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            int requests = 0;
            card.SettingsRequested += (_, _) => requests++;

            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget);
            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Enter, 0);

            Assert.AreEqual(1, requests);
            Assert.IsTrue(card.Visible, "The settings open on this card; it stays.");
        });
    }

    [TestMethod]
    public void TheOffSwitchTrackShowsOnTheDarkCardAsAnOutlineWithNoFill()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: true);
            WidgetCardModel model = CardKit.MainModel() with { ShowSwitch = true, AutoPauseOn = false };
            card.Render(model, 96);
            using Bitmap bitmap = CardKit.Render(card);
            Rectangle row = card.CurrentMainLayout.Switch;
            Color background = bitmap.GetPixel(0, 0);
            Color secondary = Color.FromArgb(0xC8, 0xC8, 0xC8);
            var track = new Rectangle(row.Right - 40, row.Y + ((row.Height - 20) / 2), 40, 20);

            Assert.AreEqual(background, bitmap.GetPixel(track.X + 20, track.Y + 10), "Off: no fill inside the track.");
            Assert.AreEqual(secondary, bitmap.GetPixel(track.X + 20, track.Y), "Off: a text.secondary outline, which shows on the dark card.");
            Assert.AreEqual(secondary, bitmap.GetPixel(track.X + 9, track.Y + 10), "Off: the knob at the left is text.secondary.");

            card.Render(model with { AutoPauseOn = true }, 96);
            using Bitmap on = CardKit.Render(card);
            Assert.AreEqual(WidgetCard.AccentDark, on.GetPixel(track.X + 20, track.Y + 10), "On: the accent track.");
        });
    }
}
