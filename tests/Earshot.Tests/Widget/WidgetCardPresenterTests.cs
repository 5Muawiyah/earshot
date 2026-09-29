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

    // The model's own ShowSetupButton comes from BuildModel, shared with CaseOpenCardPresenter: proving it
    // here proves both, since neither presenter has its own copy of this derivation.
    [TestMethod]
    public void TheModelShowsTheSetupButtonOnlyWhileNoPartHasAPercent()
    {
        var callbacks = new FakeCallbacks { Snapshot = Snapshot() };
        WidgetCardModel none = WidgetCardPresenter.BuildModel(callbacks.Build(), new Streaming.TestTimeProvider());
        Assert.IsTrue(none.ShowSetupButton, "No part has a percent: the grid gives way to the set-up button.");
        Assert.AreEqual(WidgetCardView.Main, none.View);

        callbacks.Snapshot = Snapshot() with { Left = new PartReading(70, false, null) { ReadAt = DateTimeOffset.UtcNow } };
        WidgetCardModel reading = WidgetCardPresenter.BuildModel(callbacks.Build(), new Streaming.TestTimeProvider());
        Assert.IsFalse(reading.ShowSetupButton, "With a reading the button is gone: a repeat set-up is reached from the menu.");

        callbacks.Snapshot = Snapshot() with { Case = new PartReading(30, null, null) { ReadAt = DateTimeOffset.UtcNow } };
        Assert.IsFalse(WidgetCardPresenter.BuildModel(callbacks.Build(), new Streaming.TestTimeProvider()).ShowSetupButton, "A case percent alone is a reading.");
    }

    [TestMethod]
    public void RequestSetupShowsListeningAndStartsTheListen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);

            presenter.RequestSetup(Gauge, Gauge.Location);
            Application.DoEvents();

            Assert.IsTrue(presenter.IsShown);
            Assert.AreEqual(WidgetCardView.SetupListening, presenter.ViewForTest);
            Assert.AreEqual(WidgetCardView.SetupListening, presenter.CurrentModelForTest!.View);
            Assert.AreEqual("1/3", presenter.CurrentModelForTest.Setup!.Step);
            Assert.AreEqual(1, callbacks.Listens.Count, "One listen is started.");
            Assert.IsFalse(callbacks.ListenTokens[0].IsCancellationRequested);
        });
    }

    [TestMethod]
    public void FoundMovesToPickWithFiftiesAndChargingOff()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);

            callbacks.Listens[0].SetResult(FoundListen());

            Application.DoEvents(); // the listen continues on the UI thread

            Assert.AreEqual(WidgetCardView.SetupPick, presenter.ViewForTest);
            SetupViewModel setup = presenter.CurrentModelForTest!.Setup!;
            Assert.AreEqual("2/3", setup.Step);
            Assert.AreEqual(new BatterySetupPicks(50, 50, 50, false, false, false), setup.Picks,
                "Never the advertisement's own values: the pickers start at 50 with Charging off.");
        });
    }

    [TestMethod]
    public void SaveCompletesAndShowsTheResultsDoneView()
    {
        (BatterySetupResultStatus Status, string Text)[] expected =
        [
            (BatterySetupResultStatus.BatterySetUp, WidgetCopy.SetupBatterySetUp),
            (BatterySetupResultStatus.CaseSetUp, WidgetCopy.SetupCaseSetUp),
            (BatterySetupResultStatus.CaseSetUpBudsSame, WidgetCopy.SetupCaseSetUp),
            (BatterySetupResultStatus.SavedNeedsAnother, WidgetCopy.SetupSaved),
            (BatterySetupResultStatus.NotSaved, WidgetCopy.SetupNotSaved),
            (BatterySetupResultStatus.CouldNotRead, WidgetCopy.SetupCouldNotRead),
        ];
        foreach ((BatterySetupResultStatus status, string text) in expected)
        {
            Phase5.CardDesktop.Run(() =>
            {
                var callbacks = new FakeCallbacks { CompleteStatus = status };
                var log = new CapturingLog();
                WidgetCard? card = null;
                using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
                presenter.RequestSetup(Gauge, Gauge.Location);
                callbacks.Listens[0].SetResult(FoundListen());
                Application.DoEvents(); // the listen continues on the UI thread

                // The owner steps the left picker up once, then saves. Focus starts on Save.
                card!.HandleSetupKey(Keys.Tab);   // Save -> Back
                card.HandleSetupKey(Keys.Tab);    // Back -> left up
                card.HandleSetupKey(Keys.Space);  // left +10
                card.HandleSetupKey(Keys.Tab);    // left down
                card.HandleSetupKey(Keys.Enter);  // Enter is Save wherever the focus is

                Assert.AreEqual(1, callbacks.CompleteCalls.Count, "Save completes the set-up exactly once (" + status + ").");
                Assert.AreEqual(60, callbacks.CompleteCalls[0].Picks.Left, "The picks the owner made reach the completion.");
                Assert.AreEqual(WidgetCardView.SetupDone, presenter.ViewForTest);
                Assert.AreEqual(text, presenter.CurrentModelForTest!.Setup!.Status, "The Done view says what this result was (" + status + ").");
                Assert.AreEqual("3/3", presenter.CurrentModelForTest.Setup!.Step);
            });
        }
    }

    [TestMethod]
    public void NotFoundShowsFailedWithTryAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);

            callbacks.Listens[0].SetResult(Failed(BatterySetupListenStatus.NotFound));

            Application.DoEvents(); // the listen continues on the UI thread

            Assert.AreEqual(WidgetCardView.SetupFailed, presenter.ViewForTest);
            SetupViewModel setup = presenter.CurrentModelForTest!.Setup!;
            Assert.AreEqual(WidgetCopy.SetupNotFound, setup.Status);
            CollectionAssert.AreEqual(new[] { WidgetCopy.Cancel, WidgetCopy.TryAgain }, setup.Buttons.Select(b => b.Label).ToArray());
            Assert.IsTrue(setup.Buttons[1].Primary, "Try again is the primary button.");
        });
    }

    [TestMethod]
    public void TryAgainListensAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);
            callbacks.Listens[0].SetResult(Failed(BatterySetupListenStatus.Ambiguous));
            Application.DoEvents(); // the listen continues on the UI thread
            Assert.AreEqual(WidgetCardView.SetupFailed, presenter.ViewForTest);

            card!.HandleSetupKey(Keys.Enter); // the primary button: Try again

            Assert.AreEqual(2, callbacks.Listens.Count, "Try again starts a second listen.");
            Assert.AreEqual(WidgetCardView.SetupListening, presenter.ViewForTest);
            Assert.IsTrue(presenter.IsShown);
        });
    }

    [TestMethod]
    public void CancelDuringListenCancelsTheToken()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);

            card!.HandleSetupKey(Keys.Escape);

            Assert.IsTrue(callbacks.ListenTokens[0].IsCancellationRequested, "Escape cancels a listen that is still running.");
            Assert.IsFalse(presenter.IsShown, "Escape closes the card.");
            Assert.AreEqual(WidgetCardView.Main, presenter.ViewForTest);

            // The listen finishing after the cancel is dropped: nothing reopens or moves on.
            callbacks.Listens[0].SetResult(FoundListen());
            Application.DoEvents(); // the listen continues on the UI thread
            Assert.AreEqual(WidgetCardView.Main, presenter.ViewForTest);
            Assert.AreEqual(0, callbacks.CompleteCalls.Count, "A cancelled set-up writes nothing.");
        });
    }

    [TestMethod]
    public void ShortFormOnlyGoesThroughPickThenSaysCouldNotRead()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks { CompleteStatus = BatterySetupResultStatus.CouldNotRead };
            var log = new CapturingLog();
            WidgetCard? card = null;
            using var presenter = new WidgetCardPresenter(() => card = new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);

            callbacks.Listens[0].SetResult(FoundListen() with { Status = BatterySetupListenStatus.ShortFormOnly });

            Application.DoEvents(); // the listen continues on the UI thread
            Assert.AreEqual(WidgetCardView.SetupPick, presenter.ViewForTest, "The picks are still asked for: the record keeps them beside the captures.");

            card!.HandleSetupKey(Keys.Enter);

            Assert.AreEqual(WidgetCardView.SetupDone, presenter.ViewForTest);
            Assert.AreEqual(WidgetCopy.SetupCouldNotRead, presenter.CurrentModelForTest!.Setup!.Status);
            Assert.AreEqual(WidgetCopy.SetupCapturesKept, presenter.CurrentModelForTest.Setup.StatusSub);
        });
    }

    [TestMethod]
    public void TheSpinnerTimerRunsOnlyWhileListening()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var time = new Streaming.TestTimeProvider();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, time, log);
            Assert.IsFalse(presenter.SpinnerRunningForTest);

            presenter.RequestSetup(Gauge, Gauge.Location);
            Assert.IsTrue(presenter.SpinnerRunningForTest, "Listening turns the spinner.");
            time.Advance(WidgetCardPresenter.SpinnerInterval);
            Assert.AreEqual(1, presenter.CurrentModelForTest!.Setup!.SpinnerFrame, "Each 100 ms advances one frame.");

            callbacks.Listens[0].SetResult(FoundListen());

            Application.DoEvents(); // the listen continues on the UI thread
            Assert.IsFalse(presenter.SpinnerRunningForTest, "Once the pickers show, no timer runs.");
        });
    }

    // A set-up page does not close on a second gauge click: a stray click on the way to the owner's case must
    // not lose the step.
    [TestMethod]
    public void ASecondGaugeClickDuringSetupIsIgnored()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var callbacks = new FakeCallbacks();
            var log = new CapturingLog();
            using var presenter = new WidgetCardPresenter(() => new WidgetCard(log), callbacks.Build(), Inline, new Streaming.TestTimeProvider(), log);
            presenter.RequestSetup(Gauge, Gauge.Location);

            presenter.RequestShow(Gauge, Gauge.Location);

            Assert.IsTrue(presenter.IsShown, "The gauge click must not close a set-up page.");
            Assert.AreEqual(WidgetCardView.SetupListening, presenter.ViewForTest);
        });
    }

    private static BatterySetupListen FoundListen()
    {
        BatterySetupRecord record = SetupRecordFixtures.Record(SetupRecordFixtures.Message(high: 8, low: 4), SetupRecordFixtures.Picks(40, 80));
        return new BatterySetupListen(
            BatterySetupListenStatus.Found, string.Empty, record.Candidate, [], SetupRecordFixtures.Start, SetupRecordFixtures.Start.AddSeconds(20), 40, 12);
    }

    private static BatterySetupListen Failed(BatterySetupListenStatus status) =>
        new(status, "x", Candidate: null, [], SetupRecordFixtures.Start, SetupRecordFixtures.Start.AddSeconds(20), 0, 0);

    private const int WM_KEYDOWN = 0x0100;

    private static void Inline(Action action) => action();

    private static WidgetSnapshot Snapshot(AirPodsWhere where = AirPodsWhere.Unknown, bool autoPauseAvailable = false, DateTimeOffset? readAt = null, bool claimExists = true) =>
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
            ClaimExists: claimExists,
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

        // One TaskCompletionSource per listen the presenter starts, so a test finishes each in turn, and the
        // token each was given.
        public List<TaskCompletionSource<BatterySetupListen>> Listens { get; } = new();

        public List<CancellationToken> ListenTokens { get; } = new();

        public List<(BatterySetupListen Listen, BatterySetupPicks Picks)> CompleteCalls { get; } = new();

        public BatterySetupResultStatus CompleteStatus { get; set; } = BatterySetupResultStatus.CaseSetUp;

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
            SetAutoPause: (on, place) => AutoPauseCalls.Add((on, place)),
            ListenForSetup: ct =>
            {
                ListenTokens.Add(ct);
                var listen = new TaskCompletionSource<BatterySetupListen>();
                Listens.Add(listen);
                return listen.Task;
            },
            CompleteSetup: (listen, picks) =>
            {
                CompleteCalls.Add((listen, picks));
                return new BatterySetupResult(CompleteStatus, "setup.json", DecodeProof.Evaluate([]));
            });
    }
}
