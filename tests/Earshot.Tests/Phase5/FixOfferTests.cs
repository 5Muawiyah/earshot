using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
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

    // A card that stays up has no dismiss timer: the trace's Stop is the only way to end the recording, so its card goes only
    // when it is pressed, clicked, replaced or hidden.
    [TestMethod]
    public void ACardThatStaysUpHasNoDismissTimer()
    {
        using var rig = new Rig();
        rig.Presenter.Show(new CardContent("AirPods", Reason), CardAnchor.NearTray);
        rig.Ui.RunAll();
        int restarts = rig.Timer.Restarts.Count;

        rig.Presenter.Show(new CardContent("AirPods", Reason, new CardAction("Stop", () => { }), StaysUp: true), CardAnchor.NearTray);
        rig.Ui.RunAll();

        Assert.HasCount(restarts, rig.Timer.Restarts, "No dismiss interval was started for it.");
        Assert.IsFalse(rig.Timer.Running, "The earlier card's timer was stopped, so it cannot close this one.");
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

    // The launcher is the real one and the program is the built Earshot.exe, in safe mode and against a temporary data folder,
    // where fix prints its plan and ends with PlanOnly without asking anyone. The one real execution of the tray's launcher:
    // it starts a Windows-subsystem program with no --out and nothing redirected, as the tray does. (A test host has a console
    // its child can attach to, so this cannot by itself prove the no-console branch; CommandOutputTests and the run in
    // NoConsoleFixRunTests do.)
    [TestMethod]
    public void TheTrayStartsFixWithNoConsoleAndItRuns()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var runtime = new RuntimeVariablesCleared();
        using var temp = new TempFolder();
        using var ended = new ManualResetEventSlim();
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => exe);
        using var safe = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, temp.File("data"));

        Assert.IsTrue(launcher.Start(new CapturingLog()));

        Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(60)), "The fix run did not end.");
        Assert.AreEqual(ExitCodes.PlanOnly, launcher.LastExitCode, "Fix connection prints its plan and ends with PlanOnly; IoError would mean it found nowhere to write.");
        string logFile = Path.Combine(temp.File("data"), "Local", "Earshot", "logs", "earshot.log");
        string log = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Assert.IsFalse(log.Contains("no console to write to", StringComparison.Ordinal), log);
    }

    // Two presses, two cards and a menu item all reach one launcher, so a second run is refused while the first is open.
    [TestMethod]
    public void TheLauncherRefusesWhileItsRunIsAlive()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        using var ended = new ManualResetEventSlim();
        var log = new CapturingLog();
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => ping, ["-n", "3", "127.0.0.1"]);
        Assert.IsFalse(launcher.IsRunning);

        Assert.IsTrue(launcher.Start(log));
        Assert.IsTrue(launcher.IsRunning);
        Assert.IsFalse(launcher.Start(log), "A second run while the first is open.");
        Assert.IsTrue(log.Has(LogLevel.Info, "still open"));

        Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(30)), "The first run did not end.");
        Assert.IsFalse(launcher.IsRunning);
        Assert.AreEqual(0, launcher.LastExitCode);
    }
}
