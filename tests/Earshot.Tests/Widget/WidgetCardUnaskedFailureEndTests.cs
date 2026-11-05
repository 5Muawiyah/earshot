using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A failure said on a card ends when that card closes, by every way of closing. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private static readonly TimeSpan PastTheDoubleClick = TimeSpan.FromSeconds(2);

    private static void AssertTheFailureDidNotComeBack(ConnectRig rig, string how)
    {
        Assert.IsFalse(rig.Presenter.IsShown, how + ": the asked card closed.");
        Assert.IsFalse(rig.Presenter.FailureActiveForTest, how + ": the failure is over.");
        Assert.IsFalse(rig.Presenter.FailureHeldForTest, how + ": it is not held for later either.");
        rig.Time.Advance(PastTheDoubleClick);
        rig.OpenAsked();
        Assert.AreNotEqual(AReason, rig.Asked.StatusRow.Text, how + ": reopened later, the old reason is not said again.");
    }

    [TestMethod]
    public void AFailureSaidOnTheAskedCardEndsWhenEscClosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            Assert.AreEqual(FailureShown.Shown, rig.Fail(AReason, "Connect in Windows"));
            Assert.AreEqual(AReason, rig.Asked.StatusRow.Text);

            Phase5.TestWindows.Send(rig.Asked.Handle, 0x0100, (nint)Keys.Escape, 0);
            Application.DoEvents();

            AssertTheFailureDidNotComeBack(rig, "Esc");
        });
    }

    [TestMethod]
    public void AFailureSaidOnTheAskedCardEndsWhenClickingAwayClosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            Assert.AreEqual(FailureShown.Shown, rig.Fail(AReason, "Connect in Windows"));

            using var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(400, 50), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Application.DoEvents();

            AssertTheFailureDidNotComeBack(rig, "Click away");
        });
    }

    [TestMethod]
    public void AFailureSaidOnTheFailedCardEndsWhenItsCloseButtonClosesIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the failed card opened.");
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);

            Rectangle close = rig.Unasked.CurrentMainLayout.Gear;
            ClickAt(rig.Unasked.Handle, new Point(close.X + (close.Width / 2), close.Y + (close.Height / 2)));
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
            Assert.IsFalse(rig.Presenter.FailureActiveForTest, "Closed with its close button, the failure is over.");
            rig.OpenAsked();
            Assert.AreNotEqual(AReason, rig.Asked.StatusRow.Text);
        });
    }

    [TestMethod]
    public void AFailedCardClosedByTheFullScreenRecheckTakesItsFailureWithIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the failed card opened.");
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);

            rig.Environment.Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN);
            rig.Presenter.RecheckFullScreen();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "Sanity: the recheck closed it.");
            Assert.IsFalse(rig.Presenter.FailureActiveForTest, "The failure it said is over with the card.");
            rig.Environment.Notifications = State(Shell.QUNS_ACCEPTS_NOTIFICATIONS);
            rig.OpenAsked();
            Assert.AreNotEqual(AReason, rig.Asked.StatusRow.Text, "It does not come back on the next card.");
        });
    }

    // The asked card closed by losing focus, and the second click of the volume-icon gesture does not reopen it: that click opened
    // no card, so it must not mark a failure still waiting for the cross as said.
    [TestMethod]
    public void AClickThatReopensNoCardDoesNotMarkAWaitingFailureAsSaid()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.OpenAsked();
            using var other = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(400, 50), ClientSize = new Size(50, 50), ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.HasPendingToggleCloseWindow, "Sanity: losing focus closed the asked card.");

            Assert.AreEqual(FailureShown.Pending, rig.Fail(AReason, "Connect in Windows"));
            rig.Presenter.RequestShow(AGauge, AGauge.Location);
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsShown, "Sanity: the click was taken as the second half of the gesture.");
            Assert.IsFalse(rig.Presenter.FailureActiveForTest, "No card said the failure.");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The failure still gets its card when the cross has had its time.");
            Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text);
        });
    }

    // The failure wins over the case opening: the case closing is not what ends it.
    [TestMethod]
    public void ACaseOpeningOverAFailedCardLeavesTheFailureToItsOwnEndAndTheCaseClosingDoesNotCloseIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the failed card opened.");

            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The case closing did not close the failure.");
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);
            Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text);

            Rectangle close = rig.Unasked.CurrentMainLayout.Gear;
            ClickAt(rig.Unasked.Handle, new Point(close.X + (close.Width / 2), close.Y + (close.Height / 2)));
            Application.DoEvents();
            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "Its own close button ends it.");
        });
    }

    [TestMethod]
    public void TheLogClaimsTheGaugesCrossOnlyForAToggleThatEndedFailed()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var failed = new ConnectRig(asked: false);
            failed.Presenter.ToggleStarted(connect: true);
            failed.Presenter.ToggleEnded(connect: true, ToggleOutcome.Failed);
            Assert.AreEqual(FailureShown.NotShown, failed.Fail(AReason, next: null));
            Assert.IsTrue(failed.Log.Has(LogLevel.Debug, "so the gauge's cross says it"), "A failed toggle is told by the cross, and the log says so.");

            using var partial = new ConnectRig(asked: false);
            partial.Presenter.ToggleStarted(connect: true);
            partial.Presenter.ToggleEnded(connect: true, ToggleOutcome.Partial);
            Assert.AreEqual(FailureShown.NotShown, partial.Fail(AReason, next: null));
            Assert.IsFalse(partial.Log.Has(LogLevel.Debug, "cross says it"), "A part-way toggle draws no cross, so the log does not claim one.");
            Assert.IsTrue(partial.Log.Has(LogLevel.Debug, "no cross is drawn"));
        });
    }
}
