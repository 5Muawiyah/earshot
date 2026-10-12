using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// The Fix offer: one button under the failure reason, made only for a Connect that failed on this PC's side, and a card that
// carries a button, draws it, hit-tests it and runs what it does. The fix itself is not started by any test.
[TestClass]
public sealed class FixOfferTests
{
    private const string Reason = "The audio driver did not accept the request.";

    // ---- when it is offered

    [TestMethod]
    public void APcSideConnectFailureThatReachedTheThresholdIsOffered()
    {
        Assert.IsTrue(FixOffer.ShouldOffer(new FixOfferFacts(true, FixFailureClass.PcSide), succeeded: false, cancelled: false, wasConnect: true));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(1)]
    [DataRow(0)]
    public void NeverOfferedForAnyOtherClassOfFailure(int failure)
    {
        // Out of range, in the case, on the other device: the AirPods' side, so a fix on this PC cannot help, however many times.
        Assert.IsFalse(FixOffer.ShouldOffer(new FixOfferFacts(true, failure switch { 2 => FixFailureClass.DeviceSide, 1 => FixFailureClass.Temporary, _ => FixFailureClass.None }), succeeded: false, cancelled: false, wasConnect: true));
    }

    [TestMethod]
    public void NeverOfferedBeforeTheThresholdOnSuccessOnCancellationOrForADisconnect()
    {
        var due = new FixOfferFacts(true, FixFailureClass.PcSide);

        Assert.IsFalse(FixOffer.ShouldOffer(new FixOfferFacts(false, FixFailureClass.PcSide), false, false, true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: true, cancelled: false, wasConnect: true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: false, cancelled: true, wasConnect: true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: false, cancelled: false, wasConnect: false));
    }

    [TestMethod]
    public void AReportThatCarriesNoFactsOffersNothing()
    {
        var report = new ToggleReport(true, OpStatus.Failed, Reason, [], Cancelled: false);

        Assert.IsFalse(FixOffer.ShouldOffer(FixOfferFacts.From(report), report.IsSuccess, report.Cancelled, wasConnect: true));
    }

    // ---- the card

    private static CardContent WithButton(Action onClick) => new("AirPods", Reason, new CardAction(FixOffer.ButtonLabel, onClick));

    [TestMethod]
    public void TheCardGrowsByTheButtonAndDrawsItUnderTheStatus()
    {
        Phase5.CardSta.Run(() =>
        {
            using var plain = new ConnectCard(new CapturingLog());
            using var offer = new ConnectCard(new CapturingLog());
            Size without = plain.Prepare(new CardContent("AirPods", Reason), 96, CardTheme.Light, 560);
            Size with = offer.Prepare(WithButton(() => { }), 96, CardTheme.Light, 560);

            Assert.IsTrue(plain.ActionBounds().IsEmpty);
            Rectangle button = offer.ActionBounds();
            Assert.IsFalse(button.IsEmpty);
            Assert.IsGreaterThanOrEqualTo(offer.StatusLineBounds().Bottom, button.Top);
            Assert.IsGreaterThan(without.Height, with.Height);
            Assert.IsLessThanOrEqualTo(with.Width, button.Right);

            using var bitmap = new Bitmap(with.Width, with.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                offer.RenderContent(g);
            }

            CollectionAssert.Contains(offer.LastPaintedText().ToList(), "Fix connection");
        });
    }

    [TestMethod]
    public void APressOnTheButtonIsTheButtonsAndAPressAnywhereElseDismissesTheCard()
    {
        Phase5.CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            card.Prepare(WithButton(() => { }), 96, CardTheme.Light, 560);
            var actions = 0;
            var clicks = 0;
            card.ActionClicked += (_, _) => actions++;
            card.Clicked += (_, _) => clicks++;
            MethodInfo mouseUp = typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Rectangle button = card.ActionBounds();

            mouseUp.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, button.X + 3, button.Y + 3, 0)]);
            mouseUp.Invoke(card, [new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0)]);
            mouseUp.Invoke(card, [new MouseEventArgs(MouseButtons.Right, 1, button.X + 3, button.Y + 3, 0)]);

            Assert.AreEqual(1, actions);
            Assert.AreEqual(2, clicks, "A press outside the button, and a right-click on it, dismiss the card.");
        });
    }

    // ---- the presenter

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Presenter = new CardPresenter(
                Log, Ui.Post, new FakeCardEnvironment(), () => Card, () => Timer, new CardClock());
        }

        public CapturingLog Log { get; } = new();

        public QueuedUi Ui { get; } = new();

        public FakeCardSurface Card { get; } = new();

        public FakeCardTimer Timer { get; } = new();

        public CardPresenter Presenter { get; }

        public void Dispose() => Presenter.Dispose();
    }

    [TestMethod]
    public void APressOnTheButtonHidesTheCardAndRunsItsActionOnce()
    {
        using var rig = new Rig();
        int ran = 0;
        rig.Presenter.Show(WithButton(() => ran++), CardAnchor.NearTray);
        rig.Ui.RunAll();

        rig.Card.PressAction();

        Assert.IsFalse(rig.Card.OnScreen);
        Assert.AreEqual(1, ran, "One press runs the action once; the card is gone, so a second press has no button.");
    }

    [TestMethod]
    public void ACardWithAButtonStaysUpLongerThanOneWithout()
    {
        using var rig = new Rig();
        rig.Presenter.Show(new CardContent("AirPods", Reason), CardAnchor.NearTray);
        rig.Ui.RunAll();
        TimeSpan plain = rig.Timer.Restarts.Last();

        rig.Presenter.Show(WithButton(() => { }), CardAnchor.NearTray);
        rig.Ui.RunAll();
        TimeSpan withButton = rig.Timer.Restarts.Last();

        Assert.AreEqual(CardPresenter.DismissAfter, plain);
        Assert.AreEqual(CardPresenter.ActionDismissAfter, withButton);
        Assert.IsGreaterThan(plain, withButton);
    }

    [TestMethod]
    public void AButtonThatThrowsIsLoggedAndTheCardIsStillGone()
    {
        using var rig = new Rig();
        rig.Presenter.Show(WithButton(() => throw new InvalidOperationException("no")), CardAnchor.NearTray);
        rig.Ui.RunAll();

        rig.Card.PressAction();

        Assert.IsFalse(rig.Card.OnScreen);
        Assert.IsTrue(rig.Log.Entries.Any(e => e.Message.Contains("Fix connection", StringComparison.Ordinal) && e.Message.Contains("InvalidOperationException", StringComparison.Ordinal)));
    }

    // ---- the tray's side

    [TestMethod]
    public void TheRealLauncherNamesTheFixVerbOfThisProgram()
    {
        // Not started here: the verb it passes is the one the program dispatches, and the label is the button's.
        string label = FixOffer.ButtonLabel;
        Assert.AreEqual("Fix connection", label);
        Assert.IsInstanceOfType<IFixLauncher>(new OwnProgramFixLauncher());
    }
}
