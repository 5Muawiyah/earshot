using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Streaming;
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

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    private static (WidgetCardPresenter Presenter, WidgetCard Card, TestTimeProvider Time) Open(bool animations)
    {
        var log = new CapturingLog();
        var time = new TestTimeProvider();
        WidgetCard? card = null;
        var presenter = new WidgetCardPresenter(
            () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, time, log, host: null, animationsEnabled: () => animations);
        presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
        Application.DoEvents();
        return (presenter, card!, time);
    }

    [TestMethod]
    public void ACardWithMotionStartsOutAndInvisibleAndEndsAtRestAndOpaque()
    {
        Phase5.CardDesktop.Run(() =>
        {
            (WidgetCardPresenter presenter, WidgetCard card, TestTimeProvider time) = Open(animations: true);
            using (presenter)
            {
                Assert.IsTrue((Phase5.TestWindows.ExtendedStyle(card.Handle) & WsExLayered) != 0, "The layered style is what the fade needs.");
                Assert.IsTrue(card.IsMoving);
                Rectangle rest = card.RestBounds;
                int travel = CardMotion.TravelFor(CardKit.Gauge, Earshot.Widget.SystemDisplaySource.WorkAreaFor(CardKit.Gauge), 96);
                Assert.AreEqual(rest.Y + travel, card.Bounds.Y, "The window starts one travel away from rest.");
                Assert.AreEqual(rest.X, card.Bounds.X);
                Assert.AreEqual(0, AlphaOf(card), "And invisible.");

                time.Advance(Ms(16));
                MotionFrame expected = CardMotion.FrameAt(CardMotion.EnterFromBelow(travel), Ms(16));
                Assert.AreEqual(rest.Y + expected.OffsetPx, card.Bounds.Y, "The window is where the curve puts it at 16 ms.");
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
            (WidgetCardPresenter presenter, WidgetCard card, TestTimeProvider time) = Open(animations: true);
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
            (WidgetCardPresenter presenter, WidgetCard card, TestTimeProvider time) = Open(animations: true);
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
            (WidgetCardPresenter presenter, WidgetCard card, TestTimeProvider time) = Open(animations: false);
            using (presenter)
            {
                Assert.IsTrue(presenter.IsShown);
                Assert.IsFalse(card.IsMoving, "Nothing moves.");
                Assert.AreEqual(card.RestBounds, card.Bounds, "Shown at rest in one step.");
                Assert.AreEqual(255, AlphaOf(card), "Opaque in one step.");
                Assert.AreEqual(1, time.LiveTimers, "Only the read line's own timer: the animator makes none.");

                presenter.Hide();
                Application.DoEvents();
                Assert.IsFalse(card.Visible, "Hidden at once.");
                Assert.IsFalse(card.IsExiting);
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
    public void TheTravelIsOneTaskbarThicknessAwayFromTheTaskbarEdge()
    {
        // A bottom taskbar 48 px thick on a 1080 px display: the work area ends at 1032 and the gauge is centred on the
        // taskbar's short side.
        var work = new Rectangle(0, 0, 1920, 1032);
        var gauge = new Rectangle(1700, 1036, 74, 40);
        Assert.AreEqual(48, CardMotion.TravelFor(gauge, work, 96), "Down, one taskbar thickness.");

        // The same at 150%: a 72 px taskbar.
        var work150 = new Rectangle(0, 0, 2880, 1728 - 72);
        var gauge150 = new Rectangle(2500, 1656 + 6, 111, 60);
        Assert.AreEqual(72, CardMotion.TravelFor(gauge150, work150, 144));

        // A top taskbar: the work area starts below the taskbar and the card leaves upward.
        var topWork = new Rectangle(0, 48, 1920, 1032);
        var topGauge = new Rectangle(1700, 4, 74, 40);
        Assert.AreEqual(-48, CardMotion.TravelFor(topGauge, topWork, 96));

        // No gauge, or one inside the work area (an auto-hidden taskbar): the fallback, down.
        Assert.AreEqual(48, CardMotion.TravelFor(Rectangle.Empty, work, 96));
        Assert.AreEqual(60, CardMotion.TravelFor(new Rectangle(1700, 900, 74, 40), work, 120));
    }
}
