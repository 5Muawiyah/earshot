using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Compact notices on the one presenter: the gates a notice clears (which are the old popup's, now on the one card), where it goes,
// what happens when two want the card, and what its button does. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private static readonly Rectangle AGauge = new(100, 900, 88, 48);

    private static CompactRequest Notice(
        string text, CardAnchor anchor = CardAnchor.NearTray, Point? click = null, CardAction? action = null, bool staysUp = false) =>
        CompactNoticePresenter.RequestFor(new CardContent("Earshot", text, action, staysUp), anchor, click, null);

    private sealed class NoticeRig : IDisposable
    {
        public NoticeRig(UnaskedGate? gate = null, FakeCardEnvironment? environment = null, IUnaskedScene? scene = null, bool withAsked = false)
        {
            Environment = environment ?? new FakeCardEnvironment();
            Presenter = NewPresenter(
                () => new WidgetCard(Log, CardOpening.Unasked), new FakeCallbacks().Build(), gate ?? Gate(), Environment, Inline, Time, Log,
                scene ?? new FakeScene(), asked: withAsked ? () => new WidgetCard(Log) : null);
        }

        public CapturingLog Log { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public FakeCardEnvironment Environment { get; }

        public WidgetCardPresenter Presenter { get; }

        public bool Show(CompactRequest request) => Presenter.ShowCompact(request).GetAwaiter().GetResult();

        public WidgetCard Card(string display = "") => Presenter.UnaskedCardForTest(display)!;

        public void Dispose() => Presenter.Dispose();
    }

    private static NotificationStateReading State(int state) => new(0, state);

    // ---- The gates

    [TestMethod]
    public void ANoticeAfterAClickShowsOverAFullScreenApplicationWithoutAskingTheState()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(environment: new FakeCardEnvironment { Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN) });

            bool shown = rig.Show(Notice("Connected", CardAnchor.NearCursor, new Point(960, 1056)));
            Application.DoEvents();

            Assert.IsTrue(shown, "A notice that follows his own click is the result of what he did, so it always shows.");
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
            Assert.AreEqual(0, rig.Environment.NotificationQueries, "His click is his own act, so the state is not asked.");
        });
    }

    [TestMethod]
    public void ANoticeNobodyClickedForIsRefusedOverAFullScreenApplicationAndShowAsyncIsFalse()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(environment: new FakeCardEnvironment { Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN) });
            var notices = new CompactNoticePresenter(rig.Log);
            notices.SetTarget(rig.Presenter);

            bool shown = notices.ShowAsync(new CardContent("Earshot", "Checking"), CardAnchor.NearTray, null).GetAwaiter().GetResult();

            Assert.IsFalse(shown);
            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (notice), a full-screen application is on display 1."), Lines(rig.Log));
        });
    }

    [TestMethod]
    [DataRow(Shell.QUNS_PRESENTATION_MODE, "QUNS_PRESENTATION_MODE")]
    [DataRow(Shell.QUNS_QUIET_TIME, "QUNS_QUIET_TIME")]
    [DataRow(Shell.QUNS_NOT_PRESENT, "QUNS_NOT_PRESENT")]
    public void ANoticeNobodyClickedForIsRefusedWhileWindowsIsNotTakingNotifications(int state, string name)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(environment: new FakeCardEnvironment { Notifications = State(state) });

            Assert.IsFalse(rig.Show(Notice("Checking")));

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Windows is not taking notifications now (" + name + ")."), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void ANoticeNobodyClickedForIsRefusedWhenTheStateCannotBeRead()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(environment: new FakeCardEnvironment { Notifications = new NotificationStateReading(unchecked((int)0x80004005), 0) });

            Assert.IsFalse(rig.Show(Notice("Checking")));
            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void ANoticeShowsOnTheDisplaysThatAreNotUnderAFullScreenApplication()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var scene = new FakeScene { List = [CaseOpenCardChoiceTests.One, CaseOpenCardChoiceTests.Two], Window = null };
            using var rig = new NoticeRig(
                Gate(displays: [CaseOpenCardDisplayChoice.All]),
                new FakeCardEnvironment { Notifications = State(Shell.QUNS_ACCEPTS_NOTIFICATIONS) }, scene);

            Assert.IsTrue(rig.Show(Notice("Checking")));

            Assert.HasCount(2, rig.Presenter.UnaskedShownDisplaysForTest, "Every chosen display, as the case card does.");
        });
    }

    [TestMethod]
    public void ANoticeIsNotHeldBackByAHandBackOrASessionEndNorByTheCaseCardSetting()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(Gate(enabled: false, handBack: true, sessionEnd: true));

            Assert.IsTrue(rig.Show(Notice("Handing back the AirPods")), "A notice about a hand-back or a session end must be able to show.");
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void ANoticeIsRefusedWhileEarshotIsClosingEvenAfterAClick()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(Gate(closing: true));

            Assert.IsFalse(rig.Show(Notice("Closing", CardAnchor.NearCursor, new Point(960, 1056))));
            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void ANoticeAnnouncesItselfOnceToAScreenReader()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();

            rig.Show(Notice("Connected"));

            Assert.AreEqual(1, rig.Presenter.AnnouncementsForTest);
            Assert.AreEqual("Earshot: Connected", rig.Card(CaseOpenCardChoiceTests.One.Id).AccessibleName);
        });
    }

    // ---- What it shows

    [TestMethod]
    public void ANoticeIsTheCompactRowOfItsWordsAndTakesNoFocus()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var background = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            background.Show();
            background.Activate();
            Application.DoEvents();
            nint before = Phase5.TestWindows.GetActiveWindow();
            using var rig = new NoticeRig();

            rig.Show(Notice("Update available"));
            Application.DoEvents();

            WidgetCard card = rig.Card(CaseOpenCardChoiceTests.One.Id);
            Assert.IsTrue(card.IsCompactShown);
            Assert.AreEqual("Update available", rig.Presenter.UnaskedCompactForTest!.Text);
            Assert.AreEqual(before, Phase5.TestWindows.GetActiveWindow(), "A notice never takes the foreground.");
        });
    }

    [TestMethod]
    public void ANoticeWithAnActionHoldsTwelveSecondsAndAPlainOneSix()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();

            rig.Show(Notice("Plain"));
            string id = CaseOpenCardChoiceTests.One.Id;
            Assert.IsTrue(rig.Card(id).HoldActive);
            Assert.IsTrue(rig.Card(id).HoldRemaining is { } six && six > TimeSpan.FromSeconds(5) && six <= TimeSpan.FromSeconds(6), "Six seconds.");

            rig.Show(Notice("With button", action: new CardAction("Fix connection", () => { })));
            Assert.IsTrue(rig.Card(id).HoldRemaining is { } twelve && twelve > TimeSpan.FromSeconds(11) && twelve <= TimeSpan.FromSeconds(12), "Twelve seconds, and it started again.");
        });
    }

    [TestMethod]
    public void ANoticeThatStaysUpHasNoHoldUntilHide()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();

            rig.Show(Notice("Recording", action: new CardAction("Stop", () => { }), staysUp: true));
            Application.DoEvents();

            Assert.IsFalse(rig.Card(CaseOpenCardChoiceTests.One.Id).HoldActive, "It has no timer of its own.");
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.HideCompact();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void ANewerNoticeReplacesTheRowInPlaceAndTheHoldStartsAgain()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            string id = CaseOpenCardChoiceTests.One.Id;

            rig.Show(Notice("One"));
            WidgetCard first = rig.Card(id);
            long start = 1_000;
            first.HoldClock = () => start;
            first.StartHold();
            start = 4_500;

            Assert.IsTrue(rig.Show(Notice("One")), "The same words again.");

            Assert.AreSame(first, rig.Card(id), "One card, replaced in place.");
            Assert.HasCount(1, rig.Presenter.UnaskedShownDisplaysForTest);
            Assert.IsTrue(first.HoldRemaining is { } left && left > TimeSpan.FromSeconds(5.9), "The hold began again at the new notice: " + first.HoldRemaining);

            rig.Show(Notice("Two"));
            Assert.AreEqual("Two", rig.Presenter.UnaskedCompactForTest!.Text);
        });
    }

    [TestMethod]
    public void AButtonPressClosesTheCardAndThenRunsItsActionOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            int ran = 0;
            bool goneWhenRun = false;
            rig.Show(Notice("Not protected", action: new CardAction("Fix connection", () => { ran++; goneWhenRun = !rig.Presenter.IsUnaskedShown; })));
            WidgetCard card = rig.Card(CaseOpenCardChoiceTests.One.Id);
            Rectangle button = card.CurrentCompactLayout!.Action;

            ClickAt(card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            Application.DoEvents();

            Assert.AreEqual(1, ran);
            Assert.IsTrue(goneWhenRun, "The card goes away and then what the button does runs.");
            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }

    [TestMethod]
    public void AButtonThatThrowsIsLoggedAndTheCardIsStillGone()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            rig.Show(Notice("Not protected", action: new CardAction("Fix connection", () => throw new InvalidOperationException("no"))));
            WidgetCard card = rig.Card(CaseOpenCardChoiceTests.One.Id);
            Rectangle button = card.CurrentCompactLayout!.Action;

            ClickAt(card.Handle, new Point(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Log.Entries.Any(e => e.Message.Contains("Fix connection", StringComparison.Ordinal) && e.Message.Contains("InvalidOperationException", StringComparison.Ordinal)), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void AClickOnTheBodyGrowsTheRowToTheFullCardWithNoHold()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            rig.Show(Notice("Checking"));
            WidgetCard card = rig.Card(CaseOpenCardChoiceTests.One.Id);

            ClickAt(card.Handle, new Point(30, 20));
            Application.DoEvents();

            Assert.IsNull(rig.Presenter.UnaskedCompactForTest);
            Assert.IsFalse(card.IsCompactShown);
            Assert.IsFalse(card.HoldActive);
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
        });
    }

    // ---- Two things want the one card

    [TestMethod]
    public void ANoticeWaitsBehindTheCaseCardAndShowsAfterTheCaseClosesOnlyIfStillTrue()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();

            Assert.IsFalse(rig.Show(Notice("Waiting")), "It does not open over the case card.");
            Assert.AreEqual(1, rig.Presenter.WaitingForTest);
            Assert.IsNull(rig.Presenter.UnaskedCompactForTest);

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.AreEqual("Waiting", rig.Presenter.UnaskedCompactForTest?.Text, "It is shown after the case card.");
            Assert.AreEqual(0, rig.Presenter.WaitingForTest);

            rig.Presenter.HideCompact();
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            rig.Show(Notice("Late"));
            rig.Time.Advance(TimeSpan.FromSeconds(7));
            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsNull(rig.Presenter.UnaskedCompactForTest, "Its time from arrival had run out, so it is no longer true.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: dropped (notice)"), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void TheCaseOpeningTakesTheRowOnShowAndHideCompactLeavesTheCaseCardAlone()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig();
            rig.Show(Notice("Checking"));

            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();

            Assert.IsNull(rig.Presenter.UnaskedCompactForTest, "The case opening outranks every notice.");
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.HideCompact();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Hide closes a compact notice only.");
        });
    }

    [TestMethod]
    public void ANoticeIsNotShownBehindTheCardTheOwnerAskedFor()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(withAsked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsShown);

            rig.Show(Notice("Checking"));
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The two are never on screen together.");
        });
    }

    // ---- Where it goes after a click

    [TestMethod]
    public void ANoticeAfterAClickIsPlacedAtTheClickOnTheTaskbar()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var environment = new FakeCardEnvironment { Scene = Desktops.BottomTaskbar(new Point(10, 10)) };
            using var rig = new NoticeRig(environment: environment);

            Assert.IsTrue(rig.Show(Notice("Connected", CardAnchor.NearCursor, new Point(960, 1056))));

            Rectangle bounds = rig.Card().RestBounds;
            Assert.IsTrue(bounds.Left <= 960 && bounds.Right >= 960, "Over the click: " + bounds);
            Assert.IsLessThanOrEqualTo(1032, bounds.Bottom, "Above the taskbar.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "anchored at the click (960,1056)"), Lines(rig.Log));
        });
    }

    [TestMethod]
    public void AResultNoticeLandsWhereTheOneForTheSameClickDidAndForgetsItAfterAMinute()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var environment = new FakeCardEnvironment { Scene = Desktops.BottomTaskbar(new Point(960, 1056)) };
            using var rig = new NoticeRig(environment: environment);

            rig.Show(Notice("Connecting", CardAnchor.NearCursor));
            Rectangle first = rig.Card().RestBounds;

            rig.Time.Advance(TimeSpan.FromSeconds(15));
            environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
            rig.Show(Notice("Connected", CardAnchor.NearCursor));

            Assert.AreEqual(first, rig.Card().RestBounds, "The result lands where the first card for the click did.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "anchored where the last card after a click was (960,1056)"), Lines(rig.Log));

            rig.Time.Advance(WidgetCardPresenter.ClickAnchorLifetime + TimeSpan.FromSeconds(1));
            rig.Show(Notice("Disconnected", CardAnchor.NearCursor));

            Assert.IsGreaterThan(first.Right, rig.Card().RestBounds.Right, "Forgotten: the corner near the notification area.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "near the notification area, because the cursor is away from the taskbar (750,0)"), Lines(rig.Log));
        });
    }

    // ---- The card he asked for carries a notice

    [TestMethod]
    public void ANoticeOverTheMainPageIsSaidByTheStatusRowAndNoSecondWindowOpens()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(withAsked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            WidgetCard asked = rig.Presenter.AskedCardForTest!;
            int height = asked.CurrentMainLayout.Height;

            Assert.IsTrue(rig.Show(Notice("Disconnected")), "Carried counts as shown.");

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The two are never on screen together.");
            Assert.AreEqual("Disconnected", rig.Presenter.AskedNoticeForTest!.Text);
            Assert.AreEqual("Disconnected", asked.StatusRow.Text);
            Assert.AreEqual(height, asked.CurrentMainLayout.Height, "A one-line notice needs no more room than the row has.");
        });
    }

    [TestMethod]
    public void ALongNoticeGrowsTheStatusRowToThreeLinesAtMost()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(withAsked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            WidgetCard asked = rig.Presenter.AskedCardForTest!;
            int row = asked.CurrentMainLayout.WhereLine.Height;
            string longText = string.Join(" ", Enumerable.Repeat("The AirPods did not answer and may be in the case.", 8));

            rig.Show(Notice(longText, action: new CardAction("Connect in Windows", () => { })));

            int grown = asked.CurrentMainLayout.WhereLine.Height;
            Assert.IsGreaterThan(row, grown, "The row grew.");
            Assert.AreEqual(row + (2 * TextFit.Grow(WidgetCardLayout.CompactLineAt96, 96, 1.0)), grown, "Three lines at the most.");
            Assert.IsFalse(asked.NoticeActionBounds.IsEmpty);
            Assert.AreEqual(asked.CurrentMainLayout.WhereLine.Bottom, asked.NoticeActionBounds.Bottom, "The button stands on the last line.");
        });
    }

    [TestMethod]
    public void TheCarriedNoticesButtonRunsItsActionOnceAndTheCardStaysOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(withAsked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            WidgetCard asked = rig.Presenter.AskedCardForTest!;
            int ran = 0;
            rig.Show(Notice("Not protected", action: new CardAction("Fix connection", () => ran++)));

            CardKit.Click(asked, asked.NoticeActionBounds);
            Application.DoEvents();

            Assert.AreEqual(1, ran);
            Assert.IsNull(rig.Presenter.AskedNoticeForTest, "The notice goes when its button is pressed.");
            Assert.IsTrue(rig.Presenter.IsShown, "The card he asked for stays.");
        });
    }

    [TestMethod]
    public void ACarriedNoticeLeavesAfterItsHoldAndOnHideAndWithTheCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(withAsked: true);
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();

            rig.Show(Notice("One"));
            rig.Time.Advance(TimeSpan.FromSeconds(6));
            Assert.IsNull(rig.Presenter.AskedNoticeForTest, "Six seconds, as a plain notice holds.");

            rig.Show(Notice("Two", action: new CardAction("Fix connection", () => { })));
            rig.Time.Advance(TimeSpan.FromSeconds(7));
            Assert.IsNotNull(rig.Presenter.AskedNoticeForTest, "A notice with a button holds twelve.");
            rig.Time.Advance(TimeSpan.FromSeconds(6));
            Assert.IsNull(rig.Presenter.AskedNoticeForTest);

            rig.Show(Notice("Three", staysUp: true));
            rig.Time.Advance(TimeSpan.FromMinutes(5));
            Assert.IsNotNull(rig.Presenter.AskedNoticeForTest, "It stays until Hide.");
            rig.Presenter.HideCompact();
            Assert.IsNull(rig.Presenter.AskedNoticeForTest);

            rig.Show(Notice("Four", staysUp: true));
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();
            Assert.IsNull(rig.Presenter.AskedNoticeForTest, "The card closed and took the notice with it.");
        });
    }

    [TestMethod]
    public void ANoticeOverASubPageIsAStripAtTheFootWithItsButtonAndTheCardGrowsByIt()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            WidgetCardModel settings = CardKit.SettingsModel(FakeCardHost.Defaults());
            card.Render(settings, 96);
            int plain = card.LaidOutSize.Height;
            Assert.IsNull(card.CurrentStripLayout);

            var notice = new CompactContent('\0', InkRole.Primary, "Not protected", new CompactAction("Fix connection"), TimeSpan.FromSeconds(12));
            card.Render(settings with { Notice = notice }, 96);

            Assert.IsNotNull(card.CurrentStripLayout);
            Assert.AreEqual(plain + card.CurrentStripLayout!.Height, card.LaidOutSize.Height, "Bottom pinned: the page keeps its place and the strip is added.");
            Rectangle button = card.StripActionBounds;
            Assert.IsFalse(button.IsEmpty);
            Assert.IsGreaterThanOrEqualTo(card.LaidOutSize.Height - card.CurrentStripLayout.Height, button.Top, "The button is in the strip.");

            card.Render(settings, 96);
            Assert.IsNull(card.CurrentStripLayout, "The strip goes with the notice.");
            Assert.AreEqual(plain, card.LaidOutSize.Height);
        });
    }

    [TestMethod]
    public void AClickOnTheStripsButtonRaisesTheNoticeActionAndNothingUnderIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            var notice = new CompactContent('\0', InkRole.Primary, "Not protected", new CompactAction("Fix connection"), TimeSpan.FromSeconds(12));
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults()) with { Notice = notice }, 96);
            card.Show();
            Application.DoEvents();
            int raised = 0;
            card.NoticeActionRequested += (_, _) => raised++;

            CardKit.Click(card, card.StripActionBounds);

            Assert.AreEqual(1, raised);
        });
    }
}
