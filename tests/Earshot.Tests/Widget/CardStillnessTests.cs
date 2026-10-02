using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// WINDOWS ONLY: these put the real card on a private desktop (Phase5.CardDesktop.Run), so they cannot run on the Linux build
// VM, where they fail at the first window call. The logic they rely on is held by pure tests that do run there:
// ReadLineStillnessTests (the read line does not tick), AccentColourServiceTests (a repeat of the same shades raises nothing),
// CardDpiLatchTests (the scale is fixed at the show) and GaugePushKeyTests (the gauge's tooltip is not a push).
//
// "The open card twitches or redraws about every second." A scripted minute of the card as it really lives, in fake time:
//   - the linked pair's messages every 1.7 s, each with a read time a varying latency behind the clock (0, 300, 700, 900 ms),
//     the two buds' messages alternating;
//   - the case open, so a message of the set about every 250 ms: the presenter is asked to refresh at that cadence;
//   - the presenter's own timers firing (its five second read-line timer);
//   - once a second, a change of look that is not a change (ReapplyLook), the accent service raising Changed with the same
//     shades, and the host's scale reading 96 and 144 in turn (the taskbar poll rewrites it every second);
// with the gauge at (900, 732, 74, 32) and the work area (0, 0, 1024, 728), the size of the screen a hosted test machine has.
//
// Nothing the owner can see changes in that minute, so the card must make no window call at all: it is not moved, resized,
// placed, shown, raised, given a DWM attribute again, faded, or invalidated, whole or in part. A second test changes one figure
// and holds that only the region of that figure is invalidated.
[TestClass]
public sealed class CardStillnessTests
{
    private const uint WmNcActivate = 0x0086;
    private const uint WmStyleChanged = 0x007D;

    private static readonly Rectangle GaugeRect = new(900, 732, 74, 32);
    private static readonly Rectangle WorkArea = new(0, 0, 1024, 728);

    private static readonly int[] LatenciesMs = [0, 300, 700, 900];

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

    private sealed class Rig : IDisposable
    {
        private int _hostDpi = 96;

        public Rig()
        {
            Snapshot = Message(0, left: 70, right: 70);
            WidgetCardPresenterCallbacks callbacks = CardKit.Callbacks() with { CurrentSnapshot = () => Snapshot, Dpi = () => _hostDpi };
            Presenter = new WidgetCardPresenter(
                () =>
                {
                    Card = new WidgetCard(Log);
                    Card.AttachAccent(Accent);
                    Card.CallRecorder = Calls.Add;
                    return Card;
                },
                callbacks, CardKit.Inline, Time, Log, host: null, animationsEnabled: null, workAreaFor: _ => WorkArea);
        }

        public CapturingLog Log { get; } = new();

        public TestTimeProvider Time { get; } = new();

        public FakeAccent Accent { get; } = new();

        public List<CardWindowCall> Calls { get; } = [];

        public WidgetSnapshot Snapshot { get; set; }

        // The left bud's figure the script's messages carry.
        public int LeftPercent { get; set; } = 70;

        public WidgetCard? Card { get; private set; }

        public WidgetCardPresenter Presenter { get; }

        // The pair's message heard now, its read time a latency behind the clock; each bud's own message is a little apart.
        public WidgetSnapshot Message(int index, int left, int right)
        {
            DateTimeOffset now = Time.GetUtcNow();
            DateTimeOffset readLeft = now - TimeSpan.FromMilliseconds(LatenciesMs[index % LatenciesMs.Length]);
            DateTimeOffset readRight = now - TimeSpan.FromMilliseconds(LatenciesMs[(index + 1) % LatenciesMs.Length]);
            return CardKit.Snapshot(new PartReading(left, false, null) { ReadAt = readLeft }, new PartReading(right, false, null) { ReadAt = readRight }, readLeft);
        }

        public void SetHostDpi(int dpi) => _hostDpi = dpi;

        public void Open()
        {
            Presenter.RequestShow(GaugeRect, GaugeRect.Location);
            Application.DoEvents();
            Time.Advance(TimeSpan.FromMilliseconds(400));
            Application.DoEvents();
            ShownBattery shown = Card!.Model.ShownParts;
            Assert.IsTrue(shown.Left.Fresh && shown.Right.Fresh, "Sanity: both figures are on the card.");
            Application.DoEvents();
            Calls.Clear();
        }

        public void Dispose() => Presenter.Dispose();
    }

    private static Rectangle Pending(WidgetCard card)
    {
        _ = GetUpdateRect(card.Handle, out Rect r, false);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    // One scripted second, in quarters: the case-open cadence of a refresh every 250 ms, a message every 1.7 s, and at the second
    // the look, accent and scale events that change nothing.
    private static void RunSeconds(Rig rig, int seconds, ref int messageIndex, ref double sinceMessage)
    {
        for (int s = 0; s < seconds; s++)
        {
            for (int quarter = 0; quarter < 4; quarter++)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(250));
                sinceMessage += 0.25;
                if (sinceMessage >= 1.7)
                {
                    sinceMessage -= 1.7;
                    messageIndex++;
                    rig.Snapshot = rig.Message(messageIndex, left: rig.LeftPercent, right: 70);
                }

                rig.Presenter.Refresh();
                Application.DoEvents();
            }

            rig.SetHostDpi(messageIndex % 2 == 0 ? 144 : 96);
            rig.Accent.Raise();
            rig.Presenter.ReapplyLook();
            Application.DoEvents();
        }
    }

    // The scripted minute with nothing visible changing: not one window call, and not one paint.
    [TestMethod]
    public void AMinuteOfMessagesTimersAndNonChangesMakesNoWindowCallAtAll()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.Open();
            WidgetCard card = rig.Card!;
            Rectangle bounds = card.Bounds;
            int darkSets = card.DarkModeApplications;
            int backdropSets = card.BackdropApplications;
            using var counter = new WindowMessageCounter(card.Handle);
            int index = 0;
            double since = 0;

            RunSeconds(rig, 60, ref index, ref since);

            Assert.IsEmpty(rig.Calls, "The card made window calls though nothing it draws changed: " + string.Join("; ", rig.Calls.Take(8)));
            Assert.AreEqual(bounds, card.Bounds, "Not moved or resized.");
            Assert.AreEqual(Rectangle.Empty, Pending(card), "No region left to repaint.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging), "No move, resize or z-order change reached the window manager.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanged));
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmShowWindow), "Not shown again.");
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmPaint), "Not painted.");
            Assert.AreEqual(0, counter.Count(WmNcActivate), "The frame was not told it changed activation.");
            Assert.AreEqual(0, counter.Count(WmStyleChanged), "No style was set again.");
            Assert.AreEqual(darkSets, card.DarkModeApplications, "The dark mode attribute was not set again.");
            Assert.AreEqual(backdropSets, card.BackdropApplications, "Nor the backdrop.");
            Assert.IsTrue(rig.Presenter.IsShown);
        });
    }

    // The same minute with the figure of the left bud really changing once: that column is invalidated, nothing is whole, and
    // nothing else is called.
    [TestMethod]
    public void OneFigureReallyChangingInvalidatesOnlyItsRegion()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.Open();
            WidgetCard card = rig.Card!;
            Rectangle bounds = card.Bounds;
            using var counter = new WindowMessageCounter(card.Handle);
            int index = 0;
            double since = 0;

            RunSeconds(rig, 30, ref index, ref since);
            Assert.IsEmpty(rig.Calls, "Sanity: the first half minute is still.");

            rig.LeftPercent = 71;
            rig.Snapshot = rig.Message(++index, left: rig.LeftPercent, right: 70);
            rig.Presenter.Refresh();
            Application.DoEvents();
            RunSeconds(rig, 5, ref index, ref since);

            Assert.IsNotEmpty(rig.Calls, "The changed figure was drawn.");
            long whole = (long)card.ClientRectangle.Width * card.ClientRectangle.Height;
            foreach (CardWindowCall call in rig.Calls)
            {
                Assert.AreEqual(CardWindowCallKind.Invalidate, call.Kind, "Only invalidations: " + call);
                Assert.AreNotEqual(card.ClientRectangle, call.Area, "Never the whole card: " + call);
                Assert.IsLessThan(whole / 3, (long)call.Area.Width * call.Area.Height, "Only the left column: " + call);
            }

            Assert.AreEqual(bounds, card.Bounds);
            Assert.AreEqual(0, counter.Count(WindowMessageCounter.WmWindowPosChanging));
        });
    }

    // Windows only: a card that is open keeps the scale it was shown at while the host's scale moves back and forth.
    [TestMethod]
    public void TheHostsScaleMovingBackAndForthDoesNotResizeAnOpenCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new Rig();
            rig.Open();
            WidgetCard card = rig.Card!;
            Size size = card.ClientSize;

            for (int i = 0; i < 20; i++)
            {
                rig.SetHostDpi(i % 2 == 0 ? 144 : 96);
                rig.Presenter.Refresh();
                Application.DoEvents();
            }

            Assert.AreEqual(size, card.ClientSize);
            Assert.IsEmpty(rig.Calls);
        });
    }
}
