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

// CaseOpenCardPresenter: the case-open notice, its own WidgetCard(notice: true) instance, never the
// gauge-anchored card. The gate tests (setting off, closing, QUNS refusal, a failed notification-state read, hand-back,
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
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

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
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "Earshot is closing"));
    }

    // With one display a full-screen application can only be on it, so the card is not built at all.
    [TestMethod]
    public void QunsBusyWithOneDisplayKeepsTheCardOffItAndLogsIt()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate();
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY) };
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "a full-screen application is on it"));
    }

    // Presentation settings are no display in particular: no card anywhere.
    [TestMethod]
    public void PresentationSettingsRefuseTheCardOnEveryDisplayAndLogIt()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(displays: [CaseOpenCardDisplayChoice.All]);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(0, Shell.QUNS_PRESENTATION_MODE) };
        var log = new CapturingLog();
        var scene = new FakeScene { List = [CaseOpenCardChoiceTests.One, CaseOpenCardChoiceTests.Two] };
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, scene);

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Info, "not taking notifications"));
    }

    // Windows could not list the displays: the one fallback card is still a card nobody clicked for, so a full-screen
    // application (QUNS_BUSY, D3D full screen) refuses it, as it refuses a card on any display it is on.
    [TestMethod]
    public void WithNoDisplayListAFullScreenApplicationStillRefusesTheCard()
    {
        foreach (int state in new[] { Shell.QUNS_BUSY, Shell.QUNS_RUNNING_D3D_FULL_SCREEN })
        {
            var callbacks = new FakeCallbacks();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(0, state) };
            var log = new CapturingLog();
            using var presenter = new CaseOpenCardPresenter(
                ThrowingFactory, callbacks.Build(), Gate(), environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene { List = [] });

            presenter.RequestShow(new Rectangle(100, 900, 88, 48));

            Assert.IsFalse(presenter.IsShown, "State " + state);
            Assert.IsTrue(log.Has(LogLevel.Debug, "a full-screen application"), "Logged, state " + state);
        }
    }

    [TestMethod]
    public void AFailedNotificationStateReadRefusesTheCardAndFailsClosed()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate();
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment { Notifications = new NotificationStateReading(unchecked((int)0x80004005), 0) };
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

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
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

        presenter.RequestShow(gaugeBounds: null);

        Assert.IsFalse(presenter.IsShown);
        Assert.IsTrue(log.Has(LogLevel.Debug, "hand-back is running"));
    }

    // The case-open notice must never show over the owner's own gauge-anchored card, not just over a
    // second instance of itself (ThrowingFactory proves no card is even built, the same proof the gate's
    // other refusals already use).
    [TestMethod]
    public void TheOwnersOwnCardAlreadyOpenRefusesTheCard()
    {
        var callbacks = new FakeCallbacks();
        var gate = Gate(ownCardOpen: true);
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

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
        using var presenter = new CaseOpenCardPresenter(ThrowingFactory, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

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
            using var presenter = new CaseOpenCardPresenter(() => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gauge);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.IsTrue(card!.Bounds.Bottom <= gauge.Top, "Above the gauge when it is shown.");
        });
    }

    [TestMethod]
    public void WithNoGaugeTheCardGoesInTheCornerOfItsDisplayByTheTaskbar()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(() => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Rectangle workArea = CaseOpenCardChoiceTests.One.WorkArea;
            Assert.IsTrue(card!.Bounds.Right <= workArea.Right && card.Bounds.Bottom <= workArea.Bottom, "Clamped inside the work area.");
            Assert.IsGreaterThan(workArea.Width / 2, card.Bounds.X, "The bottom-right corner, not the middle.");
            Assert.IsGreaterThan(workArea.Height / 2, card.Bounds.Y, "The bottom-right corner, not the middle.");
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
            using var presenter = new CaseOpenCardPresenter(() => new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

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
                () => { created++; return new WidgetCard(log, notice: true); }, callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.AreEqual(1, created, "A second CaseOpened while one is already open must not create a second window.");
            // The card already open takes the new open in place (drawn again, its close time started over), as the presenter says; it is
            // not a refusal, so it is not logged as one.
            Assert.IsTrue(presenter.IsShown, "The one card is still on screen.");
            Assert.IsFalse(log.Has(LogLevel.Debug, "already open"), "A second open of the case is not refused.");
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
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), Gate(closeSeconds: 5), environment, Inline, time, log, new FakeScene());

            // First life: shown, sits, times out. No toggle request the whole time.
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.IsFalse(presenter.IsShown);
            Assert.AreEqual(0, callbacks.ToggleCalls.Count, "A timeout dismiss must never connect.");

            // Second life: shown again, Connect clicked.
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            Rectangle button = WidgetCardLayout.Compute(96, showSwitch: false).Button;
            ClickAt(card!.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));

            Assert.AreEqual(1, callbacks.ToggleCalls.Count, "Exactly one toggle request, from the Connect button.");
            Assert.IsTrue(presenter.IsShown, "Pressing Connect leaves the card open: it closes by its setting, its close button or the case closing.");
        });
    }

    // The case-open notice is always the main view, whatever the snapshot holds.
    [TestMethod]
    public void TheNoticeCardIsAlwaysTheMainView()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Snapshot = Snapshot() };
            var gate = Gate();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), Gate(closeSeconds: 5), environment, Inline, time, log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.AreEqual(WidgetCardView.Main, card!.Model.View);
            Assert.AreEqual(WidgetCardView.Main, card.EffectiveView);
            Assert.IsTrue(card.CurrentMainLayout.ShowColumns, "The three columns stay, with No reading in place of a percent.");
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
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            Assert.IsTrue(presenter.IsShown);

            // The top-left corner of the card: inside the window, outside both the button and the switch.
            ClickAt(card!.Handle, new Point(2, 2));

            Assert.IsFalse(presenter.IsShown, "A click outside both buttons dismisses the notice.");
            Assert.AreEqual(0, callbacks.ToggleCalls.Count);
        });
    }

    // Escape is meant to dismiss the card only if the owner clicks it first, which activates nothing: since
    // a notice-mode card is never activated (WS_EX_NOACTIVATE) it never receives real keyboard focus, so
    // this sends the key message directly at the guard in WidgetCard.OnKeyDown rather than proving the OS
    // would ever deliver it there. That gap between the intended behaviour and what a real key press could
    // ever trigger is inherent to a never-activated window, not something a differently written test could
    // close.
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
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), gate, environment, Inline, new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();

            Phase5.TestWindows.Send(card!.Handle, WM_KEYDOWN, (nint)Keys.Enter, 0);
            Phase5.TestWindows.Send(card.Handle, WM_KEYDOWN, (nint)Keys.Escape, 0);

            Assert.IsTrue(presenter.IsShown, "Neither key closes or acts on a notice-mode card.");
            Assert.AreEqual(0, callbacks.ToggleCalls.Count);
        });
    }

    // A new look (a bigger text size, here a bigger scale on the card's display) makes the notice larger. It keeps its bottom
    // edge, grows upward and stays inside the work area, instead of growing down into the taskbar from where its top-left was.
    [TestMethod]
    public void ALookChangeThatMakesTheNoticeLargerKeepsItsBottomEdgeAndItStaysInTheWorkArea()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            var scene = new FakeScene();
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, new Streaming.TestTimeProvider(), log, scene);
            Rectangle work = CaseOpenCardChoiceTests.One.WorkArea;
            var gauge = new Rectangle(work.Right - 200, work.Bottom + 4, 74, 40);

            presenter.RequestShow(gauge);
            Application.DoEvents();
            Rectangle before = card!.RestBounds;

            scene.List = [CaseOpenCardChoiceTests.One with { Dpi = 144 }];
            presenter.ReapplyLook();
            Application.DoEvents();

            Rectangle after = card.RestBounds;
            Assert.IsGreaterThan(before.Height, after.Height, "The card is larger at the new scale.");
            Assert.AreEqual(before.Bottom, after.Bottom, "It kept its bottom edge.");
            Assert.IsTrue(work.Contains(after), "And it is inside the work area: " + after + " in " + work);
            Assert.AreEqual(1, presenter.LookReappliesForTest);
        });
    }

    [TestMethod]
    public void ALookChangeWhileTheNoticeIsLeavingDrawsNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            var clock = new FakeVBlankClock(60);
            WidgetCard? card = null;
            using var presenter = new CaseOpenCardPresenter(
                () => card = new WidgetCard(log, notice: true), callbacks.Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), Inline, time, log,
                new FakeScene(), animationsEnabled: () => true, frameClockFor: _ => clock);
            presenter.RequestShow(gaugeBounds: null);
            Application.DoEvents();
            clock.RunUntil(clock.Now + TimeSpan.FromMilliseconds(300));
            presenter.Hide();
            Application.DoEvents();
            Assert.IsTrue(card!.IsExiting);
            Rectangle before = card.RestBounds;

            callbacks.Dpi = 144;
            presenter.ReapplyLook();
            Application.DoEvents();

            Assert.AreEqual(before, card.RestBounds, "A card that is leaving is not resized.");
            Assert.AreEqual(0, presenter.LookReappliesForTest);
        });
    }

    // ---- Displays

    private static readonly string IdOne = CaseOpenCardChoiceTests.One.Id;
    private static readonly string IdTwo = CaseOpenCardChoiceTests.Two.Id;
    private static readonly string IdThree = CaseOpenCardChoiceTests.Three.Id;

    private static FakeScene ThreeDisplays() =>
        new() { List = [CaseOpenCardChoiceTests.One, CaseOpenCardChoiceTests.Two, CaseOpenCardChoiceTests.Three] };

    private static ForegroundWindowReading Game(Rectangle monitor) =>
        new(new WindowIdentity("GameWindowClass", false), monitor, monitor, "Display");

    private static (CaseOpenCardPresenter Presenter, List<WidgetCard> Cards, Earshot.Tests.Phase5.FakeCardEnvironment Environment, FakeCallbacks Callbacks, CapturingLog Log)
        Rig(CaseOpenCardGate gate, FakeScene scene, TimeProvider? time = null, FakeCallbacks? callbacks = null)
    {
        callbacks ??= new FakeCallbacks();
        var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
        var log = new CapturingLog();
        var cards = new List<WidgetCard>();
        var presenter = new CaseOpenCardPresenter(
            () =>
            {
                var card = new WidgetCard(log, notice: true);
                cards.Add(card);
                return card;
            },
            callbacks.Build(), gate, environment, Inline, time ?? new Streaming.TestTimeProvider(), log, scene);
        return (presenter, cards, environment, callbacks, log);
    }

    [TestMethod]
    public void ByDefaultItShowsOnlyWhereTheGaugeIs()
    {
        Phase5.CardDesktop.Run(() =>
        {
            FakeScene scene = ThreeDisplays();
            (CaseOpenCardPresenter presenter, List<WidgetCard> cards, _, _, _) = Rig(Gate(gaugeDisplay: IdTwo), scene);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                CollectionAssert.AreEqual(new[] { IdTwo }, presenter.ShownDisplaysForTest.ToArray(), "The display the gauge is set to.");
                Assert.HasCount(1, cards);
                Assert.IsTrue(CaseOpenCardChoiceTests.Two.WorkArea.Contains(cards[0].Bounds), "On that display.");
            }
        });
    }

    [TestMethod]
    public void AllDisplaysShowsOneCardOnEachAtItsOwnScale()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (CaseOpenCardPresenter presenter, List<WidgetCard> cards, _, _, _) = Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays());
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                CollectionAssert.AreEquivalent(new[] { IdOne, IdTwo, IdThree }, presenter.ShownDisplaysForTest.ToArray());
                WidgetCard onThree = presenter.CardForTest(IdThree)!;
                WidgetCard onOne = presenter.CardForTest(IdOne)!;
                Assert.IsGreaterThan(onOne.ClientSize.Width, onThree.ClientSize.Width, "Display 3 is at 150%, so its card is drawn larger.");
                Assert.IsTrue(CaseOpenCardChoiceTests.Three.WorkArea.Contains(onThree.Bounds));
            }
        });
    }

    [TestMethod]
    public void AChosenSetShowsOnThoseDisplaysOnly()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(displays: [IdOne, IdThree], gaugeDisplay: IdTwo), ThreeDisplays());
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                CollectionAssert.AreEquivalent(new[] { IdOne, IdThree }, presenter.ShownDisplaysForTest.ToArray());
            }
        });
    }

    // Never over a full-screen application, display by display, by the gauge's own rule: still on the other chosen ones.
    [TestMethod]
    public void NotOnTheDisplayAFullScreenApplicationIsOnButStillOnTheOthers()
    {
        Phase5.CardDesktop.Run(() =>
        {
            FakeScene scene = ThreeDisplays();
            scene.Window = Game(CaseOpenCardChoiceTests.Two.Bounds);
            (CaseOpenCardPresenter presenter, _, Earshot.Tests.Phase5.FakeCardEnvironment environment, _, CapturingLog log) =
                Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), scene);
            environment.Notifications = new NotificationStateReading(0, Shell.QUNS_RUNNING_D3D_FULL_SCREEN);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                CollectionAssert.AreEquivalent(new[] { IdOne, IdThree }, presenter.ShownDisplaysForTest.ToArray());
                Assert.IsNull(presenter.CardForTest(IdTwo), "No card was even built for the full-screen display.");
                Assert.IsTrue(log.Has(LogLevel.Debug, "Display 2, a full-screen application is on it"));
            }
        });
    }

    [TestMethod]
    public void AFullScreenApplicationOpeningLaterClosesOnlyThatDisplaysCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            FakeScene scene = ThreeDisplays();
            (CaseOpenCardPresenter presenter, _, Earshot.Tests.Phase5.FakeCardEnvironment environment, _, _) =
                Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), scene);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                Assert.HasCount(3, presenter.ShownDisplaysForTest);

                environment.Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY);
                scene.Window = Game(CaseOpenCardChoiceTests.Three.Bounds);
                presenter.RecheckFullScreen();
                Application.DoEvents();

                CollectionAssert.AreEquivalent(new[] { IdOne, IdTwo }, presenter.ShownDisplaysForTest.ToArray());
            }
        });
    }

    // ---- Focus and the screen reader

    // Never takes focus: every card has WS_EX_NOACTIVATE, is shown without activation (ShowWithoutActivation, so the show
    // itself does not activate it), answers a mouse press with MA_NOACTIVATE, and the window that was active stays active.
    [TestMethod]
    public void EveryCardIsShownWithoutActivationAndNeverTakesTheFocus()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint before = Phase5.TestWindows.GetActiveWindow();

            (CaseOpenCardPresenter presenter, List<WidgetCard> cards, _, _, _) = Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays());
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                Assert.HasCount(3, cards);
                foreach (WidgetCard card in cards)
                {
                    Assert.IsTrue(card.Visible);
                    Assert.IsTrue(card.ShowsWithoutActivationForTest, "Shown without activation.");
                    long style = Phase5.TestWindows.ExtendedStyle(card.Handle);
                    Assert.AreEqual((long)NativeMethods.WS_EX_NOACTIVATE, style & NativeMethods.WS_EX_NOACTIVATE);
                    Assert.AreEqual((nint)NativeMethods.MA_NOACTIVATE, Phase5.TestWindows.Send(card.Handle, NativeMethods.WM_MOUSEACTIVATE));
                }

                Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "No case-open card took the foreground.");
            }
        });
    }

    // One announcement per open, from one card, however many displays show it; a new open is announced again.
    [TestMethod]
    public void EachOpenIsAnnouncedOnceFromOneCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            (CaseOpenCardPresenter presenter, List<WidgetCard> cards, _, FakeCallbacks callbacks, _) = Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays(), time);
            DateTimeOffset now = time.GetUtcNow();
            callbacks.Snapshot = Snapshot() with
            {
                Selection = BroadcastSelectionState.Linked,
                Left = new PartReading(70, false, null) { ReadAt = now }, Right = new PartReading(70, false, null) { ReadAt = now }, Case = new PartReading(50, true, null) { ReadAt = now },
                BatteryReadAt = now,
            };
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                Assert.AreEqual(1, presenter.AnnouncementsForTest);
                string[] said = cards.SelectMany(c => c.AnnouncedForTest).ToArray();
                Assert.HasCount(1, said, "Said once, not once per display.");
                Assert.AreEqual("AirPods case open. Left 70%, Right 70%, Case 50%.", said[0]);

                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                Assert.AreEqual(2, presenter.AnnouncementsForTest, "A new open of the case is said again.");
            }
        });
    }

    // ---- Closing

    [TestMethod]
    [DataRow(5)]
    [DataRow(10)]
    [DataRow(30)]
    [DataRow(60)]
    public void ItClosesAtItsCloseTimeOnEveryDisplay(int seconds)
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(closeSeconds: seconds, displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays(), time);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(100));
                Assert.IsTrue(presenter.IsShown, "Not yet due.");

                time.Advance(TimeSpan.FromMilliseconds(100));
                Assert.IsFalse(presenter.IsShown, "Closed on every display at its close time.");
            }
        });
    }

    // A second open of the case while the card is up takes the card in place and starts its close time over (the presenter's own words).
    [TestMethod]
    public void ASecondOpenOfTheCaseStartsTheCloseTimeOver()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(closeSeconds: 5, displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays(), time);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                time.Advance(TimeSpan.FromSeconds(3));
                Assert.IsTrue(presenter.IsShown, "Sanity: 3 s into a 5 s card.");

                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                time.Advance(TimeSpan.FromSeconds(3));
                Assert.IsTrue(presenter.IsShown, "6 s after the first open but 3 s after the second: the close time started over.");

                time.Advance(TimeSpan.FromSeconds(2));
                Assert.IsFalse(presenter.IsShown, "5 s after the second open it closes.");
            }
        });
    }

    [TestMethod]
    public void UntilTheCaseClosesItStaysPastEveryCloseTimeAndClosesWhenTheCaseCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(), new FakeScene(), time);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();

                time.Advance(TimeSpan.FromMinutes(5));
                Assert.IsTrue(presenter.IsShown, "No close time of its own.");

                presenter.CaseClosed();
                Assert.IsFalse(presenter.IsShown, "The case closed.");
            }
        });
    }

    [TestMethod]
    public void TheCaseClosingClosesItBeforeItsOwnCloseTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(closeSeconds: 60), new FakeScene(), time);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                time.Advance(TimeSpan.FromSeconds(10));

                presenter.CaseClosed();
                Assert.IsFalse(presenter.IsShown);
            }
        });
    }

    // The close button stands where the gauge's card has its gear: one press closes every display's card, and asks for no
    // connect.
    [TestMethod]
    public void TheCloseButtonClosesEveryDisplaysCardAndConnectsNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (CaseOpenCardPresenter presenter, List<WidgetCard> cards, _, FakeCallbacks callbacks, _) = Rig(Gate(displays: [CaseOpenCardDisplayChoice.All]), ThreeDisplays());
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                WidgetCard card = presenter.CardForTest(IdOne)!;
                Rectangle close = card.CurrentMainLayout.Gear;
                Assert.IsFalse(close.IsEmpty, "The close button is there.");
                CardControl described = card.CurrentControls().Single(c => c.Name == WidgetCopy.TipCloseCard);
                Assert.IsFalse(described.Focused, "Described to a screen reader, never focused.");

                ClickAt(card.Handle, new Point(close.X + (close.Width / 2), close.Y + (close.Height / 2)));

                Assert.IsFalse(presenter.IsShown, "Every display's card closed.");
                Assert.IsEmpty(callbacks.ToggleCalls);
                Assert.IsTrue(cards.All(c => !c.Visible));
            }
        });
    }

    // Windows only (a real card on a private desktop). After Connect the card updates in place: its button turns to
    // Disconnect, drawn from the new snapshot, with the card still on screen.
    [TestMethod]
    public void AfterConnectTheCardStaysOpenAndItsButtonTurnsToDisconnect()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
            (CaseOpenCardPresenter presenter, _, _, _, _) = Rig(Gate(), new FakeScene(), callbacks: callbacks);
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                WidgetCard card = presenter.CardForTest(IdOne)!;
                Assert.AreEqual(WidgetCopy.Connect, card.CurrentControls()[0].Name);

                Rectangle button = card.CurrentMainLayout.Button;
                ClickAt(card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
                callbacks.Intent = new ToggleIntent(false, callbacks.Intent!.Container, "AirPods Pro 3");
                presenter.Refresh();
                Application.DoEvents();

                Assert.IsTrue(presenter.IsShown, "Still open after Connect.");
                Assert.AreEqual(WidgetCopy.Disconnect, card.CurrentControls()[0].Name, "Updated in place.");
            }
        });
    }

    // Windows only. A close timer that fired as the card was shown again must not close the newer open: its hide was queued
    // for the earlier one. The queue here is manual so the stale callback runs after the second request.
    [TestMethod]
    public void AStaleCloseTimerHideDoesNotCloseANewerOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var time = new Streaming.TestTimeProvider();
            var queue = new Queue<Action>();
            var callbacks = new FakeCallbacks();
            var environment = new Earshot.Tests.Phase5.FakeCardEnvironment();
            var log = new CapturingLog();
            using var presenter = new CaseOpenCardPresenter(
                () => new WidgetCard(log, notice: true), callbacks.Build(), Gate(closeSeconds: 5), environment, queue.Enqueue, time, log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Drain(queue);
            time.Advance(TimeSpan.FromSeconds(5)); // the timer fires: its hide is queued, not yet run
            presenter.RequestShow(gaugeBounds: null); // the case opened again before the hide ran
            Drain(queue);

            Assert.IsTrue(presenter.IsShown, "The stale hide of the first open did nothing.");
        });
    }

    [TestMethod]
    public void ACaseClosedQueuedBeforeAReopenDoesNotCloseTheReopenedCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var queue = new Queue<Action>();
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            using var presenter = new CaseOpenCardPresenter(
                () => new WidgetCard(log, notice: true), callbacks.Build(), Gate(), new Earshot.Tests.Phase5.FakeCardEnvironment(), queue.Enqueue,
                new Streaming.TestTimeProvider(), log, new FakeScene());

            presenter.RequestShow(gaugeBounds: null);
            Drain(queue);
            presenter.CaseClosed();
            presenter.RequestShow(gaugeBounds: null);
            Drain(queue);

            Assert.IsTrue(presenter.IsShown);
        });
    }

    // Windows only. Recheck fails closed: a card on screen closes when the notification state cannot be read, and the
    // fallback card (no display list) closes when a full-screen application is on.
    [TestMethod]
    public void AFailedStateReadAtTheRecheckClosesTheCards()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (CaseOpenCardPresenter presenter, _, Earshot.Tests.Phase5.FakeCardEnvironment environment, _, _) = Rig(Gate(), new FakeScene());
            using (presenter)
            {
                presenter.RequestShow(gaugeBounds: null);
                Application.DoEvents();
                Assert.IsTrue(presenter.IsShown);

                environment.Notifications = new NotificationStateReading(unchecked((int)0x80004005), 0);
                presenter.RecheckFullScreen();
                Application.DoEvents();

                Assert.IsFalse(presenter.IsShown);
            }
        });
    }

    [TestMethod]
    public void TheFallbackCardClosesAtTheRecheckWhenAFullScreenApplicationIsOn()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (CaseOpenCardPresenter presenter, _, Earshot.Tests.Phase5.FakeCardEnvironment environment, _, _) = Rig(Gate(), new FakeScene { List = [] });
            using (presenter)
            {
                presenter.RequestShow(new Rectangle(100, 900, 88, 48));
                Application.DoEvents();
                Assert.IsTrue(presenter.IsShown);

                environment.Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY);
                presenter.RecheckFullScreen();
                Application.DoEvents();

                Assert.IsFalse(presenter.IsShown);
            }
        });
    }

    private static void Drain(Queue<Action> queue)
    {
        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }

        Application.DoEvents();
    }

    private static void Inline(Action action) => action();

    private static WidgetCard ThrowingFactory() =>
        throw new AssertFailedException("No WidgetCard should be created: the gate must refuse before a card is ever built.");

    // A left down then a left up at the same point: WidgetCard.OnMouseUp now requires a matching left down
    // on the same control before it activates anything, so an up alone no longer reaches the Connect
    // button or the switch.
    private static void ClickAt(nint handle, Point point)
    {
        nint lParam = MakeLParam(point.X, point.Y);
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);
    }

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static CaseOpenCardGate Gate(
        bool enabled = true, bool closing = false, bool handBack = false, bool sessionEnd = false, bool ownCardOpen = false,
        int closeSeconds = CaseOpenCardClose.UntilCaseCloses, string[]? displays = null, string gaugeDisplay = "") =>
        new(
            Enabled: () => enabled, Closing: () => closing, HandBackInProgress: () => handBack, SessionEndInProgress: () => sessionEnd, OwnCardOpen: () => ownCardOpen,
            Options: () => new CaseOpenCardOptions(closeSeconds, displays ?? [], gaugeDisplay));

    // Fake displays (the three of CaseOpenCardChoiceTests, or any), the gauges shown on them and the foreground window.
    private sealed class FakeScene : ICaseOpenCardScene
    {
        public IReadOnlyList<DisplayInfo> List { get; set; } = [CaseOpenCardChoiceTests.One];

        public List<Rectangle> Shown { get; } = [];

        public ForegroundWindowReading? Window { get; set; }

        public IReadOnlyList<DisplayInfo> Displays() => List;

        public IReadOnlyList<Rectangle> Gauges() => Shown;

        public ForegroundWindowReading? Foreground(IReadOnlyList<DisplayInfo> displays) => Window;
    }

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
