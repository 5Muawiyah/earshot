using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WidgetCardPresenter against a real WidgetCard, shown only on a private desktop
// (Earshot.Tests.Phase5.CardDesktop.Run). Every callback into TrayContext (the connect path, the
// settings write) is faked here: the point of these tests is the presenter's own lifecycle, placement,
// toggle-close rule and refresh timer, not TrayContext or BlockCoordinator.
[TestClass]
public sealed class WidgetCardPresenterTests
{
    private static readonly Rectangle Gauge = new(100, 900, 88, 48);

    [TestMethod]
    public void ShowingPlacesTheCardAboveTheGaugeAndActivatesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.IsNotNull(card);
            Assert.IsTrue(card!.Bounds.Bottom <= Gauge.Top, "The card sits above the gauge, not over it.");
            Assert.AreEqual(card.Handle, Phase5.TestWindows.GetActiveWindow(), "The card is activated once shown.");
        });
    }

    // GaugeWindow answers WM_MOUSEACTIVATE with MA_NOACTIVATE
    // (GaugeWindowTests.MouseActivateAnswersNoActivate), so a real gauge click never deactivates this card;
    // WidgetCard.OnDeactivate cannot be what a second gauge click relies on. This drives the real gauge
    // click path twice in a row (RequestShow with the same gauge bounds, exactly what
    // TrayContext.OnWidgetCardRequested calls on every left click of the gauge), not a second Form standing
    // in for lost focus.
    [TestMethod]
    public void ASecondGaugeClickWhileTheCardIsShownClosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown, "The first gauge click opens the card.");

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "A second gauge click while the card is shown must close it.");
        });
    }

    // The direct gauge-click close above is a fresh toggle, not the deactivate-close double-click window:
    // a third click right after must reopen it, the same as any ordinary first click would.
    [TestMethod]
    public void AThirdGaugeClickRightAfterTheDirectCloseOpensItAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "Sanity: the second click already closed it.");

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown, "A third click, right after the direct close, is an ordinary open, not suppressed.");
        });
    }

    // The other, distinct trigger for the same "does not reopen" wording: a genuine deactivation from
    // losing focus to some other real window (not the gauge, which cannot cause this), followed by a click
    // on the gauge within the double-click window.
    [TestMethod]
    public void AGaugeClickWithinTheDoubleClickWindowOfALostFocusDeactivateCloseDoesNotReopen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            using var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(500, 500), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "Losing activation to another real window closes the card.");

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "A click inside the double-click window of the deactivate-close does not reopen it.");

            time.Advance(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime + 50));
            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown, "A later click, past the double-click window, opens it again.");
        });
    }

    [TestMethod]
    public void TheReadLineRefreshesEvery5SecondsWhileTheCardIsOpenAndSaysNothingWhileTheReadingIsFresh()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            DateTimeOffset readAt = time.GetUtcNow() - TimeSpan.FromSeconds(10);
            var callbacks = new FakeCallbacks { Snapshot = Snapshot(readAt: readAt) };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.AreEqual("", WidgetCopy.BatteryReadLine(card!.Model.Snapshot.BatteryReadAt, card.Model.Now), "Fresh: no age.");

            Assert.AreEqual(TimeSpan.FromSeconds(5), WidgetCardPresenter.ReadLineRefreshInterval);
            time.Advance(WidgetCardPresenter.ReadLineRefreshInterval);
            Assert.AreEqual(time.GetUtcNow(), card.Model.Now, "Re-rendered at the new time.");
            Assert.AreEqual("", card.ReadLineText, "Still fresh at 15 s: nothing to tick.");
            time.Advance(TimeSpan.FromSeconds(20));
            time.Advance(WidgetCardPresenter.ReadLineRefreshInterval);
            Assert.AreEqual("Battery read under 1 min ago", card.ReadLineText, "Past the fresh window it says its age, in minutes.");
        });
    }

    [TestMethod]
    public void RefreshReRendersTheOpenCardFromTheLatestSnapshot()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.AreEqual(AirPodsWhere.Unknown, card!.Model.Snapshot.Where);

            callbacks.Snapshot = Snapshot(where: AirPodsWhere.ThisPc);
            presenter.Refresh();
            Application.DoEvents();

            Assert.AreEqual(AirPodsWhere.ThisPc, card.Model.Snapshot.Where);
        });
    }

    [TestMethod]
    public void PressingConnectRoutesThroughRequestToggleWithThePlaceAboveTheGaugeAndClosesTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.AreEqual(0, callbacks.ToggleCalls.Count);

            Phase5.TestWindows.Send(card!.Handle, WM_KEYDOWN, (nint)Keys.Enter, 0);

            Assert.AreEqual(1, callbacks.ToggleCalls.Count, "Exactly one toggle request.");
            CardPlace place = callbacks.ToggleCalls[0];
            Assert.AreEqual(new Point(Gauge.X + (Gauge.Width / 2), Gauge.Y), place.ClickPoint, "The place follows the gauge, so the result card lands above it.");
            Assert.IsFalse(presenter.IsShown, "The card closes once Connect/Disconnect is pressed.");
        });
    }

    [TestMethod]
    public void TogglingTheSwitchRoutesThroughSetAutoPauseAndDoesNotClose()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Snapshot = Snapshot(autoPauseAvailable: true), AutoPauseOnValue = false };
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();

            Phase5.TestWindows.Send(card!.Handle, WM_KEYDOWN, (nint)Keys.Tab, 0);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Space, 0);

            Assert.AreEqual(1, callbacks.AutoPauseCalls.Count);
            Assert.IsTrue(callbacks.AutoPauseCalls[0].On, "The switch was off, so the write turns it on.");
            Assert.IsTrue(presenter.IsShown, "The switch never closes the card.");
        });
    }

    // What each part shows, and whether it is fresh, is worked out once by the presenter and handed to the card. The
    // same derivation serves the case-open notice, which builds its model the same way.
    [TestMethod]
    public void TheModelCarriesWhatEachPartShowsAndWhetherItIsFresh()
    {
        var time = new Streaming.TestTimeProvider();
        DateTimeOffset now = time.GetUtcNow();
        var callbacks = new FakeCallbacks
        {
            Snapshot = Snapshot(where: AirPodsWhere.ThisPc) with
            {
                Left = new PartReading(70, false, null) { ReadAt = now - TimeSpan.FromSeconds(5) },
                Right = new PartReading(60, false, null) { ReadAt = now - TimeSpan.FromSeconds(90) },
            },
        };

        WidgetCardModel model = WidgetCardPresenter.BuildModel(callbacks.Build(), time);

        Assert.IsNotNull(model.Parts);
        Assert.IsTrue(model.Parts.Left.Fresh);
        Assert.IsFalse(model.Parts.Right.Fresh, "Read 90 s ago: greyed.");
        Assert.AreEqual(60, model.Parts.Right.Percent, "Still the last value read.");
        Assert.IsFalse(model.Parts.Case.HasValue);
        Assert.AreEqual(WidgetCardView.Main, model.View);
    }

    // A part greys without any new reading: the refresh timer re-renders the open card often enough that it greys
    // within 5 seconds of crossing the fresh window.
    [TestMethod]
    public void APartGreysOnTheOpenCardWhileNothingNewIsRead()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            DateTimeOffset readAt = time.GetUtcNow() - TimeSpan.FromSeconds(28);
            var callbacks = new FakeCallbacks
            {
                Snapshot = Snapshot(where: AirPodsWhere.ThisPc, readAt: readAt) with
                {
                    Left = new PartReading(70, false, null) { ReadAt = readAt },
                },
            };
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsTrue(presenter.CurrentModelForTest!.Parts!.Left.Fresh, "Read 28 s ago: still fresh.");

            time.Advance(WidgetCardPresenter.ReadLineRefreshInterval);

            Assert.IsFalse(presenter.CurrentModelForTest!.Parts!.Left.Fresh, "Read 33 s ago: greyed by the next refresh.");
        });
    }

    private const int WM_KEYDOWN = 0x0100;

    // A card opened with a scale of its own (from another display's gauge) is drawn at that scale for as long as it is open, whatever
    // the host's scale becomes in the meantime; one opened without follows the host's.
    [TestMethod]
    public void ACardOpenedWithAScaleKeepsItWhileOpenAndTheNextCardOpenedWithoutOneFollowsTheHost()
    {
        Phase5.CardDesktop.Run(() =>
        {
            int WidthShownAt(int hostDpi, int? cardDpi)
            {
                var callbacks = new FakeCallbacks { Dpi = hostDpi };
                var log = new CapturingLog();
                using var plain = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
                plain.RequestShow(Gauge, Gauge.Location, dpi: cardDpi);
                Application.DoEvents();
                return plain.CardWidthForTest;
            }

            int at96 = WidthShownAt(96, null);
            int at120 = WidthShownAt(120, null);
            int at144 = WidthShownAt(144, null);
            Assert.IsTrue(at144 > at96, "Sanity: a larger scale gives a wider card.");
            Assert.AreEqual(at144, WidthShownAt(96, 144), "The scale that came with the request is the card's, not the host's.");

            var hostCallbacks = new FakeCallbacks { Dpi = 96 };
            var hostLog = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(hostLog), hostCallbacks.Build(), Inline, new Streaming.TestTimeProvider(), hostLog);
            presenter.RequestShow(Gauge, Gauge.Location, dpi: 144);
            Application.DoEvents();
            hostCallbacks.Dpi = 120;
            presenter.Refresh();
            Application.DoEvents();
            Assert.AreEqual(at144, presenter.CardWidthForTest, "A host scale read while the card is open does not change it.");

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.IsFalse(presenter.IsShown, "Sanity: the second click closed it.");
            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();
            Assert.AreEqual(at120, presenter.CardWidthForTest, "The next card, opened without a scale, follows the host's.");
        });
    }

    private static void Inline(Action action) => action();

    private static WidgetSnapshot Snapshot(AirPodsWhere where = AirPodsWhere.Unknown, bool autoPauseAvailable = false, DateTimeOffset? readAt = null) =>
        new(
            where,
            PartReading.Unknown,
            PartReading.Unknown,
            PartReading.Unknown,
            BatteryReadAt: readAt,
            EarReadAt: null,
            LidOpen: null,
            WidgetWatcherState.Started,
            WatcherErrorCode: null,
            WatcherErrorName: null,
            AutoPauseAvailable: autoPauseAvailable,
            WidgetCounters.Empty)
        {
            Selection = BroadcastSelectionState.Linked,
        };

    // The card is built ahead of the first click (CardPrewarm asks for it): the factory runs once, nothing is shown or
    // activated, and the first open reuses that card instead of making another.
    [TestMethod]
    public void PrewarmBuildsTheCardOnceShowsNothingAndTheFirstOpenReusesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            int made = 0;
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => { made++; return card = new WidgetCard(log); }, callbacks.Build(), Inline, time, log);

            presenter.Prewarm();
            presenter.Prewarm();
            Application.DoEvents();

            Assert.AreEqual(1, made, "The factory runs once, however many times the warm-up is asked for.");
            Assert.IsFalse(presenter.IsShown, "Nothing is shown.");
            Assert.IsFalse(card!.Visible);
            Assert.AreNotEqual(card.Handle, Phase5.TestWindows.GetActiveWindow(), "Nothing is activated.");

            presenter.RequestShow(Gauge, Gauge.Location);
            Application.DoEvents();

            Assert.AreEqual(1, made, "The first open reuses the card built ahead.");
            Assert.IsTrue(presenter.IsShown);
        });
    }

    // Records every call the presenter makes into "TrayContext"; nothing here touches a real coordinator,
    // settings store or device.
    private sealed class FakeCallbacks
    {
        public WidgetSnapshot Snapshot { get; set; } = Snapshot();

        public bool AutoPauseOnValue { get; set; }

        public ToggleIntent? Intent { get; set; }

        public bool Busy { get; set; }

        public int Dpi { get; set; } = 96;

        public Color Ink { get; set; } = Color.Black;

        public bool HighContrast { get; set; }

        public string OtherDeviceLabel { get; set; } = "";

        public List<CardPlace> ToggleCalls { get; } = new();

        public List<(bool On, CardPlace Place)> AutoPauseCalls { get; } = new();

        public WidgetCardPresenterCallbacks Build() => new(
            CurrentSnapshot: () => Snapshot,
            AutoPauseOn: () => AutoPauseOnValue,
            CurrentIntent: () => Intent,
            IsBusy: () => Busy,
            Dpi: () => Dpi,
            Ink: () => Ink,
            HighContrast: () => HighContrast,
            OtherDeviceLabel: () => OtherDeviceLabel,
            RequestToggle: place => ToggleCalls.Add(place),
            SetAutoPause: (on, place) => AutoPauseCalls.Add((on, place)));
    }
}
