using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Streaming;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real card, presenter and animator together on a private desktop, the clock a fake one: the window really
// starts out and faded, ends at rest and opaque, and leaves the same way. With animation effects off nothing moves.
[TestClass]
public sealed class WidgetCardMotionTests
{
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 2;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    private static byte AlphaOf(WidgetCard card)
    {
        Assert.IsTrue(GetLayeredWindowAttributes(card.Handle, out _, out byte alpha, out uint flags), "The card is a layered window.");
        Assert.AreEqual(LwaAlpha, flags & LwaAlpha, "Its opacity is a constant alpha.");
        return alpha;
    }

    // The presenter's own timers run on Time; the card's frames come from a fake 60 Hz display, Clock. Advance moves both.
    private sealed class MotionTime
    {
        public TestTimeProvider Time { get; } = new();

        public FakeVBlankClock Clock { get; } = new(60);

        public int LiveTimers => Time.LiveTimers;

        public void Advance(TimeSpan by)
        {
            Time.Advance(by);
            Clock.RunUntil(Clock.Now + by);
        }
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    private static (WidgetCardPresenter Presenter, WidgetCard Card, MotionTime Time) Open(bool animations)
    {
        var log = new CapturingLog();
        var time = new MotionTime();
        WidgetCard? card = null;
        var presenter = new WidgetCardPresenter(
            () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, time.Time, log, host: null, animationsEnabled: () => animations,
            frameClockFor: _ => time.Clock);
        presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
        Application.DoEvents();
        return (presenter, card!, time);
    }

    [TestMethod]
    public void ACardWithMotionStartsOutAndInvisibleAndEndsAtRestAndOpaque()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time) = Open(animations: true);
            using (presenter)
            {
                Assert.IsTrue((Phase5.TestWindows.ExtendedStyle(card.Handle) & WsExLayered) != 0, "The layered style is what the fade needs.");
                Assert.IsTrue(card.IsMoving);
                Rectangle rest = card.RestBounds;
                int travel = CardMotion.TravelFor(CardKit.Gauge, Earshot.Widget.SystemDisplaySource.WorkAreaFor(CardKit.Gauge), 96);
                Assert.AreEqual(rest.Y + travel, card.Bounds.Y, "The window starts one travel away from rest.");
                Assert.AreEqual(rest.X, card.Bounds.X);
                Assert.AreEqual(0, AlphaOf(card), "And invisible.");

                // The display's first blank, 1/60 s after the open.
                time.Advance(time.Clock.TimeOfBlank(1));
                MotionFrame expected = CardMotion.FrameAt(CardMotion.EnterFromBelow(travel), time.Clock.TimeOfBlank(1));
                Assert.AreEqual(rest.Y + expected.OffsetPx, card.Bounds.Y, "The window is where the curve puts it at the first blank.");
                Assert.AreEqual(expected.Alpha, AlphaOf(card));

                time.Advance(Ms(300));
                Assert.IsFalse(card.IsMoving, "The motion ends.");
                Assert.AreEqual(rest, card.Bounds, "At rest.");
                Assert.AreEqual(255, AlphaOf(card), "Opaque.");
            }
        });
    }

    [TestMethod]
    public void AClosingCardSlidesAndFadesAwayThenHidesAndIsLeftAtRestForTheNextOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time) = Open(animations: true);
            using (presenter)
            {
                time.Advance(Ms(300));
                Rectangle rest = card.RestBounds;

                presenter.Hide();
                Application.DoEvents();
                Assert.IsTrue(card.Visible, "Still on screen while it leaves.");
                Assert.IsFalse(presenter.IsShown, "But no longer counted as shown.");
                Assert.IsTrue(card.IsExiting);

                time.Advance(Ms(100));
                Assert.IsTrue(card.Visible);
                Assert.IsTrue(card.Bounds.Y > rest.Y, "Sliding down.");
                Assert.IsTrue(AlphaOf(card) < 255, "Fading.");

                time.Advance(Ms(100));
                Assert.IsFalse(card.Visible, "Hidden once the 167 ms are up.");
                Assert.IsFalse(card.IsExiting);
                Assert.AreEqual(rest, card.Bounds, "Put back at rest.");
                Assert.AreEqual(255, AlphaOf(card), "And opaque, ready for the next entrance.");
            }
        });
    }

    [TestMethod]
    public void AnOpenWhileTheCardIsLeavingBringsItBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time) = Open(animations: true);
            using (presenter)
            {
                time.Advance(Ms(300));
                presenter.Hide();
                time.Advance(Ms(60));
                Assert.IsTrue(card.IsExiting);

                presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
                Application.DoEvents();
                Assert.IsTrue(presenter.IsShown, "A second open while it leaves brings it back.");
                Assert.IsFalse(card.IsExiting);

                time.Advance(Ms(400));
                Assert.IsTrue(card.Visible, "The cancelled exit never hides it.");
                Assert.AreEqual(card.RestBounds, card.Bounds);
                Assert.AreEqual(255, AlphaOf(card));
            }
        });
    }

    [TestMethod]
    public void WithAnimationEffectsOffTheCardAppearsAtRestAtOnceAndHidesAtOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time) = Open(animations: false);
            using (presenter)
            {
                Assert.IsTrue(presenter.IsShown);
                Assert.IsFalse(card.IsMoving, "Nothing moves.");
                Assert.AreEqual(card.RestBounds, card.Bounds, "Shown at rest in one step.");
                Assert.AreEqual(255, AlphaOf(card), "Opaque in one step.");
                Assert.AreEqual(1, time.LiveTimers, "Only the read line's own timer: the animator makes none.");
                Assert.AreEqual(0, time.Clock.SubscribeCalls, "And asks for no frame.");

                presenter.Hide();
                Application.DoEvents();
                Assert.IsFalse(card.Visible, "Hidden at once.");
                Assert.IsFalse(card.IsExiting);
            }
        });
    }

    // ----- a window call of the motion fails -----

    private static (WidgetCardPresenter Presenter, WidgetCard Card, MotionTime Time, CapturingLog Log, int[] Toggles) OpenWith(
        Action<WidgetCard> arrange, FakeCardHost? host = null, bool show = true)
    {
        var log = new CapturingLog();
        var time = new MotionTime();
        var toggles = new int[1];
        WidgetCardPresenterCallbacks callbacks = CardKit.Callbacks() with { RequestToggle = _ => toggles[0]++, CurrentIntent = () => new ToggleIntent(true, Guid.NewGuid(), "AirPods Pro 3") };
        WidgetCard? card = null;
        var presenter = new WidgetCardPresenter(
            () =>
            {
                card = new WidgetCard(log);
                arrange(card);
                return card;
            }, callbacks, CardKit.Inline, time.Time, log, host: host, animationsEnabled: () => true, frameClockFor: _ => time.Clock);
        if (show)
        {
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
        }

        return (presenter, card!, time, log, toggles);
    }

    // The first call to the window fails with ERROR_ACCESS_DENIED (5), then the real call is made again.
    private static Func<nint, int, int, bool> MoveFailingOnce(WidgetCard card)
    {
        Func<nint, int, int, bool> real = card.MoveWindow;
        int calls = 0;
        return (handle, x, y) =>
        {
            if (++calls == 1)
            {
                Marshal.SetLastPInvokeError(5);
                return false;
            }

            return real(handle, x, y);
        };
    }

    [TestMethod]
    public void AnEntranceWhoseFirstWindowCallFailsLeavesTheCardShownAtRestAndOpaqueNotInvisible()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, CapturingLog log, _) = OpenWith(c => c.MoveWindow = MoveFailingOnce(c));
            using (presenter)
            {
                Assert.IsTrue(card.Visible, "The card is on screen.");
                Assert.IsFalse(card.IsMoving, "Its motion was given up, not left running.");
                Assert.AreEqual(card.RestBounds, card.Bounds, "At rest.");
                Assert.AreEqual(255, AlphaOf(card), "Opaque: a layered window whose opacity was never set is not drawn at all.");
                Assert.IsTrue(log.Has(LogLevel.Warn, "set-window-pos"), "The raw code is in the log.");
                Assert.IsTrue(log.Has(LogLevel.Warn, "ERROR_ACCESS_DENIED"));

                time.Advance(Ms(500));
                Assert.AreEqual(255, AlphaOf(card), "Nothing changes it afterwards.");
                Assert.AreEqual(card.RestBounds, card.Bounds);

                presenter.Hide();
                Application.DoEvents();
                Assert.IsFalse(card.Visible, "From then on it hides in one step, as a card with no motion does.");
                Assert.IsFalse(card.IsExiting);
            }
        });
    }

    [TestMethod]
    public void AnExitWhoseFirstWindowCallFailsStillHidesTheCardAndLeavesItAtRestAndOpaque()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Func<nint, int, int, bool>? real = null;
            bool failNext = false;
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, _, _) = OpenWith(c =>
            {
                real = c.MoveWindow;
                c.MoveWindow = (handle, x, y) =>
                {
                    if (failNext)
                    {
                        failNext = false;
                        Marshal.SetLastPInvokeError(5);
                        return false;
                    }

                    return real(handle, x, y);
                };
            });
            using (presenter)
            {
                time.Advance(Ms(300));
                Rectangle rest = card.RestBounds;
                failNext = true;

                presenter.Hide();
                Application.DoEvents();

                Assert.IsFalse(card.Visible, "The exit could not slide, so it hid the card instead of leaving it on screen.");
                Assert.IsFalse(card.IsExiting, "And it is not left exiting.");
                Assert.IsFalse(card.IsMoving);
                Assert.AreEqual(rest, card.Bounds, "At rest for the next show.");
                Assert.AreEqual(255, AlphaOf(card));
            }
        });
    }

    // The window call that puts the card back at rest when it has gone is checked too, and its raw code logged.
    [TestMethod]
    public void AFailureToPutTheHiddenCardBackAtRestIsLoggedWithItsRawCode()
    {
        Phase5.CardDesktop.Run(() =>
        {
            Func<nint, int, int, bool>? real = null;
            int restY = int.MinValue;
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, CapturingLog log, _) = OpenWith(c =>
            {
                real = c.MoveWindow;
                c.MoveWindow = (handle, x, y) =>
                {
                    // Only the placement at rest, which is the one call with no offset.
                    if (y == restY)
                    {
                        Marshal.SetLastPInvokeError(5);
                        return false;
                    }

                    return real(handle, x, y);
                };
            });
            using (presenter)
            {
                time.Advance(Ms(300));
                restY = card.RestBounds.Y;

                presenter.Hide();
                time.Advance(Ms(300));

                Assert.IsFalse(card.Visible, "The card was hidden all the same.");
                Assert.IsTrue(log.Has(LogLevel.Warn, "set-window-pos:widget-card-rest"), "The result of the window call was recorded.");
                Assert.IsTrue(log.Has(LogLevel.Warn, "ERROR_ACCESS_DENIED"));
            }
        });
    }

    [TestMethod]
    public void AFailedFadeThatCannotBeUndoneGivesTheLayeredStyleUpSoTheCardIsDrawn()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, CapturingLog log, _) = OpenWith(c =>
            {
                Func<nint, byte, bool> real = c.SetWindowAlpha;
                c.SetWindowAlpha = (handle, alpha) =>
                {
                    if (alpha == 255)
                    {
                        Marshal.SetLastPInvokeError(5);
                        return false;
                    }

                    return real(handle, alpha);
                };
            });
            using (presenter)
            {
                time.Advance(Ms(400));

                Assert.IsTrue(card.Visible, "The card is still on screen.");
                Assert.AreEqual(0, Phase5.TestWindows.ExtendedStyle(card.Handle) & WsExLayered, "It is no longer a layered window, which needs an opacity to be drawn at all.");
                Assert.IsFalse(card.IsMoving);
                Assert.IsTrue(log.Has(LogLevel.Warn, "gives up its fade"), "The raw code and what the card did about it are in the log.");
                Assert.IsTrue(log.Has(LogLevel.Warn, "set-layered-window-attributes"));
            }
        });
    }

    // ----- a card that is leaving answers nothing -----

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static void ClickAt(nint handle, Point point)
    {
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, MakeLParam(point.X, point.Y));
        Phase5.TestWindows.Send(handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(point.X, point.Y));
    }

    private const int WmKeyDown = 0x0100;

    [TestMethod]
    public void AClickOrAKeyOnACardThatIsFadingOutDoesNothingWhileTheSameClickBeforeItDoes()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, _, int[] toggles) = OpenWith(_ => { });
            using (presenter)
            {
                time.Advance(Ms(300));
                Rectangle button = card.CurrentMainLayout.Button;
                var centre = new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2));

                // A button down before the exit begins, and its up after: the up must not complete a press on a card that was closed.
                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, MakeLParam(centre.X, centre.Y));
                presenter.Hide();
                Application.DoEvents();
                Assert.IsTrue(card.IsExiting);
                Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, MakeLParam(centre.X, centre.Y));
                ClickAt(card.Handle, centre);
                Phase5.TestWindows.Send(card.Handle, WmKeyDown, (nint)Keys.Enter, 0);
                Phase5.TestWindows.Send(card.Handle, WmKeyDown, (nint)Keys.Space, 0);

                Assert.AreEqual(0, toggles[0], "Nothing on a card that is fading out connects or disconnects.");
            }
        });
    }

    [TestMethod]
    public void TheSameClickOnACardThatIsNotLeavingIsAnswered()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, _, int[] toggles) = OpenWith(_ => { });
            using (presenter)
            {
                time.Advance(Ms(300));
                Rectangle button = card.CurrentMainLayout.Button;

                ClickAt(card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));

                Assert.AreEqual(1, toggles[0], "The control for the test above: the same click is a real press when the card is not leaving.");
            }
        });
    }

    // ----- a card that is leaving is neither drawn again nor resized -----

    [TestMethod]
    public void ARefreshOrALookChangeWhileTheSettingsPageFadesOutDrawsNothingAndResizesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost();
            (WidgetCardPresenter presenter, WidgetCard card, MotionTime time, _, _) = OpenWith(_ => { }, host);
            using (presenter)
            {
                time.Advance(Ms(300));
                presenter.OpenSettingsForTest();
                Application.DoEvents();
                Assert.AreEqual(WidgetCardView.Settings, presenter.CurrentModelForTest!.View);
                Size sizeBefore = card.ClientSize;
                Rectangle restBefore = card.RestBounds;
                int reappliesBefore = presenter.LookReappliesForTest;

                presenter.Hide();
                Application.DoEvents();
                Assert.IsTrue(card.IsExiting);
                presenter.Refresh();
                presenter.ReapplyLook();
                Application.DoEvents();

                Assert.AreEqual(WidgetCardView.Settings, presenter.CurrentModelForTest!.View, "The card still shows the page it was closed on.");
                Assert.AreEqual(sizeBefore, card.ClientSize, "It was not resized to another page's height.");
                Assert.AreEqual(restBefore, card.RestBounds);
                Assert.AreEqual(reappliesBefore, presenter.LookReappliesForTest, "A card that is leaving is not given the look again.");
            }
        });
    }

    [TestMethod]
    public void ACardWithNoMotionAttachedIsAnOrdinaryWindowWithNoLayeredStyle()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new TestTimeProvider(), log);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();

            Assert.IsTrue((Phase5.TestWindows.ExtendedStyle(card!.Handle) & WsExLayered) == 0);
            Assert.IsFalse(card.IsMoving);
            Assert.AreEqual(card.RestBounds, card.Bounds);
            presenter.Hide();
            Application.DoEvents();
            Assert.IsFalse(card.Visible, "Hidden at once, as before there was motion.");
        });
    }

    [TestMethod]
    public void TheRealAnimationEffectsSettingCanBeRead()
    {
        var log = new CapturingLog();
        _ = new SystemAnimationSetting(log).Enabled();
        Assert.IsFalse(log.Has(LogLevel.Warn, "could not be read"), "SystemParametersInfo answered, whichever way the setting is.");
    }

    [TestMethod]
    public void TheTravelIs40ScaledAwayFromTheTaskbarEdge()
    {
        // A bottom taskbar 48 px thick on a 1080 px display: the work area ends at 1032 and the gauge is centred on the
        // taskbar's short side.
        var work = new Rectangle(0, 0, 1920, 1032);
        var gauge = new Rectangle(1700, 1036, 74, 40);
        Assert.AreEqual(40, CardMotion.TravelFor(gauge, work, 96), "Down, 40 px.");

        // The same at 150%: 60 px.
        var work150 = new Rectangle(0, 0, 2880, 1728 - 72);
        var gauge150 = new Rectangle(2500, 1656 + 6, 111, 60);
        Assert.AreEqual(60, CardMotion.TravelFor(gauge150, work150, 144));

        // A top taskbar: the work area starts below the taskbar and the card leaves upward.
        var topWork = new Rectangle(0, 48, 1920, 1032);
        var topGauge = new Rectangle(1700, 4, 74, 40);
        Assert.AreEqual(-40, CardMotion.TravelFor(topGauge, topWork, 96));

        // No gauge, or one inside the work area (an auto-hidden taskbar): down.
        Assert.AreEqual(40, CardMotion.TravelFor(Rectangle.Empty, work, 96));
        Assert.AreEqual(50, CardMotion.TravelFor(new Rectangle(1700, 900, 74, 40), work, 120));
    }
}
