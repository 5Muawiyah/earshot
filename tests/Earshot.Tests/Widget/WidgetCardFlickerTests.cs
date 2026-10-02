using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Tests.Streaming;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The widget card flashing while it is open, and on the update page. The card paints its own pixels over a translucent
// backdrop, so a repaint that clears the window and then draws on it shows the backdrop alone for as long as the drawing
// takes: a flash. The owner's card was drawn again for every status update (about thirty-five broadcast messages a minute,
// the five-second read-line timer, every settings change message Windows sends) and for every percent of a download, each
// time from the clear. Held here, with the real card on a private desktop and the updates fed at the real cadence:
//   - an update that changes nothing the card draws touches nothing: no invalid region, no paint, no placement;
//   - an update that changes a few pixels invalidates those pixels and nothing else;
//   - update progress redraws the bar and its figure and nothing else, and never places the card again;
//   - the card closes without a frame of itself at full opacity.
[TestClass]
public sealed class WidgetCardFlickerTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUpdateRect(nint window, out Rect rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    // The bounding box of what the window has been told to repaint and has not yet: empty when nothing is pending.
    private static Rectangle PendingPaint(WidgetCard card)
    {
        _ = GetUpdateRect(card.Handle, out Rect r, false);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static long Area(Rectangle r) => (long)r.Width * r.Height;

    private static WidgetSnapshot Snapshot(int left, int right, bool charging = false, DateTimeOffset? readAt = null) =>
        CardKit.Snapshot(new PartReading(left, charging, null), new PartReading(right, false, null), readAt ?? DateTimeOffset.UtcNow);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeCardHost? host = null, bool animations = false, WidgetSnapshot? first = null)
        {
            Snapshot = first ?? WidgetCardFlickerTests.Snapshot(70, 70, readAt: Time.GetUtcNow());
            Host = host;
            var callbacks = CardKit.Callbacks() with { CurrentSnapshot = () => Snapshot };
            Presenter = new WidgetCardPresenter(
                () => Card = new WidgetCard(Log), callbacks, CardKit.Inline, Time, Log, host, animations ? () => true : null,
                frameClockFor: _ => Clock);
        }

        // The card's frames come from the display, not from the time provider: a fake 60 Hz display, which Advance moves with the time.
        public FakeVBlankClock Clock { get; } = new(60);

        public void Advance(TimeSpan by)
        {
            Time.Advance(by);
            Clock.RunUntil(Clock.Now + by);
        }

        public CapturingLog Log { get; } = new();

        public TestTimeProvider Time { get; } = new();

        public FakeCardHost? Host { get; }

        public WidgetSnapshot Snapshot { get; set; }

        public WidgetCard? Card { get; private set; }

        public WidgetCardPresenter Presenter { get; }

        public void OpenOnMain()
        {
            Presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            Advance(TimeSpan.FromMilliseconds(400));
            Application.DoEvents();
            ShownBattery shown = Card!.Model.ShownParts;
            Assert.IsTrue(shown.Left.Fresh && shown.Right.Fresh, "Sanity: both figures are drawn, so a repaint test is about a card that has figures on it.");
        }

        public void Dispose() => Presenter.Dispose();
    }

    // Thirty-five broadcast messages, five seconds of read-line ticks and a settings change message: none of them changes what
    // the card draws, so none of them touches the window.
    [TestMethod]
    public void UpdatesThatChangeNothingTheCardDrawsTouchNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.OpenOnMain();
            WidgetCard card = rig.Card!;
            using var counter = new WindowMessageCounter(card.Handle);
            Rectangle bounds = card.Bounds;

            for (int i = 0; i < 35; i++)
            {
                rig.Presenter.Refresh();
                Assert.AreEqual(Rectangle.Empty, PendingPaint(card), "Update " + i + " left a region to repaint though nothing changed.");
                Application.DoEvents();
            }

            rig.Presenter.ReapplyLook();
            Assert.AreEqual(Rectangle.Empty, PendingPaint(card), "A settings change message that is not a change of look left a region to repaint.");
            Application.DoEvents();

            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmPaint), "Nothing was painted.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging), "The card was not placed again.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmShowWindow), "Nor shown again.");
            Assert.AreEqual(bounds, card.Bounds);
        });
    }

    // A battery figure that changes invalidates its own column and nothing else, and the card is not placed again.
    [TestMethod]
    public void AChangedBatteryFigureRepaintsItsColumnAndNothingElse()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.OpenOnMain();
            WidgetCard card = rig.Card!;
            using var counter = new WindowMessageCounter(card.Handle);
            long whole = Area(card.ClientRectangle);

            for (int percent = 71; percent <= 80; percent++)
            {
                rig.Snapshot = Snapshot(percent, 70, readAt: rig.Time.GetUtcNow());
                rig.Presenter.Refresh();
                Rectangle pending = PendingPaint(card);
                Assert.IsTrue(Area(pending) > 0, percent + " %: the changed figure must be repainted.");
                Assert.IsLessThan(whole / 3, Area(pending), percent + " %: only the left column changed, not the whole card (pending " + pending + " of " + card.ClientSize + ").");
                Application.DoEvents();
            }

            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging), "The card was not placed again.");
        });
    }

    // The update page during a download: each percent redraws the bar and its figure.
    [TestMethod]
    public void UpdateProgressRedrawsTheBarAndItsFigureAndNeverPlacesOrAnimatesTheCardAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var host = new FakeCardHost { View = CardKit.Update(UpdateStage.Downloading, percent: 5) };
            using var rig = new Rig(host, animations: true);
            rig.Presenter.RequestUpdatePage(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            rig.Advance(TimeSpan.FromMilliseconds(400));
            Application.DoEvents();
            WidgetCard card = rig.Card!;
            Assert.AreEqual(WidgetCardView.Update, rig.Presenter.ViewForTest);
            Assert.IsFalse(card.IsMoving, "The entrance is over.");
            using var counter = new WindowMessageCounter(card.Handle);
            Rectangle rest = card.RestBounds;
            int moves = 0;
            int alphas = 0;
            Func<nint, int, int, bool> move = card.MoveWindow;
            Func<nint, byte, bool> alpha = card.SetWindowAlpha;
            card.MoveWindow = (h, x, y) =>
            {
                moves++;
                return move(h, x, y);
            };
            card.SetWindowAlpha = (h, a) =>
            {
                alphas++;
                return alpha(h, a);
            };
            Rectangle progress = card.CurrentSetupLayout!.Progress;
            Assert.IsFalse(progress.IsEmpty, "Sanity: the page has a progress row.");
            long whole = Area(card.ClientRectangle);

            for (int percent = 6; percent <= 60; percent++)
            {
                host.View = CardKit.Update(UpdateStage.Downloading, percent: percent);
                host.RaiseUpdateChanged();
                Rectangle pending = PendingPaint(card);
                Assert.IsTrue(Area(pending) > 0, percent + " %: the figure must be repainted.");
                Assert.IsLessThanOrEqualTo(Area(Rectangle.Inflate(progress, 4, 4)), Area(pending), percent + " %: only the progress row, not the page (pending " + pending + ", row " + progress + ").");
                Assert.IsLessThan(whole / 4, Area(pending));
                Application.DoEvents();
            }

            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging), "The card was not placed again.");
            Assert.AreEqual(0, moves, "No frame of a slide.");
            Assert.AreEqual(0, alphas, "No fade.");
            Assert.AreEqual(rest, card.RestBounds);
        });
    }

    // The same updates feed a card that is not asked to do anything about them: one placement and one entrance for the whole of it.
    [TestMethod]
    public void AnOpenCardThatIsGivenManyUpdatesIsPlacedOnceAndAnimatedOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig(animations: true);
            int moves = 0;
            int alphas = 0;
            rig.Presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
            WidgetCard card = rig.Card!;
            Func<nint, int, int, bool> move = card.MoveWindow;
            Func<nint, byte, bool> alpha = card.SetWindowAlpha;
            card.MoveWindow = (h, x, y) =>
            {
                moves++;
                return move(h, x, y);
            };
            card.SetWindowAlpha = (h, a) =>
            {
                alphas++;
                return alpha(h, a);
            };
            rig.Advance(TimeSpan.FromMilliseconds(400));
            Application.DoEvents();
            int entranceMoves = moves;
            int entranceAlphas = alphas;
            Assert.IsGreaterThan(0, entranceMoves, "Sanity: the entrance moved the card.");

            for (int i = 0; i < 60; i++)
            {
                rig.Snapshot = Snapshot(60 + (i % 7), 70, readAt: rig.Time.GetUtcNow());
                rig.Presenter.Refresh();
                rig.Advance(TimeSpan.FromMilliseconds(500));
                Application.DoEvents();
            }

            Assert.AreEqual(entranceMoves, moves, "No frame of any slide after the entrance.");
            Assert.AreEqual(entranceAlphas, alphas, "No fade after the entrance.");
            Assert.IsFalse(card.IsMoving);
        });
    }

    // The backdrop and the dark-mode attribute belong to the window, not to a show: closing and opening again, a look that did not
    // change and a settings change message set neither again.
    [TestMethod]
    public void TheBackdropAndTheDarkModeAreSetOncePerWindowWhateverTheShowsAndLookChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig(animations: true);
            rig.OpenOnMain();
            WidgetCard card = rig.Card!;
            int backdrop = card.BackdropApplications;
            int dark = card.DarkModeApplications;
            Assert.AreEqual(1, backdrop, "Set when the window was made.");

            for (int i = 0; i < 3; i++)
            {
                rig.Presenter.Hide();
                rig.Advance(TimeSpan.FromMilliseconds(400));
                Application.DoEvents();
                rig.Presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
                rig.Advance(TimeSpan.FromMilliseconds(400));
                Application.DoEvents();
                rig.Presenter.ReapplyLook();
            }

            Assert.IsTrue(rig.Presenter.IsShown, "Sanity: shown again.");
            Assert.AreEqual(backdrop, card.BackdropApplications, "The backdrop is not set again at each show.");
            Assert.AreEqual(dark, card.DarkModeApplications, "Nor the dark-mode attribute, when the theme is the same.");
        });
    }

    // A card that closes slides and fades out, and only then goes back to full opacity: while it is hidden, never while it is
    // still on screen, where it would be a frame of the whole card.
    [TestMethod]
    public void ACardThatClosesIsNeverMadeOpaqueWhileItIsStillOnScreen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig(animations: true);
            rig.OpenOnMain();
            WidgetCard card = rig.Card!;
            var opaqueWhileVisible = new List<string>();
            var calls = new List<string>();
            bool faded = false;
            Func<nint, byte, bool> alpha = card.SetWindowAlpha;
            card.SetWindowAlpha = (h, a) =>
            {
                calls.Add("alpha " + a + (card.Visible ? " visible" : " hidden"));
                faded |= a < 255;

                // The slide starts from full opacity; only a full opacity after the fade has begun is the card put back.
                if (a == 255 && faded && card.Visible)
                {
                    opaqueWhileVisible.Add("opaque while visible");
                }

                return alpha(h, a);
            };

            rig.Presenter.Hide();
            for (int i = 0; i < 30; i++)
            {
                rig.Advance(TimeSpan.FromMilliseconds(16));
                Application.DoEvents();
            }

            Assert.IsFalse(card.Visible, "Sanity: the card closed.");
            Assert.IsEmpty(opaqueWhileVisible, "The card was set to full opacity while still on screen, which is a frame of the whole card at the end of every close. Calls: " + string.Join(", ", calls.TakeLast(5)));
        });
    }
}
