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

    // M2 (widget-review-3): GaugeWindow answers WM_MOUSEACTIVATE with MA_NOACTIVATE
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
    public void TheReadLineRefreshesEvery30SecondsWhileTheCardIsOpen()
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
            Assert.AreEqual("Battery read 10 s ago", WidgetCopy.BatteryReadLine(card!.Model.Snapshot.BatteryReadAt, card.Model.Now));

            time.Advance(WidgetCardPresenter.ReadLineRefreshInterval);
            Assert.AreEqual("Battery read 40 s ago", WidgetCopy.BatteryReadLine(card.Model.Snapshot.BatteryReadAt, card.Model.Now), "The read line moves forward with the clock while open.");
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

    private const int WM_KEYDOWN = 0x0100;

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
            ClaimExists: true,
            AutoPauseAvailable: autoPauseAvailable,
            WidgetCounters.Empty);

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
