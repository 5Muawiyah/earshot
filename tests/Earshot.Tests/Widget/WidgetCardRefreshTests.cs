using System.Drawing;
using System.Windows.Forms;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The refresh icon on the card and the presenter's refresh flow: the layout of the icon, how it is drawn and turned,
// how it is reached from the keyboard and the mouse, and what the card says while a refresh reads and after one that
// found nothing.
[TestClass]
public sealed class WidgetCardRefreshTests
{
    // ---- The layout

    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    public void TheRefreshIconSitsLeftOfTheGearInTheSameRowAtTheSameSize(int dpi)
    {
        WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(dpi, showSwitch: false);

        Assert.IsFalse(layout.Refresh.IsEmpty);
        Assert.AreEqual(layout.Gear.Size, layout.Refresh.Size);
        Assert.AreEqual(layout.Gear.Top, layout.Refresh.Top);
        Assert.IsLessThanOrEqualTo(layout.Gear.Left, layout.Refresh.Right, "The icon does not overlap the gear.");
        Assert.IsLessThanOrEqualTo(layout.Refresh.Left, layout.Title.Right, "The title does not run under the icon.");
    }

    [TestMethod]
    public void TheRefreshIconIsAbsentWhenTheViewHasNoIconRowOrNoGear()
    {
        Assert.IsTrue(WidgetCardLayout.Compute(96, showSwitch: false, showRefresh: false).Refresh.IsEmpty);
        Assert.IsTrue(WidgetCardLayout.Compute(96, showSwitch: false, showGear: false).Refresh.IsEmpty, "It never stands without the gear.");
    }

    // ---- The words

    [TestMethod]
    public void TheReadLineSaysReadingWhileARefreshReadsAndWhyItFoundNothingAfterwards()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.MainModel() with { Refresh = BatteryRefreshView.Started }, 96);
            Assert.AreEqual("Reading the battery", card.ReadLineText);

            card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.NothingHeard) }, 96);
            Assert.AreEqual("Nothing heard. Open the case", card.ReadLineText);

            card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.BluetoothOff) }, 96);
            Assert.AreEqual("Bluetooth is off", card.ReadLineText);

            card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.NotListening) }, 96);
            Assert.AreEqual("Not listening", card.ReadLineText);

            card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(false, 0, BatteryRefreshOutcome.Heard) }, 96);
            Assert.AreEqual(WidgetCopy.BatteryNotReadYet, card.ReadLineText, "A refresh that heard something leaves the ordinary line, which is now about the new reading.");
        });
    }

    // ---- The picture

    [TestMethod]
    public void TheRefreshIconHasInkAndTurnsWhileReading()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (bool dark in CardKit.Themes)
            {
                using WidgetCard card = CardKit.NewCard(dark);
                card.Render(CardKit.MainModel(), 96);
                using Bitmap resting = CardKit.Render(card);
                Rectangle icon = card.CurrentMainLayout.Refresh;
                Color background = resting.GetPixel(0, 0);
                Assert.IsTrue(CardKit.HasInk(resting, icon, background), "The icon is drawn at rest.");

                card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(true, 0) }, 96);
                using Bitmap frame0 = CardKit.Render(card);
                card.Render(CardKit.MainModel() with { Refresh = new BatteryRefreshView(true, 3) }, 96);
                using Bitmap frame3 = CardKit.Render(card);

                Assert.IsTrue(CardKit.HasInk(frame3, icon, background), "It still has ink while it turns.");
                Assert.IsFalse(SameRegion(frame0, frame3, icon), "A third of a turn on, the icon is somewhere else.");
            }
        });
    }

    // A refresh that ended with nothing to show marks the read line with the caution triangle in the caution colour in
    // the clock's place; the clock and a refresh under way keep the neutral ink.
    [TestMethod]
    public void AFinishedRefreshThatFoundNothingMarksTheReadLineWithACautionIconAndTheOrdinaryLineDoesNot()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (bool dark in CardKit.Themes)
            {
                using WidgetCard card = CardKit.NewCard(dark);
                BatteryRefreshView?[] views =
                [
                    null,
                    BatteryRefreshView.Started,
                    new(false, 0, BatteryRefreshOutcome.Heard),
                    new(false, 0, BatteryRefreshOutcome.NothingHeard),
                    new(false, 0, BatteryRefreshOutcome.BluetoothOff),
                    new(false, 0, BatteryRefreshOutcome.NotListening),
                ];
                bool[] expected = [false, false, false, true, true, true];
                for (int i = 0; i < views.Length; i++)
                {
                    card.Render(CardKit.MainModel() with { Refresh = views[i] }, 96);
                    using Bitmap bitmap = CardKit.Render(card);
                    Rectangle line = card.CurrentMainLayout.ReadLine;
                    var icon = new Rectangle(line.X, line.Y, 16, line.Height);
                    Assert.AreEqual(expected[i], HasSaturatedInk(bitmap, icon), "Refresh view " + i + (dark ? " (dark)" : " (light)") + ": caution icon expected " + expected[i] + ".");
                }
            }
        });
    }

    // Caution is amber in both themes; the clock and the text are neutral greys, so a pixel with a wide spread between its
    // channels is the caution icon.
    private static bool HasSaturatedInk(Bitmap bitmap, Rectangle rect)
    {
        Rectangle bounds = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > 60)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SameRegion(Bitmap a, Bitmap b, Rectangle rect)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---- The keyboard and the mouse

    [TestMethod]
    public void TheRefreshIconIsTheLastStopAfterTheGearAndEnterActivatesItWithoutClosingTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(CardKit.MainModel(), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            int requests = 0;
            card.RefreshRequested += (_, _) => requests++;

            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Gear, card.FocusTarget);
            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Refresh, card.FocusTarget);
            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Enter, 0);

            Assert.AreEqual(1, requests);
            Assert.IsTrue(card.Visible, "A refresh happens on this card; it stays.");

            Phase5.TestWindows.Send(card.Handle, 0x0100, (nint)Keys.Tab, 0);
            Assert.AreEqual(WidgetCardFocus.Button, card.FocusTarget, "Tab cycles back to the button.");
        });
    }

    [TestMethod]
    public void AClickOnTheRefreshIconRaisesARefreshRequestAndNothingElse()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(CardKit.MainModel(), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            var raised = new List<string>();
            card.RefreshRequested += (_, _) => raised.Add("Refresh");
            card.SettingsRequested += (_, _) => raised.Add("Gear");
            card.ToggleRequested += (_, _) => raised.Add("Connect");

            CardKit.Click(card, card.CurrentMainLayout.Refresh);

            Assert.HasCount(1, raised);
            Assert.AreEqual("Refresh", raised[0]);
            Assert.IsTrue(card.Visible);
        });
    }

    [TestMethod]
    public void ThePressOnTheRefreshIconThatLostCaptureDoesNotRefresh()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var card = new WidgetCard(new CapturingLog());
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(CardKit.MainModel(), 96);
            card.Location = new Point(50, 50);
            card.Show();
            card.Activate();
            Application.DoEvents();
            int requests = 0;
            card.RefreshRequested += (_, _) => requests++;
            Rectangle rect = card.CurrentMainLayout.Refresh;
            nint lParam = (nint)((((rect.Y + (rect.Height / 2)) & 0xFFFF) << 16) | ((rect.X + (rect.Width / 2)) & 0xFFFF));

            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
            Phase5.TestWindows.Send(card.Handle, 0x0215, 0, 0); // WM_CAPTURECHANGED
            Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);

            Assert.AreEqual(0, requests);
        });
    }

    [TestMethod]
    public void TheCaseOpenNoticeHasNoRefreshIcon()
    {
        Phase5.CardSta.Run(() =>
        {
            using var notice = new WidgetCard(new CapturingLog(), notice: true);
            notice.Render(CardKit.MainModel(), 96);

            Assert.IsTrue(notice.CurrentMainLayout.Refresh.IsEmpty);
        });
    }

    // ---- The presenter's flow

    private sealed class Reader
    {
        public int Calls { get; private set; }

        public CancellationToken Token { get; private set; }

        public TaskCompletionSource<BatteryRefreshOutcome> Result { get; } = new();

        public Task<BatteryRefreshOutcome> Read(CancellationToken ct)
        {
            Calls++;
            Token = ct;
            return Result.Task;
        }
    }

    private sealed class Flow : IDisposable
    {
        public Flow(Reader reader, WidgetSnapshot? snapshot = null)
        {
            Reader = reader;
            Snapshot = snapshot ?? CardKit.Snapshot();
            Log = new CapturingLog();
            Time = new Streaming.TestTimeProvider();
            Presenter = new WidgetCardPresenter(
                () => Card = new WidgetCard(Log),
                CardKit.Callbacks(snapshot: null) with { CurrentSnapshot = () => Snapshot, RefreshBattery = reader.Read },
                CardKit.Inline, Time, Log);
        }

        public Reader Reader { get; }

        public WidgetSnapshot Snapshot { get; set; }

        public CapturingLog Log { get; }

        public Streaming.TestTimeProvider Time { get; }

        public WidgetCardPresenter Presenter { get; }

        public WidgetCard? Card { get; private set; }

        public void ShowCard()
        {
            Presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();
        }

        public void Dispose() => Presenter.Dispose();
    }

    [TestMethod]
    public void TheIconTurnsWhileTheServiceReadsAndStopsWhenItAnswers()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            flow.ShowCard();
            Assert.IsNull(flow.Presenter.RefreshViewForTest);

            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);

            Assert.AreEqual(1, reader.Calls);
            Assert.IsTrue(flow.Presenter.RefreshViewForTest!.Reading);
            Assert.IsTrue(flow.Presenter.RefreshSpinnerRunningForTest, "The icon turns while it reads.");
            Assert.AreEqual("Reading the battery", flow.Card.ReadLineText);

            flow.Time.Advance(WidgetCardPresenter.SpinnerInterval);
            Assert.AreEqual(1, flow.Presenter.CurrentModelForTest!.Refresh!.SpinFrame);
            flow.Time.Advance(WidgetCardPresenter.SpinnerInterval);
            Assert.AreEqual(2, flow.Presenter.CurrentModelForTest!.Refresh!.SpinFrame);

            reader.Result.SetResult(BatteryRefreshOutcome.Heard);

            Assert.IsFalse(flow.Presenter.RefreshSpinnerRunningForTest, "It stops when the refresh ends.");
            Assert.IsNull(flow.Presenter.CurrentModelForTest!.Refresh, "A refresh that heard something says nothing of its own.");
        });
    }

    [TestMethod]
    public void ASecondRequestWhileOneIsReadingJoinsItInsteadOfStartingAnother()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            flow.ShowCard();

            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);
            CardKit.Click(flow.Card, flow.Card.CurrentMainLayout.Refresh);

            Assert.AreEqual(1, reader.Calls);
        });
    }

    [TestMethod]
    public void ARefreshThatHeardNothingSaysSoUntilANewerReadingArrivesOrTheCardCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            flow.ShowCard();
            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);

            reader.Result.SetResult(BatteryRefreshOutcome.NothingHeard);
            Assert.AreEqual("Nothing heard. Open the case", flow.Card.ReadLineText);

            // Nothing newer than the refresh itself: the words stay through a redraw.
            flow.Presenter.Refresh();
            Assert.AreEqual("Nothing heard. Open the case", flow.Card.ReadLineText);

            // A reading taken after the refresh began supersedes the words.
            flow.Snapshot = flow.Snapshot with { Left = new PartReading(60, false, null) { ReadAt = flow.Time.GetUtcNow() + TimeSpan.FromSeconds(1) }, BatteryReadAt = flow.Time.GetUtcNow() + TimeSpan.FromSeconds(1) };
            flow.Presenter.Refresh();
            Assert.IsNull(flow.Presenter.CurrentModelForTest!.Refresh);
        });
    }

    [TestMethod]
    public void ClosingTheCardForgetsTheReasonAWaitFoundNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            flow.ShowCard();
            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);
            reader.Result.SetResult(BatteryRefreshOutcome.BluetoothOff);
            Assert.IsNotNull(flow.Presenter.RefreshViewForTest);

            flow.Presenter.Hide();

            Assert.IsNull(flow.Presenter.RefreshViewForTest);
        });
    }

    [TestMethod]
    public void TheMenuRequestOpensTheCardAndStartsTheSameRefresh()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            Assert.IsFalse(flow.Presenter.IsShown);

            flow.Presenter.RequestRefresh(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(flow.Presenter.IsShown);
            Assert.AreEqual(1, reader.Calls);
            Assert.IsTrue(flow.Presenter.RefreshViewForTest!.Reading);
        });
    }

    [TestMethod]
    public void AFailedRefreshIsLoggedAndLeavesTheOrdinaryReadLine()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            using var flow = new Flow(reader);
            flow.ShowCard();
            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);

            reader.Result.SetException(new InvalidOperationException("the radio went away"));

            Assert.IsNull(flow.Presenter.RefreshViewForTest);
            Assert.IsFalse(flow.Presenter.RefreshSpinnerRunningForTest);
            Assert.IsTrue(flow.Log.Entries.Any(e => e.Message.Contains("the radio went away", StringComparison.Ordinal)), "The failure is on the log, not swallowed.");
        });
    }

    [TestMethod]
    public void DisposingThePresenterCancelsARefreshThatIsStillReading()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var reader = new Reader();
            var flow = new Flow(reader);
            flow.ShowCard();
            CardKit.Click(flow.Card!, flow.Card!.CurrentMainLayout.Refresh);
            Assert.IsFalse(reader.Token.IsCancellationRequested);

            flow.Dispose();

            Assert.IsTrue(reader.Token.IsCancellationRequested);
        });
    }

    [TestMethod]
    public void WithNoRefreshSourceTheIconDoesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(
                () => card = new WidgetCard(log), CardKit.Callbacks(), CardKit.Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestShow(CardKit.Gauge, CardKit.Gauge.Location);
            Application.DoEvents();

            CardKit.Click(card!, card!.CurrentMainLayout.Refresh);

            Assert.IsNull(presenter.RefreshViewForTest);
            Assert.IsFalse(presenter.RefreshSpinnerRunningForTest);
        });
    }
}
