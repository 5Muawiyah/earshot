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
}
