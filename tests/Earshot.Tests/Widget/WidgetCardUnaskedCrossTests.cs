using System.Windows.Forms;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A failure that is waiting for the gauge's cross to have had its time, and the card he asks for opens meanwhile: that card says
// the failure, its button works, and closing the card ends it. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    [TestMethod]
    public void TheAskedCardOpenedDuringTheCrossSaysTheFailureWithAButtonThatWorks()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            int pressed = 0;
            Assert.AreEqual(FailureShown.Pending, rig.Fail(AReason, "Connect in Windows", () => pressed++));

            rig.OpenAsked();

            Assert.AreEqual(AReason, rig.Asked.StatusRow.Text);
            rig.Asked.CurrentControls().Single(c => c.Name == "Connect in Windows").Activate();
            Application.DoEvents();

            Assert.AreEqual(1, pressed, "The next step it offers runs.");
            Assert.IsFalse(rig.Presenter.FailureActiveForTest, "and the failure is over.");
        });
    }

    [TestMethod]
    public void ACaseCardOpenedDuringTheCrossNeverDrawsANextStepButtonThatDoesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            int pressed = 0;
            Assert.AreEqual(FailureShown.Pending, rig.Fail(AReason, "Connect in Windows", () => pressed++));

            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the case card opened during the cross.");

            Assert.IsNull(rig.Unasked.CurrentControls().SingleOrDefault(c => c.Name == "Connect in Windows"), "No button while the cross has its time.");

            rig.PassTheCross();
            Application.DoEvents();

            Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text, "Once the cross ends the open card says the failure.");
            rig.Unasked.CurrentControls().Single(c => c.Name == "Connect in Windows").Activate();
            Application.DoEvents();
            Assert.AreEqual(1, pressed, "and its button works.");
        });
    }

    [TestMethod]
    public void ACardClosedAfterSayingTheFailureDuringTheCrossDoesNotSayItAgainOnReopenOrOpenACardWhenTheCrossEnds()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.Fail(AReason, "Connect in Windows");
            rig.OpenAsked();
            Assert.AreEqual(AReason, rig.Asked.StatusRow.Text);

            rig.Presenter.Hide();
            Application.DoEvents();
            Assert.IsFalse(rig.Presenter.IsShown);
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The cross ended and the failure had been said: no card opens for it.");

            rig.OpenAsked();

            Assert.AreNotEqual(AReason, rig.Asked.StatusRow.Text, "It does not come back on reopen.");
            Assert.IsFalse(rig.Presenter.FailureActiveForTest);
            Assert.IsFalse(rig.Presenter.FailureHeldForTest);
        });
    }
}
