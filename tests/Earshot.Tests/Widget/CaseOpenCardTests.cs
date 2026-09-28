using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// CaseOpenCardPresenter: spec 7.6, its own WidgetCard(notice: true) instance, never the gauge-anchored
// card. The gate tests (setting off, closing, QUNS refusal, a failed notification-state read, hand-back,
// session end) never let a card be created at all: createCard throws, so a passing test proves no window
// was ever built, not just that one happened to stay hidden. Everything that actually shows a card runs on
// a private desktop (Earshot.Tests.Phase5.CardDesktop.Run), never the input desktop.
[TestClass]
public sealed class CaseOpenCardTests
{
    private const int WM_KEYDOWN = 0x0100;

    [TestMethod]
    public void TheSettingOffNeverAsksToCreateACard()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(enabled: false);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.AreEqual(0, environment.NotificationQueries, "The setting is checked before the notification state is ever read.");
    }

    [TestMethod]
    public void ClosingRefusesTheCardAndLogsWhy()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(closing: true);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "Earshot is closing"));
    }

    [TestMethod]
    public void QunsBusyRefusesTheCardAndLogsIt()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate();
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY) };
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Info, "not taking notifications"));
    }

    [TestMethod]
    public void AFailedNotificationStateReadRefusesTheCardAndFailsClosed()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate();
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(unchecked((int)0x80004005), 0) };
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Warn, "could not be read"), "A failed read is fail-closed, not fail-open.");
    }

    [TestMethod]
    public void HandBackInProgressRefusesTheCard()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(handBack: true);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "hand-back is running"));
    }

    // M3 (widget-review-3): the case-open notice must never show over the owner's own gauge-anchored card,
    // not just over a second instance of itself (ThrowingFactory proves no card is even built, the same
    // proof the gate's other refusals already use).
    [TestMethod]
    public void TheOwnersOwnCardAlreadyOpenRefusesTheCard()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(ownCardOpen: true);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "the owner's own card is already open"));
    }

    [TestMethod]
    public void SessionEndInProgressRefusesTheCard()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(sessionEnd: true);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "session is ending"));
    }

    [TestMethod]
    public void EveryGateOpenShowsTheCardAboveTheGaugeOrNearTrayOtherwise()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            var gauge = new Rectangle(100, 900, 88, 48);
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(() => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gauge);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.IsTrue(card!.Bounds.Bottom <= gauge.Top, "Above the gauge when it is shown.");
        });
    }

    [TestMethod]
    public void WithNoGaugeTheCardGoesNearTray()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment(); // BottomTaskbar: work area 0,0,1920,1032
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(() => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Rectangle workArea = new(0, 0, 1920, 1032);
            Assert.IsTrue(card!.Bounds.Right <= workArea.Right && card.Bounds.Bottom <= workArea.Bottom, "Clamped inside the work area.");
            Assert.IsGreaterThan(workArea.Width / 2, card.Bounds.X, "NearTray sits in the bottom-right corner, not the middle.");
            Assert.IsGreaterThan(workArea.Height / 2, card.Bounds.Y, "NearTray sits in the bottom-right corner, not the middle.");
        });
    }

    [TestMethod]
    public void ShowingNeverActivatesOrStealsTheForeground()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint before = Phase5.TestWindows.GetActiveWindow();

            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            using var presenter = new CaseOpenCardPresenter(() => new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "A case-open card must never take the foreground.");
        });
    }

    [TestMethod]
    public void TwoCaseOpenedEventsInQuickSuccessionShowAtMostOneCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            int created = 0;
            using var presenter = new CaseOpenCardPresenter(
                () => { created++; return new WidgetCard(log, notice: true); }, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.AreEqual(1, created, "A second CaseOpened while one is already open must not create a second window.");
            Assert.IsTrue(log.Has(LogLevel.Debug, "already open"));
        });
    }

    [TestMethod]
    public void TheDismissTimerUsesTheEnvironmentsDurationAndHidesAtExactlyThatTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            using var presenter = new CaseOpenCardPresenter(
                () => new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, time, log,
                () => new DismissDurationReading(true, 5, 0));

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            time.Advance(TimeSpan.FromSeconds(4.9));
            Assert.IsTrue(presenter.IsShown, "Not yet due.");

            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.IsFalse(presenter.IsShown, "Hidden at exactly the environment's duration.");
        });
    }

    [TestMethod]
    public void AFailedDurationReadFallsBackToFiveSecondsAndLogsTheWin32CodeOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            using var presenter = new CaseOpenCardPresenter(
                () => new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, time, log,
                () => new DismissDurationReading(false, 0, 87));

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.IsFalse(presenter.IsShown, "The default of 5 s is used when the read fails.");

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            time.Advance(TimeSpan.FromSeconds(5));

            int warnings = log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("Win32 error 87", StringComparison.Ordinal));
            Assert.AreEqual(1, warnings, "The same failure is logged once, not on every show.");
        });
    }

    [TestMethod]
    public void AcrossTheCardsWholeLifeTheToggleSinkSeesNoRequestsExceptOneFromConnect()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, time, log,
                () => new DismissDurationReading(true, 5, 0));

            // First life: shown, sits, times out. No toggle request the whole time.
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.IsFalse(presenter.IsShown);
            Assert.AreEqual(0, callbacks.ToggleCalls.Count, "A timeout dismiss must never connect.");

            // Second life: shown again, dismissed by clicking Connect instead.
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            Rectangle button = WidgetCardLayout.Compute(96, showSwitch: false).Button;
            ClickAt(card!.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));

            Assert.AreEqual(1, callbacks.ToggleCalls.Count, "Exactly one toggle request, from the Connect button.");
            Assert.IsFalse(presenter.IsShown, "Pressing Connect closes the card.");
        });
    }

    [TestMethod]
    public void AClickOutsideBothButtonsDismissesTheCardWithoutToggling()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            // The top-left corner of the card: inside the window, outside both the button and the switch.
            ClickAt(card!.Handle, new Point(2, 2));

            Assert.IsFalse(presenter.IsShown, "A click outside both buttons dismisses the notice.");
            Assert.AreEqual(0, callbacks.ToggleCalls.Count);
        });
    }

    // Spec 7.6 says Escape dismisses "if the owner clicks it first, which activates nothing": since a
    // notice-mode card is never activated (WS_EX_NOACTIVATE) it never receives real keyboard focus, so this
    // sends the key message directly at the guard in WidgetCard.OnKeyDown rather than proving the OS would
    // ever deliver it there. See the report for why this is a spec tension, not a gap in this test.
    [TestMethod]
    public void KeyboardNeverActsOnANoticeModeCardEvenIfAKeyMessageArrivedAnyway()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log);

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Phase5.TestWindows.Send(card!.Handle, WM_KEYDOWN, (nint)Keys.Enter, 0);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Escape, 0);

            Assert.IsTrue(presenter.IsShown, "Neither key closes or acts on a notice-mode card.");
            Assert.AreEqual(0, callbacks.ToggleCalls.Count);
        });
    }

    private static void Inline(Action action) => action();

    private static WidgetCard ThrowingFactory() =>
        throw new AssertFailedException("No WidgetCard should be created: the gate must refuse before a card is ever built.");

    // A left down then a left up at the same point: WidgetCard.OnMouseUp now requires a matching left down
    // on the same control before it activates anything (M1, widget-review-3), so an up alone no longer
    // reaches the Connect button or the switch.
    private static void ClickAt(nint handle, Point point)
    {
        nint lParam = MakeLParam(point.X, point.Y);
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);
    }

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static CaseOpenCardGate Gate(bool enabled = true, bool closing = false, bool handBack = false, bool sessionEnd = false, bool ownCardOpen = false) =>
        new(Enabled: () => enabled, Closing: () => closing, HandBackInProgress: () => handBack, SessionEndInProgress: () => sessionEnd, OwnCardOpen: () => ownCardOpen);

    private static WidgetSnapshot Snapshot(AirPodsWhere where = AirPodsWhere.Unknown, bool autoPauseAvailable = false) =>
        new(
            where,
            PartReading.Unknown,
            PartReading.Unknown,
            PartReading.Unknown,
            BatteryReadAt: null,
            EarReadAt: null,
            LidOpen: null,
            WidgetWatcherState.Started,
            WatcherErrorCode: null,
            WatcherErrorName: null,
            ClaimExists: true,
            AutoPauseAvailable: autoPauseAvailable,
            WidgetCounters.Empty);

    // Records every call the presenter makes into "TrayContext"; nothing here touches a real coordinator,
    // settings store or device. Mirrors WidgetCardPresenterTests.FakeCallbacks exactly, since both
    // presenters take the same WidgetCardPresenterCallbacks shape.
    private sealed class FakeCallbacks
    {
        public WidgetSnapshot Snapshot { get; set; } = CaseOpenCardTests.Snapshot();

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
