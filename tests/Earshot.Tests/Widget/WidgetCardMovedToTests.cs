using System.Drawing;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The compact "Moved to" card on the one presenter: when it opens, what it says, what it never does on its own, and what its Take
// back button does. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private sealed class MovedRig : IDisposable
    {
        private long _clock;

        public MovedRig(
            bool asked = false, FakeCardEnvironment? environment = null, UnaskedGate? gate = null, string label = "iPhone",
            AirPodsWhere where = AirPodsWhere.Elsewhere, bool settingOn = true)
        {
            Callbacks = new FakeCallbacks { Snapshot = Snapshot(where), OtherDeviceLabel = label, MovedToOnValue = settingOn };
            Environment = environment ?? new FakeCardEnvironment();
            Presenter = NewPresenter(
                () => new WidgetCard(Log, CardOpening.Unasked) { HoldClock = () => _clock }, Callbacks.Build(), gate ?? Gate(), Environment, Inline, Time, Log,
                new FakeScene(), asked: asked ? () => new WidgetCard(Log) : null);
        }

        public CapturingLog Log { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public FakeCallbacks Callbacks { get; }

        public FakeCardEnvironment Environment { get; }

        public WidgetCardPresenter Presenter { get; }

        public WidgetCard Card => Presenter.UnaskedCardForTest(Presenter.UnaskedShownDisplaysForTest.Single())!;

        public void MoveHold(TimeSpan by) => _clock += (long)by.TotalMilliseconds;

        // A genuine left down and up on the card's button.
        public void ClickTakeBack()
        {
            Rectangle button = Card.CurrentCompactLayout!.Action;
            ClickAt(Card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            Application.DoEvents();
        }

        public void Show()
        {
            Presenter.MovedTo();
            Application.DoEvents();
        }

        public void Dispose() => Presenter.Dispose();
    }

    private static readonly string[] RequestToggleCallers =
    [
        "WidgetCardPresenter.cs:OnToggleRequested",
        "WidgetCardPresenter.Unasked.cs:OnUnaskedToggleRequested",
        "WidgetCardPresenter.MovedTo.cs:OnTakeBackPressed",
    ];

    private const string TakeBackFailure = "The AirPods did not answer this PC. They may need pairing again.";

    // ---- What it says

    [TestMethod]
    public void MovedToOpensTheCompactCardWithTheOwnersLabelAndATakeBackButton()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();

            rig.Show();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Card.IsCompactShown);
            CompactContent row = rig.Presenter.UnaskedCompactForTest!;
            Assert.AreEqual("Moved to iPhone", row.Text);
            Assert.AreEqual(FluentGlyphs.CellPhone, row.Glyph);
            Assert.AreEqual(UnaskedKind.MovedTo, row.Kind);
            Assert.AreEqual(FluentGlyphs.OnThisPc, row.Action!.Glyph);
            Assert.AreEqual("Take back", row.Action.Label);
            Assert.AreEqual(TimeSpan.FromMilliseconds(6000), row.Hold);
            Assert.AreEqual(1, rig.Presenter.AnnouncementsForTest);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: shown unasked (moved to), on 1 display"), Lines(rig.Log));
            LiveTests.LiveTest25Lines.AssertLogged(rig.Log, LiveTests.LiveTest25Lines.MovedCardShown);
            Assert.IsEmpty(rig.Callbacks.ToggleCalls, "Showing it asked for nothing.");
        });
    }

    [TestMethod]
    public void WithNoLabelItSaysAnotherDeviceWithTheDevicesGlyph()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(label: string.Empty);

            rig.Show();

            CompactContent row = rig.Presenter.UnaskedCompactForTest!;
            Assert.AreEqual("Moved to another device", row.Text);
            Assert.AreEqual(FluentGlyphs.Devices, row.Glyph);
        });
    }

    // The one consumer that needs a window: the card the owner asks for, given the snapshot a real verdict made.
    [TestMethod]
    public void TheAskedCardSaysOnYourIPhoneWithThePhoneGlyphAfterARealVerdict()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var verdict = new MovedToRig();
            verdict.LeaveAndBeHeardNear();
            using var rig = new ConnectRig();
            rig.Callbacks.Snapshot = verdict.Service.Current;
            rig.Callbacks.OtherDeviceLabel = "iPhone";

            rig.OpenAsked();

            (char glyph, string text, _) = rig.Asked.StatusRow;
            Assert.AreEqual(FluentGlyphs.CellPhone, glyph);
            Assert.AreEqual("On your iPhone", text);
        });
    }

    // ---- When it opens, and when not

    [TestMethod]
    public void WithTheSettingOffNothingOpens()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(settingOn: false);

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.AreEqual(0, rig.Presenter.WaitingForTest);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (moved to), its setting is off."), Lines(rig.Log));
        });
    }

    // It is shown only while it is still true.
    [TestMethod]
    public void WhenThePairIsNoLongerElsewhereNothingOpens()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(where: AirPodsWhere.ThisPc);

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (moved to), the AirPods are not on another device now."), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void AnAskedCardOpensNothingAndQueuesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(asked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.AreEqual(0, rig.Presenter.WaitingForTest);
            Assert.IsTrue(rig.Presenter.IsShown, "The card he asked for is left as it is.");
        });
    }

    [TestMethod]
    public void AFullScreenApplicationRefusesItAndItIsNotQueued()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(environment: new FakeCardEnvironment { Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN) });

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.AreEqual(0, rig.Presenter.WaitingForTest, "A card a gate refused is not queued.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (moved to), a full-screen application is on display 1."), Lines(rig.Log));
            Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Callbacks.Snapshot.Where, "The status still reads true.");
        });
    }

    [TestMethod]
    public void WindowsNotTakingNotificationsRefusesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(environment: new FakeCardEnvironment { Notifications = State(Shell.QUNS_QUIET_TIME) });

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.AreEqual(0, rig.Presenter.WaitingForTest);
        });
    }

    [TestMethod]
    public void AHandBackRunningRefusesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(gate: Gate(handBack: true));

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (moved to), a hand-back is running."), Lines(rig.Log));
        });
    }

    // ---- Over and under the other cards

    [TestMethod]
    public void TheCaseCardOutranksItAndItShowsAfterOnlyIfStillTrue()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();

            rig.Show();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown && rig.Card.IsCompactShown, "The case card stays.");
            Assert.AreEqual(1, rig.Presenter.WaitingForTest);

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsTrue(rig.Card.IsCompactShown, "It shows once the case card has gone, because it is still true.");
            Assert.AreEqual("Moved to iPhone", rig.Presenter.UnaskedCompactForTest!.Text);
        });
    }

    [TestMethod]
    public void AWaitingMovedToIsDroppedWhenItIsNoLongerTrue()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            rig.Show();
            rig.Callbacks.Snapshot = Snapshot(AirPodsWhere.ThisPc);

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: dropped (moved to), it was no longer true when its turn came."), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void TheCaseOpeningMorphsItInPlaceIntoTheFullCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;

            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();

            Assert.AreSame(card, rig.Card, "The same window.");
            Assert.IsFalse(card.IsCompactShown, "Grown to the full case card.");
            Assert.IsNull(rig.Presenter.UnaskedCompactForTest);
        });
    }

    [TestMethod]
    public void TheCardHeAsksForClosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(asked: true);
            rig.Show();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Presenter.IsShown);
        });
    }

    [TestMethod]
    public void ALowFallbackWaitsBehindItAndAFullyChargedOneDoesNotReplaceIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();

            bool shown = rig.Presenter.ShowCompact(new CompactRequest(CompactContents.FullyCharged(), CardAnchor.NearTray)).GetAwaiter().GetResult();

            Assert.IsFalse(shown, "A lower kind waits its turn.");
            Assert.AreEqual("Moved to iPhone", rig.Presenter.UnaskedCompactForTest!.Text);
            Assert.AreEqual(1, rig.Presenter.WaitingForTest);
        });
    }

    // ---- The hold and the fold

    [TestMethod]
    public void ItHoldsSixSecondsAndFoldsAway()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            Assert.IsTrue(card.HoldActive);

            rig.MoveHold(TimeSpan.FromMilliseconds(5900));
            card.CheckHold();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Not yet.");

            rig.MoveHold(TimeSpan.FromMilliseconds(100));
            card.CheckHold();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsEmpty(rig.Callbacks.ToggleCalls, "Folding away asked for nothing.");
        });
    }

    [TestMethod]
    public void TheBodyGrowsItToTheFullCardWithNoTimer()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;

            ClickAt(card.Handle, new Point(30, 20));
            Application.DoEvents();
            rig.MoveHold(TimeSpan.FromMinutes(5));
            card.CheckHold();

            Assert.IsFalse(card.IsCompactShown);
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "No timer closes the full card.");
            Assert.IsEmpty(rig.Callbacks.ToggleCalls);
        });
    }

    // ---- Take back

    [TestMethod]
    public void TakeBackIsOneGenuineClickThatAsksForConnectOnceAndShowsARing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;

            rig.ClickTakeBack();

            Assert.HasCount(1, rig.Callbacks.ToggleCalls);
            Assert.AreEqual(CardPlace.NearTray, rig.Callbacks.ToggleCalls[0]);
            Assert.AreEqual(CompactActionState.Progress, card.ActionStateShown);
            Assert.IsFalse(card.HoldActive, "It does not fold while the work runs.");
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            LiveTests.LiveTest25Lines.AssertLogged(rig.Log, LiveTests.LiveTest25Lines.TakeBackPressed);

            // A press on the ring starts nothing more.
            card.CurrentControls().First(c => c.Role == CardControlRole.PushButton).Activate();
            Assert.HasCount(1, rig.Callbacks.ToggleCalls);
        });
    }

    [TestMethod]
    public void TakeBackThatSucceedsShowsATickHoldsEightHundredMillisecondsAndFolds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();
            rig.Presenter.ToggleStarted(connect: true);

            rig.Presenter.ToggleEnded(connect: true, ToggleOutcome.Succeeded);

            Assert.AreEqual(CompactActionState.Done, card.ActionStateShown);
            Assert.IsTrue(card.HoldRemaining is { } left && left <= TimeSpan.FromMilliseconds(800), "The tick holds 800 ms: " + card.HoldRemaining);
            rig.MoveHold(TimeSpan.FromMilliseconds(799));
            card.CheckHold();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.MoveHold(TimeSpan.FromMilliseconds(1));
            card.CheckHold();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "Then the card folds away.");
            Assert.HasCount(1, rig.Callbacks.ToggleCalls);
        });
    }

    [TestMethod]
    public void TakeBackThatFailsGrowsToTheFullCardAndSaysWhyThere()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();
            rig.Presenter.ToggleStarted(connect: true);

            rig.Presenter.ToggleEnded(connect: true, ToggleOutcome.Failed);
            Assert.IsFalse(card.IsCompactShown, "Grown to the full card; the compact never shows the error itself.");
            Assert.IsNull(rig.Presenter.UnaskedCompactForTest);
            FailureShown shown = rig.Presenter.ShowFailed(TakeBackFailure, new FailureNextStep("Connect in Windows", () => { })).GetAwaiter().GetResult();

            Assert.AreEqual(FailureShown.Shown, shown);
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);
            Assert.AreEqual(TakeBackFailure, rig.Presenter.UnaskedModelForTest!.Failure!.Text);
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "No timer closes the full card.");
            rig.MoveHold(TimeSpan.FromMinutes(5));
            card.CheckHold();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void TakeBackThatIsCancelledGoesBackToTheButtonAndHoldsAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();
            rig.Presenter.ToggleStarted(connect: true);

            rig.Presenter.ToggleEnded(connect: true, ToggleOutcome.Cancelled);

            Assert.AreEqual(CompactActionState.Idle, card.ActionStateShown);
            Assert.IsTrue(card.IsCompactShown);
            Assert.IsTrue(card.HoldActive, "It holds again.");
        });
    }

    // The tray may refuse the click (safe mode, a menu action in flight): no start ever comes, and the ring does not turn forever.
    [TestMethod]
    public void TakeBackThatNeverStartsStopsTheRing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();
            Assert.AreEqual(CompactActionState.Progress, card.ActionStateShown);

            rig.Time.Advance(WidgetCardPresenter.TakeBackStartLimit);
            Application.DoEvents();

            Assert.AreEqual(CompactActionState.Idle, card.ActionStateShown);
            Assert.IsTrue(card.HoldActive);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: take back did not start"), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void AStartThatComesInTimeKeepsTheRing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();
            rig.Time.Advance(WidgetCardPresenter.TakeBackStartLimit - TimeSpan.FromMilliseconds(1));
            rig.Presenter.ToggleStarted(connect: true);

            rig.Time.Advance(TimeSpan.FromMinutes(1));
            Application.DoEvents();

            Assert.AreEqual(CompactActionState.Progress, card.ActionStateShown, "The work is running; only its end changes the button.");
        });
    }

    // The toggle the tray chose was a disconnect (the state changed under the click): the compact is not taking anything back.
    [TestMethod]
    public void ADisconnectThatStartsInsteadEndsTheTakeBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();
            WidgetCard card = rig.Card;
            rig.ClickTakeBack();

            rig.Presenter.ToggleStarted(connect: false);

            Assert.AreEqual(CompactActionState.Idle, card.ActionStateShown);
        });
    }

    // ---- A Connect from elsewhere

    [TestMethod]
    public void AConnectFromElsewhereFoldsItAtOnceBecauseItIsNoLongerTrue()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();
            rig.Show();

            rig.Presenter.ToggleStarted(connect: true);

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsEmpty(rig.Callbacks.ToggleCalls, "The connect came from elsewhere: the card asked for nothing.");
        });
    }

    [TestMethod]
    public void AConnectFromElsewhereLeavesALowOrChargedCardToItsHold()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig(where: AirPodsWhere.NotInUse);
            rig.Presenter.ShowCompact(new CompactRequest(CompactContents.FullyCharged(), CardAnchor.NearTray)).GetAwaiter().GetResult();
            Application.DoEvents();

            rig.Presenter.ToggleStarted(connect: true);

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "It stays to its hold; the gauge tells the connect.");
        });
    }

    // ---- The at-rest pin: no path to a connect except a genuine click

    // A whole life of the card, the clock moving, every event the arbiter knows: nothing reaches the toggle, the connect or the
    // setting writers until a click on the button does.
    [TestMethod]
    public void NothingAsksForAToggleUntilAGenuineClick()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new MovedRig();

            rig.Show();
            rig.Presenter.Refresh();
            rig.Presenter.DisplayChanged();
            rig.Presenter.RecheckFullScreen();
            Application.DoEvents();
            for (int i = 0; i < 12; i++)
            {
                rig.MoveHold(TimeSpan.FromMilliseconds(500));
                rig.Time.Advance(TimeSpan.FromMilliseconds(500));
                rig.Card.CheckHold();
                Application.DoEvents();
            }

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The hold ended and it folded.");

            // Showing it again, then every other event on top of it.
            rig.Show();
            rig.Presenter.ShowCompact(new CompactRequest(CompactContents.FullyCharged(), CardAnchor.NearTray)).GetAwaiter().GetResult();
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            rig.Presenter.CaseClosed();
            Application.DoEvents();
            rig.Show();
            rig.Card.NotePointer(true);
            rig.Card.NotePointer(false);
            rig.Presenter.ToggleStarted(connect: true);
            rig.Presenter.ToggleEnded(connect: true, ToggleOutcome.Failed);
            rig.Presenter.HideUnasked();
            Application.DoEvents();

            Assert.IsEmpty(rig.Callbacks.ToggleCalls, "Nothing but a click may ask for a connect.");
            Assert.IsEmpty(rig.Callbacks.AutoPauseCalls);

            rig.Show();
            rig.ClickTakeBack();

            Assert.HasCount(1, rig.Callbacks.ToggleCalls, "One genuine click asked once.");
        });
    }

    // The only places that ask for a toggle are the click handlers of a card's own button, so a timer, a fold or a verdict cannot.
    [TestMethod]
    public void OnlyTheCardsOwnButtonHandlersCallRequestToggle()
    {
        string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Earshot", "Widget"));
        var callers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("RequestToggle(", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                    !lines[i].Contains("Action<CardPlace> RequestToggle", StringComparison.Ordinal))
                {
                    callers.Add(Path.GetFileName(file) + ":" + EnclosingMethod(lines, i));
                }
            }
        }

        CollectionAssert.AreEquivalent(
            RequestToggleCallers,
            callers,
            "These are the card's own button handlers; anything else that asks for a toggle is a new path and must be a genuine click.");
    }

    private static string EnclosingMethod(string[] lines, int from)
    {
        for (int i = from; i >= 0; i--)
        {
            string line = lines[i].TrimStart();
            if (lines[i].StartsWith("    private ", StringComparison.Ordinal) || lines[i].StartsWith("    internal ", StringComparison.Ordinal) ||
                lines[i].StartsWith("    public ", StringComparison.Ordinal))
            {
                int paren = line.IndexOf('(', StringComparison.Ordinal);
                int arrow = line.IndexOf("=>", StringComparison.Ordinal);
                int end = paren >= 0 ? paren : arrow;
                string head = end >= 0 ? line[..end] : line;
                return head.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
            }
        }

        return "?";
    }
}
