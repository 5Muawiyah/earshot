using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Interop;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The one presenter opens the card the owner asks for and the cards nobody asked for: the two are never on screen together,
// no unasked opening takes focus, every line the card writes starts with "Card:", and the status service's events played
// through the presenter show the card once for one opening of the case.
public sealed partial class WidgetCardUnaskedTests
{
    private static bool BothVisible(WidgetCardPresenter presenter) => presenter.IsShown && presenter.IsUnaskedShown;

    private static string Lines(CapturingLog log) => string.Join(" | ", log.Entries.Select(e => e.Message));

    // ---- The asked card and the unasked ones are never on screen together

    [TestMethod]
    public void AnAskedOpenClosesTheUnaskedCardAndTheTwoAreNeverVisibleTogether()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            using var presenter = NewPresenter(() => new WidgetCard(log, CardOpening.Unasked), callbacks.Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(),
                Inline, new Streaming.TestTimeProvider(), log, new FakeScene(), asked: () => new WidgetCard(log));

            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsUnaskedShown, "Sanity: the unasked card is open.");
            Assert.IsFalse(BothVisible(presenter));

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown, "The card he asked for opens.");
            Assert.IsFalse(presenter.IsUnaskedShown, "The unasked card closed to make room.");
            Assert.IsFalse(BothVisible(presenter));
            Assert.IsTrue(log.Has(LogLevel.Debug, "Card: closed (case), an asked card was opened, after "), Lines(log));

            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsUnaskedShown, "A case opening while the asked card is open shows nothing.");
            Assert.IsFalse(BothVisible(presenter));

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "A second gauge click closes the asked card.");
            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsUnaskedShown, "With the asked card gone the case opening shows again.");
            Assert.IsFalse(BothVisible(presenter));
        });
    }

    [TestMethod]
    public void AGaugeClickThenAnUnaskedOpeningThenAnotherGaugeClickKeepsOneCardOnScreen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            using var presenter = NewPresenter(() => new WidgetCard(log, CardOpening.Unasked), new FakeCallbacks().Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(),
                Inline, new Streaming.TestTimeProvider(), log, new FakeScene(), asked: () => new WidgetCard(log));

            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsUnaskedShown);

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();

            Assert.IsFalse(presenter.IsUnaskedShown, "The click closes the unasked card whatever the click then does.");
            Assert.IsFalse(BothVisible(presenter));
        });
    }

    [TestMethod]
    public void TheUpdatePageAskedForClosesTheUnaskedCardToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            using var presenter = new WidgetCardPresenter(
                () => new WidgetCard(log), new FakeCallbacks().Build(), Inline, new Streaming.TestTimeProvider(), log, host: new FakeCardHost(),
                unasked: new UnaskedParts(() => new WidgetCard(log, CardOpening.Unasked), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), new FakeScene()));

            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsUnaskedShown);

            presenter.RequestUpdatePage(gauge, gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.IsFalse(BothVisible(presenter));
        });
    }

    // ---- No focus on any unasked opening

    [TestMethod]
    public void NoUnaskedOpeningTakesTheForegroundOrActivatesItsWindow()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint before = Phase5.TestWindows.GetActiveWindow();

            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            var created = new List<WidgetCard>();
            var gauge = new Rectangle(100, 900, 88, 48);
            var scene = new FakeScene { List = [CaseOpenCardChoiceTests.One, CaseOpenCardChoiceTests.Two] };
            using var presenter = NewPresenter(
                () => { var card = new WidgetCard(log, CardOpening.Unasked); created.Add(card); return card; }, new FakeCallbacks().Build(),
                Gate(closeSeconds: 5, displays: [CaseOpenCardDisplayChoice.All]), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, time, log, scene);

            // The first opening, on every display; the same opening again in place; one after its close time; one after the case closed.
            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "The first opening.");
            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "The same opening again, in place.");
            time.Advance(TimeSpan.FromSeconds(5));
            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "After its close time.");
            presenter.CaseClosed();
            presenter.CaseOpened(gauge);
            Application.DoEvents();
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "After the case closed.");

            Assert.IsTrue(presenter.IsUnaskedShown);
            Assert.HasCount(2, created, "One window for each chosen display.");
            foreach (WidgetCard card in created)
            {
                Assert.IsTrue(card.ShowsWithoutActivationForTest, "Created for a card nobody asked for.");
                Assert.AreNotEqual(Phase5.TestWindows.GetActiveWindow(), card.Handle, "Never the active window.");
            }
        });
    }

    // ---- The log lines

    [TestMethod]
    public void AnOpeningAndItsCloseWriteTheCardLinesWithTheirReasons()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            var scene = new FakeScene { List = [CaseOpenCardChoiceTests.One, CaseOpenCardChoiceTests.Two] };
            using var presenter = NewPresenter(
                () => new WidgetCard(log, CardOpening.Unasked), new FakeCallbacks().Build(), Gate(displays: [CaseOpenCardDisplayChoice.All]),
                new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, time, log, scene);

            presenter.CaseOpened(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(
                log.Has(LogLevel.Debug, "Card: shown unasked (case), on 2 displays, state QUNS_ACCEPTS_NOTIFICATIONS, closes when the case closes."), Lines(log));

            time.Advance(TimeSpan.FromMilliseconds(8200));
            presenter.CaseClosed();
            Assert.IsTrue(log.Has(LogLevel.Debug, "Card: closed (case), the case closed, after 8200 ms."), Lines(log));
        });
    }

    [TestMethod]
    public void ACloseTimeIsSaidInTheOpeningLineAndItsEndInTheClosingLine()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            using var presenter = NewPresenter(
                () => new WidgetCard(log, CardOpening.Unasked), new FakeCallbacks().Build(), Gate(closeSeconds: 10),
                new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, time, log, new FakeScene());

            presenter.CaseOpened(gaugeBounds: null);
            Application.DoEvents();
            time.Advance(TimeSpan.FromSeconds(10));

            Assert.IsTrue(log.Has(LogLevel.Debug, "Card: shown unasked (case), on 1 display, state QUNS_ACCEPTS_NOTIFICATIONS, closes after 10 s."), Lines(log));
            Assert.IsTrue(log.Has(LogLevel.Debug, "Card: closed (case), its close time of 10 s passed, after 10000 ms."), Lines(log));
        });
    }

    [TestMethod]
    public void NoLineOfTheCardCarriesTheOldCaseOpenWording()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var scene = new FakeScene { List = [CaseOpenCardChoiceTests.One] };
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY) };
            using var presenter = NewPresenter(
                () => new WidgetCard(log, CardOpening.Unasked), new FakeCallbacks().Build(), Gate(), environment, Inline, new Streaming.TestTimeProvider(), log, scene);

            presenter.CaseOpened(gaugeBounds: null);
            environment.Notifications = new NotificationStateReading(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS);
            presenter.CaseOpened(gaugeBounds: null);
            Application.DoEvents();
            presenter.CaseClosed();

            Assert.IsFalse(log.Entries.Any(e => e.Message.StartsWith("Case-open card", StringComparison.Ordinal)), Lines(log));
            Assert.IsGreaterThanOrEqualTo(3, log.Entries.Count(e => e.Message.StartsWith("Card:", StringComparison.Ordinal)), Lines(log));
        });
    }

    [TestMethod]
    public void AnAskedOpenWritesItsCardLine()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            using var presenter = NewPresenter(
                ThrowingFactory, new FakeCallbacks().Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, new Streaming.TestTimeProvider(), log, new FakeScene(),
                asked: () => new WidgetCard(log));

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(log.Has(LogLevel.Debug, "Card: shown asked (gauge)."), Lines(log));
        });
    }

    // ---- The whole stack: the status service's events into the presenter

    // The lone pair about a minute after the close, played through the real status service and into the presenter, shows the card
    // once and closes it once: a second show is what the owner saw.
    [TestMethod]
    public void ALonePairAfterTheCloseNeverShowsTheCardASecondTimeThroughTheWholeStack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var harness = new CaseOpenHarness();
            var time = new Streaming.TestTimeProvider();
            using var presenter = NewPresenter(
                () => new WidgetCard(harness.Log, CardOpening.Unasked), new FakeCallbacks().Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, time,
                harness.Log, new FakeScene());
            harness.Service.CaseOpened += (_, _) => presenter.CaseOpened(gaugeBounds: null);
            harness.Service.CaseClosed += (_, _) => presenter.CaseClosed();

            harness.Link();
            Application.DoEvents();
            Assert.IsTrue(presenter.IsUnaskedShown, "The link is the first show.");
            harness.Steady(2.5, 6);
            harness.AdvanceTo(14.5);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsUnaskedShown, "Eight seconds of silence close it.");

            harness.SendAt(66.7);
            harness.SendAt(68);
            harness.AdvanceTo(83);
            harness.AdvanceTo(198);
            Application.DoEvents();

            Assert.IsFalse(presenter.IsUnaskedShown, "The pair a minute later does not show the card again.");
            Assert.AreEqual(1, harness.Log.Entries.Count(e => e.Message.StartsWith("Card: shown unasked", StringComparison.Ordinal)), "One show. " + Lines(harness.Log));
            Assert.AreEqual(1, harness.Log.Entries.Count(e => e.Message.StartsWith("Card: closed (case)", StringComparison.Ordinal)), "One close. " + Lines(harness.Log));
        });
    }

    // ---- Connect leaves the card where it is

    [TestMethod]
    public void AConnectFromTheAskedCardLeavesItOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            WidgetCard? card = null;
            using var presenter = NewPresenter(
                ThrowingFactory, callbacks.Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, new Streaming.TestTimeProvider(), log, new FakeScene(),
                asked: () => card = new WidgetCard(log));

            presenter.RequestShow(gauge, gauge.Location);
            Application.DoEvents();
            Rectangle button = card!.CurrentMainLayout.Button;
            ClickAt(card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));

            Assert.AreEqual(1, callbacks.ToggleCalls.Count, "One toggle request, from the button.");
            Assert.IsTrue(presenter.IsShown, "Connect no longer closes the card the owner asked for.");
        });
    }
}
