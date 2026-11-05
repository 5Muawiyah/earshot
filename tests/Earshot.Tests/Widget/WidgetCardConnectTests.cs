using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A connect and a disconnect on the one card: the button's ring, the failure line, the card a failure opens when none is open, and
// the failure held when a gate refuses it. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private const string AReason = "The AirPods did not answer this PC. They may need pairing again.";

    private sealed class ConnectRig : IDisposable
    {
        private long _clock;

        public ConnectRig(bool asked = true, FakeCardEnvironment? environment = null, AirPodsWhere where = AirPodsWhere.NotInUse)
        {
            Callbacks = new FakeCallbacks { Snapshot = Snapshot(where) };
            Environment = environment ?? new FakeCardEnvironment();
            Presenter = NewPresenter(
                () => new WidgetCard(Log, CardOpening.Unasked) { HoldClock = () => _clock }, Callbacks.Build(), Gate(), Environment, Inline, Time, Log,
                new FakeScene(), asked: asked ? () => new WidgetCard(Log) : null);
        }

        public CapturingLog Log { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public FakeCallbacks Callbacks { get; }

        public FakeCardEnvironment Environment { get; }

        public WidgetCardPresenter Presenter { get; }

        public WidgetCard Asked => Presenter.AskedCardForTest!;

        public WidgetCard Unasked => Presenter.UnaskedCardForTest(Presenter.UnaskedShownDisplaysForTest.Single())!;

        public void OpenAsked()
        {
            Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            Assert.IsTrue(Presenter.IsShown, "The asked card opened.");
        }

        public void PassTheCross() => Time.Advance(GaugeStory.CrossHold);

        public void MoveHoldClock(TimeSpan by) => _clock += (long)by.TotalMilliseconds;

        public FailureShown Fail(string reason, string? next, Action? onPress = null) =>
            Presenter.ShowFailed(reason, next is null ? null : new FailureNextStep(next, onPress ?? (() => { }))).GetAwaiter().GetResult();

        public void Dispose() => Presenter.Dispose();
    }

    // ---- The button

    [TestMethod]
    public void TheButtonShowsARingAndIgnoresAPressWhileAConnectRunsThenReadsConnectAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.Callbacks.Intent = new ToggleIntent(true, Guid.NewGuid(), "AirPods");
            rig.OpenAsked();

            rig.Presenter.ToggleStarted(connect: true);

            Assert.AreEqual(CardButtonWork.Connecting, rig.Asked.WorkShown);
            Assert.IsFalse(rig.Asked.ButtonUsable, "A press is ignored while it runs.");
            CardControl button = rig.Asked.CurrentControls()[0];
            Assert.AreEqual("Connecting", button.Name);
            Assert.IsFalse(button.Enabled);
            button.Activate();
            Assert.IsEmpty(rig.Callbacks.ToggleCalls, "A press on the ring started nothing.");
            Assert.AreEqual(1.0, rig.Asked.WorkFadeProgress, "With animation effects off the change lands in one step.");
            Assert.AreEqual(0f, rig.Asked.WorkRingAngleForTest, "Still: a 90 degree arc at 12 o'clock.");

            rig.Presenter.ToggleEnded(connect: true, ToggleOutcome.Failed);

            Assert.AreEqual(CardButtonWork.None, rig.Asked.WorkShown);
            Assert.AreEqual("Connect", rig.Asked.CurrentControls()[0].Name);
        });
    }

    [TestMethod]
    public void ADisconnectRunsTheSameRing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(where: AirPodsWhere.ThisPc);
            rig.Callbacks.Intent = new ToggleIntent(false, Guid.NewGuid(), "AirPods");
            rig.OpenAsked();

            rig.Presenter.ToggleStarted(connect: false);

            Assert.AreEqual(CardButtonWork.Disconnecting, rig.Asked.WorkShown);
            Assert.AreEqual("Disconnecting", rig.Asked.CurrentControls()[0].Name);
        });
    }

    [TestMethod]
    public void ClosingTheCardMidAttemptLeavesTheAttemptRunningAndACardOpenedLaterShowsTheRing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            rig.Presenter.ToggleStarted(connect: true);

            rig.Presenter.Hide();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsShown);
            Assert.AreEqual(CardButtonWork.Connecting, rig.Presenter.WorkForTest, "The attempt carries on.");
            rig.OpenAsked();
            Assert.AreEqual(CardButtonWork.Connecting, rig.Asked.WorkShown, "A card opened meanwhile shows the ring.");
        });
    }

    [TestMethod]
    public void ARunningConnectShowsTheRingOnTheUnaskedFullCardToo()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.ToggleStarted(connect: true);

            Assert.AreEqual(CardButtonWork.Connecting, rig.Unasked.WorkShown);
            Assert.AreEqual("Connecting", rig.Unasked.CurrentControls()[0].Name);
        });
    }

    // ---- The failure line

    // Every reason the connect table can give: each is said whole, in the status row, in its name and in its tooltip.
    [TestMethod]
    [DataRow(ConnectFailureClassifier.WindowsDidNotBringThemBack)]
    [DataRow(ConnectFailureClassifier.DidNotAnswer)]
    [DataRow(ConnectFailureClassifier.NeedPairingAgain)]
    [DataRow(ConnectFailureClassifier.WindowsRefused)]
    [DataRow(ConnectFailureClassifier.DriverDidNotAccept)]
    [DataRow(ConnectFailureClassifier.PartOfThemStayedOff)]
    [DataRow(ConnectFailureClassifier.OpenTheCase)]
    [DataRow(ConnectFailureClassifier.OpenTheCaseAndTryAgain)]
    [DataRow(ConnectFailureClassifier.InUseOnYourIPhone)]
    [DataRow(ConnectFailureClassifier.NotHeardNearby)]
    [DataRow(Earshot.Audio.Connect.ConnectMessages.StillConnecting)]
    [DataRow(Earshot.Audio.Connect.ConnectMessages.CouldNotReadDevices)]
    [DataRow(Earshot.Audio.Connect.ConnectMessages.OutputTurnedOff)]
    [DataRow(Earshot.Audio.Connect.ConnectMessages.CouldNotReachDriver)]
    public void EveryReasonOfTheTableIsSaidWholeInTheOpenCard(string reason)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();

            FailureShown shown = rig.Fail(reason, "Connect in Windows");

            Assert.AreEqual(FailureShown.Shown, shown);
            (char glyph, string text, _) = rig.Asked.StatusRow;
            Assert.AreEqual(FluentGlyphs.Error, glyph, "E783.");
            Assert.AreEqual(reason, text, "The reason is not shortened.");
            CardControl words = rig.Asked.CurrentControls().Single(c => c.Role == CardControlRole.StaticText && c.Name == reason);
            Assert.AreEqual(reason, words.Tooltip, "The whole reason is in the tooltip.");
            Assert.IsTrue(rig.Asked.CurrentControls().Any(c => c.Role == CardControlRole.PushButton && c.Name == "Connect in Windows"));
        });
    }

    [TestMethod]
    public void ALongReasonWrapsToTwoLinesLeftOfTheButtonAndTheCardGrowsUpwardWithItsBottomKept()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            int bottom = rig.Asked.RestBounds.Bottom;
            int row = rig.Asked.CurrentMainLayout.WhereLine.Height;

            rig.Fail(AReason, "Connect in Windows");

            WidgetCardLayout.Layout layout = rig.Asked.CurrentMainLayout;
            Assert.IsGreaterThan(row, layout.WhereLine.Height, "The row grew for the second line.");
            Assert.AreEqual(bottom, rig.Asked.RestBounds.Bottom, "Bottom pinned.");
            Rectangle button = rig.Asked.NoticeActionBounds;
            Assert.AreEqual(layout.WhereLine.Right, button.Right, "The next step is right-aligned.");
            Assert.AreEqual(layout.WhereLine.Bottom, button.Bottom, "On the last line.");
        });
    }

    [TestMethod]
    public void TheNextStepIsATextButtonThatRunsOnceAndTheFailureGoes()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            int pressed = 0;
            rig.Fail(ConnectFailureClassifier.DriverDidNotAccept, "Fix connection", () => pressed++);

            rig.Asked.CurrentControls().Single(c => c.Name == "Fix connection").Activate();

            Assert.AreEqual(1, pressed);
            Assert.IsFalse(rig.Presenter.FailureActiveForTest);
            Assert.AreEqual("Not on this PC", rig.Asked.StatusRow.Text);
            Assert.IsTrue(rig.Presenter.IsShown, "The card stays open.");
        });
    }

    [TestMethod]
    public void AFailureWithNoNextStepIsOnlyTheReason()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();

            Assert.AreEqual(FailureShown.Shown, rig.Fail(ConnectFailureClassifier.NotHeardNearby, null));

            Assert.AreEqual(ConnectFailureClassifier.NotHeardNearby, rig.Asked.StatusRow.Text);
            Assert.IsTrue(rig.Asked.NoticeActionBounds.IsEmpty);
        });
    }

    [TestMethod]
    public void TheFailureEndsWithTheNextAttempt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            rig.Fail(AReason, "Connect in Windows");

            rig.Presenter.ToggleStarted(connect: true);

            Assert.AreEqual("Not on this PC", rig.Asked.StatusRow.Text);
            Assert.IsFalse(rig.Presenter.FailureActiveForTest);
        });
    }

    [TestMethod]
    public void TheFailureEndsWhenWhereTheAirPodsAreChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            rig.Fail(AReason, "Connect in Windows");

            rig.Callbacks.Snapshot = Snapshot(AirPodsWhere.ThisPc);
            rig.Presenter.Refresh();

            Assert.IsFalse(rig.Presenter.FailureActiveForTest);
            Assert.AreEqual("On this PC", rig.Asked.StatusRow.Text);
        });
    }

    [TestMethod]
    public void TheFailureEndsWhenTheCardItWasSaidOnCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            rig.Fail(AReason, "Connect in Windows");

            rig.Presenter.Hide();
            Application.DoEvents();
            rig.OpenAsked();

            Assert.AreEqual("Not on this PC", rig.Asked.StatusRow.Text);
        });
    }

    // ---- No card open

    [TestMethod]
    public void WithNoCardOpenAFailureWithANextStepOpensTheFullCardUnaskedOnceTheCrossHasHadItsTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);

            FailureShown shown = rig.Fail(AReason, "Connect in Windows");

            Assert.AreEqual(FailureShown.Pending, shown);
            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The gauge's cross comes first.");
            rig.PassTheCross();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
            WidgetCard card = rig.Unasked;
            Assert.AreEqual(CardForm.Full, card.ShownForm);
            Assert.AreEqual(AReason, card.StatusRow.Text);
            Assert.IsFalse(card.Focused, "It never takes focus.");
            Assert.AreEqual("Earshot: AirPods", card.AccessibleName);
            Assert.AreEqual(TimeSpan.FromSeconds(12), card.HoldRemaining, "Twelve seconds, as a notice with a button.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: shown unasked (connect failed)"), string.Join("\n", rig.Log.Entries.Select(e => e.Message)));
        });
    }

    [TestMethod]
    public void TheFailedCardHoldsAndAPointerOverItPausesTheHold()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            WidgetCard card = rig.Unasked;

            card.NotePointer(true);
            rig.MoveHoldClock(TimeSpan.FromSeconds(30));
            card.CheckHold();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The pointer over it stopped the clock.");
            Assert.IsTrue(card.HoldPaused);

            card.NotePointer(false);
            rig.MoveHoldClock(TimeSpan.FromSeconds(12));
            card.CheckHold();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "It closed when the twelve seconds had run.");
            Assert.IsFalse(rig.Presenter.FailureActiveForTest, "Closing ends the failure.");
        });
    }

    [TestMethod]
    public void TheFailedCardsButtonRunsTheNextStepAndClosesTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            int pressed = 0;
            rig.Fail(AReason, "Fix connection", () => pressed++);
            rig.PassTheCross();
            Application.DoEvents();

            rig.Unasked.CurrentControls().Single(c => c.Name == "Fix connection").Activate();
            Application.DoEvents();

            Assert.AreEqual(1, pressed);
            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void WithNoCardOpenAndNoNextStepNothingOpens()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);

            Assert.AreEqual(FailureShown.NotShown, rig.Fail(ConnectFailureClassifier.NotHeardNearby, null));
            rig.PassTheCross();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The cross and the gauge's tooltip say it.");
            Assert.IsFalse(rig.Presenter.FailureHeldForTest);
        });
    }

    [TestMethod]
    public void AFailureThatEndedBeforeTheCrossWasDoneNeverOpensACard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Fail(AReason, "Connect in Windows");

            rig.Presenter.ToggleStarted(connect: true);
            rig.PassTheCross();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "A new attempt began, so the old failure is over.");
        });
    }

    [TestMethod]
    public void WhenAGateRefusesTheFailureIsHeldAndShownOnTheNextAskedOpenUntilAStateChange()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var environment = new FakeCardEnvironment { Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN) };
            using var rig = new ConnectRig(environment: environment);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "A full-screen application refuses a card nobody asked for.");
            Assert.IsTrue(rig.Presenter.FailureHeldForTest);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: held (connect failed)"), string.Join("\n", rig.Log.Entries.Select(e => e.Message)));

            rig.OpenAsked();

            Assert.AreEqual(AReason, rig.Asked.StatusRow.Text, "His next asked open shows it.");
        });
    }

    [TestMethod]
    public void AHeldFailureIsDroppedWhenWhereTheAirPodsAreChanges()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var environment = new FakeCardEnvironment { Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN) };
            using var rig = new ConnectRig(environment: environment);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Assert.IsTrue(rig.Presenter.FailureHeldForTest);

            rig.Callbacks.Snapshot = Snapshot(AirPodsWhere.Elsewhere);
            rig.Presenter.Refresh();
            rig.OpenAsked();

            Assert.IsFalse(rig.Presenter.FailureHeldForTest);
            Assert.AreNotEqual(AReason, rig.Asked.StatusRow.Text);
        });
    }

    [TestMethod]
    public void ANewAttemptClosesTheCardTheLastFailureOpened()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.ToggleStarted(connect: true);
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The gauge tells the new attempt.");
        });
    }

    [TestMethod]
    public void AFailureOnTheCaseCardIsSaidInThatCardAndEndsWhenItCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();

            Assert.AreEqual(FailureShown.Shown, rig.Fail(AReason, null));
            Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text);
            Assert.IsNull(rig.Unasked.HoldRemaining, "A card that is already open keeps its own close rule.");

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsFalse(rig.Presenter.FailureActiveForTest);
        });
    }
}
