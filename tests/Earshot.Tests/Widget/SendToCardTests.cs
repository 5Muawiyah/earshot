using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The Send to row on the card, its two switches and shortcut on the settings page, and the picker window itself. Pure checks run
// on an STA thread with nothing shown; the presses run on a private desktop.
[TestClass]
public sealed class SendToCardTests
{
    [TestMethod]
    public void TheRowIsAbsentByDefaultAndSitsUnderTheButtonWhenOn()
    {
        WidgetCardLayout.Layout without = WidgetCardLayout.Compute(96, showSwitch: false);
        WidgetCardLayout.Layout with = WidgetCardLayout.Compute(96, showSwitch: false, showSendTo: true);

        Assert.IsTrue(without.SendTo.IsEmpty);
        Assert.IsFalse(with.SendTo.IsEmpty);
        Assert.AreEqual(with.Button.Bottom + WidgetCardLayout.SendToGapAt96, with.SendTo.Top);
        Assert.AreEqual(without.Height + WidgetCardLayout.SendToGapAt96 + with.SendTo.Height, with.Height);
        Assert.AreEqual(without.Button, with.Button, "Nothing above the row moves.");
    }

    [TestMethod]
    public void TheSwitchRowStillFollowsTheRowWhenBothAreShown()
    {
        WidgetCardLayout.Layout both = WidgetCardLayout.Compute(96, showSwitch: true, showSendTo: true);

        Assert.IsGreaterThan(both.SendTo.Bottom, both.Switch.Top);
    }

    [TestMethod]
    public void TheModelHasNoRowUnlessTheCallbacksAskForIt()
    {
        var time = new Streaming.TestTimeProvider();
        WidgetCardPresenterCallbacks off = CardKit.Callbacks();
        WidgetCardPresenterCallbacks on = off with { SendToRowOn = () => true };
        WidgetCardPresenterCallbacks disabled = off with { SendToRowOn = () => false };

        Assert.IsFalse(WidgetCardPresenter.BuildModel(off, time).ShowSendTo);
        Assert.IsTrue(WidgetCardPresenter.BuildModel(on, time).ShowSendTo);
        Assert.IsFalse(WidgetCardPresenter.BuildModel(disabled, time).ShowSendTo);
    }

    [TestMethod]
    public void TheCardListsTheRowAsAButtonAndTheCaseOpenCardNeverDoes()
    {
        Phase5.CardSta.Run(() =>
        {
            WidgetCardModel model = CardKit.MainModel() with { ShowSendTo = true };
            using WidgetCard ordinary = CardKit.NewCard(dark: false);
            ordinary.Render(model, 96);
            using WidgetCard notice = CardKit.NewCard(dark: false, notice: true);
            notice.Render(model, 96);

            Assert.IsTrue(ordinary.CurrentControls().Any(c => c.Name == "Send to" && c.Role == CardControlRole.PushButton && c.Enabled));
            Assert.IsFalse(notice.CurrentControls().Any(c => c.Name == "Send to"));
            Assert.IsTrue(notice.CurrentMainLayout.SendTo.IsEmpty);
        });
    }

    [TestMethod]
    public void TheRowIsDrawn()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.MainModel() with { ShowSendTo = true }, 96);
            using Bitmap bitmap = CardKit.Render(card);

            Assert.IsTrue(CardKit.HasInk(bitmap, card.CurrentMainLayout.SendTo, bitmap.GetPixel(0, 0)));
        });
    }

    [TestMethod]
    public void APressOnTheRowClosesTheCardAndOpensThePickerOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            int opened = 0;
            WidgetCard? card = null;
            var log = new CapturingLog();
            WidgetCardPresenterCallbacks callbacks = CardKit.Callbacks() with { SendToRowOn = () => true, OpenSendTo = () => opened++ };
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), callbacks, CardKit.Inline, new Streaming.TestTimeProvider(), log, new FakeCardHost(),
                workAreaFor: _ => new Rectangle(0, 0, 1920, 1040));
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            System.Windows.Forms.Application.DoEvents();

            CardKit.Click(card!, card!.CurrentMainLayout.SendTo);

            Assert.AreEqual(1, opened);
            Assert.IsFalse(presenter.IsShown);
        });
    }

    // ---- the settings page

    [TestMethod]
    public void TheTwoSwitchesAndTheShortcutAreOnThePageAndWorkThroughTheHost()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { Values = FakeCardHost.Defaults() with { SendToChord = "Ctrl+Alt+Shift+S" } };
            WidgetCard? card = null;
            var log = new CapturingLog();
            var area = new Rectangle(0, 0, 1920, 2600);
            Rectangle gauge = new(area.Right - 200, area.Bottom, CardKit.Gauge.Width, CardKit.Gauge.Height);
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log, host, workAreaFor: _ => area);
            presenter.RequestShow(gauge, gauge.Location);
            System.Windows.Forms.Application.DoEvents();
            CardKit.Click(card!, card!.CurrentMainLayout.Gear);
            CardKit.OpenExpanders(card);

            CardKit.Click(card, CardKit.Part(card, SettingsRowId.SendToCard, SettingsPart.Toggle));
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.SendToGauge, SettingsPart.Toggle));
            CardKit.Click(card, CardKit.Part(card, SettingsRowId.SendTo, SettingsPart.Clear));

            Assert.IsFalse(host.Values.SendToOnCard);
            Assert.IsFalse(host.Values.SendToOnGauge);
            Assert.IsTrue(host.Calls.Contains("sendToCard:False"));
            Assert.IsTrue(host.Calls.Contains("sendToGauge:False"));
            Assert.IsTrue(host.Calls.Contains("clear:SendTo"), "The cleared shortcut reached the host: " + string.Join(", ", host.Calls));
            Assert.AreEqual(string.Empty, host.Values.SendToChord);
        });
    }

    [TestMethod]
    public void EachSettingsRowHasAnIconAndShortWords()
    {
        foreach (SettingsRowId row in new[] { SettingsRowId.SendTo, SettingsRowId.SendToCard, SettingsRowId.SendToGauge })
        {
            SettingsRowInfo info = SettingsRows.For(row);
            Assert.AreNotEqual('\0', info.Glyph, row.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Tip));
        }

        Assert.AreEqual("Clear", SettingsRows.TipOf(SettingsRowId.SendTo, SettingsPart.Clear));
    }

    // ---- the picker window

    [TestMethod]
    public void ThePickerLaysOutRowsUnderTheHeaderAndPaintsThem()
    {
        Phase5.CardSta.Run(() =>
        {
            using var picker = new SendToPicker(new CapturingLog(), new FixedAccent());
            picker.SetTheme(Color.Black, highContrast: false);
            SendToModel model = SendToModel.From(AirPodsWhere.ThisPc, "iPhone", busy: false);

            Size size = picker.Prepare(model, 96, openedByKeyboard: false);
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                picker.RenderContent(g);
            }

            SendToLayout.Layout layout = picker.CurrentLayout;
            Assert.AreEqual(280, size.Width);
            Assert.HasCount(3, layout.Rows);
            Assert.IsGreaterThan(layout.Header.Bottom, layout.Rows[0].Bounds.Top);
            Color background = bitmap.GetPixel(2, 2);
            foreach (SendToLayout.RowLayout row in layout.Rows)
            {
                Assert.IsTrue(CardKit.HasInk(bitmap, row.Bounds, background));
            }

            Assert.AreEqual(1, picker.FocusIndex, "Focus starts on the first row a press would act on, not the one they are already on.");
        });
    }

    [TestMethod]
    public void ThePickerListsItsRowsForAScreenReaderWithTheCurrentOneMarked()
    {
        Phase5.CardSta.Run(() =>
        {
            using var picker = new SendToPicker(new CapturingLog(), new FixedAccent());
            picker.Prepare(SendToModel.From(AirPodsWhere.Elsewhere, "iPhone", busy: false), 96, openedByKeyboard: true);

            IReadOnlyList<CardControl> controls = picker.CurrentControls();

            Assert.AreEqual(4, controls.Count);
            Assert.IsTrue(controls.Any(c => c.Name == "iPhone, current" || c.Name == "Send to iPhone, current"));
            Assert.IsTrue(controls.Single(c => c.Name == "Other speakers").Enabled);
            Assert.IsTrue(picker.FocusCueShown);
        });
    }

    [TestMethod]
    public void APressOnARowReportsItOnceAndAPressOnTheCurrentRowReportsNothing()
    {
        Phase5.CardSta.Run(() =>
        {
            using var picker = new SendToPicker(new CapturingLog(), new FixedAccent());
            picker.Prepare(SendToModel.From(AirPodsWhere.ThisPc, "iPhone", busy: false), 96, openedByKeyboard: false);
            var chosen = new List<SendToChoice>();
            picker.Chosen += (_, c) => chosen.Add(c);
            IReadOnlyList<CardControl> controls = picker.CurrentControls();

            controls.Single(c => c.Name == "This PC, current").Activate();
            Assert.IsEmpty(chosen, "They are here already.");

            controls.Single(c => c.Name == "Other speakers").Activate();
            controls.Single(c => c.Name == "Other speakers").Activate();

            Assert.HasCount(1, chosen);
            Assert.AreEqual(SendToChoice.OtherSpeakers, chosen[0]);
        });
    }

    private sealed class FixedAccent : IAccentColours
    {
        public event EventHandler? Changed { add { } remove { } }

        public Color AccentFor(bool lightTheme) => Color.Blue;
    }
}
