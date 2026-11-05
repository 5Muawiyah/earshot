using System.Windows.Forms;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A disconnect whose block did not take leaves the nodes enabled, so the PC can page the AirPods: the failure is never left to a
// cross that may not have been on screen. With no card open and no next step it is held for the card he asks for next, however long
// that is, and it ends when it has been said, not by age or by the AirPods moving. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private const string TheBlockDidNotTake = "Could not block the AirPods. Try again.";

    [TestMethod]
    public void ABlockFailureWithNoNextStepAndNoCardOpenIsHeldWhereAnOrdinaryOneIsNot()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);

            Assert.AreEqual(FailureShown.Held, rig.Fail(TheBlockDidNotTake, null, mustBeSaid: true));
            Assert.IsTrue(rig.Presenter.FailureHeldForTest, "Nothing has said it yet, so it waits.");
            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "No card opens for it: there is no next step to offer.");

            using var ordinary = new ConnectRig(asked: false);
            Assert.AreEqual(FailureShown.NotShown, ordinary.Fail(AReason, null), "A failure that harms nothing is still left to the cross.");
            Assert.IsFalse(ordinary.Presenter.FailureHeldForTest);
        });
    }

    [TestMethod]
    public void AHeldBlockFailureIsSaidOnTheNextAskedCardAtAnyAgeAndWhereverTheAirPodsHaveGone()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var environment = new FakeCardEnvironment { Notifications = State(Shell.QUNS_PRESENTATION_MODE) };
            using var rig = new ConnectRig(environment: environment);
            Assert.AreEqual(FailureShown.Held, rig.Fail(TheBlockDidNotTake, null, mustBeSaid: true));

            rig.Time.Advance(WidgetCardPresenter.HeldFailureLimit * 4);
            rig.Callbacks.Snapshot = Snapshot(AirPodsWhere.Elsewhere);
            rig.Presenter.Refresh();
            rig.OpenAsked();

            Assert.AreEqual(TheBlockDidNotTake, rig.Asked.StatusRow.Text, "An hour later, with the AirPods on the phone, it is still said.");
            Assert.IsNull(rig.Asked.CurrentControls().SingleOrDefault(c => c.Name == "Connect in Windows"), "and it offers no button it does not have.");
        });
    }

    [TestMethod]
    public void ASaidBlockFailureIsOverWhenTheCardClosesAndDoesNotComeBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig();
            rig.Fail(TheBlockDidNotTake, null, mustBeSaid: true);
            rig.OpenAsked();
            Assert.AreEqual(TheBlockDidNotTake, rig.Asked.StatusRow.Text);

            rig.Presenter.Hide();
            Application.DoEvents();
            rig.OpenAsked();

            Assert.AreNotEqual(TheBlockDidNotTake, rig.Asked.StatusRow.Text);
            Assert.IsFalse(rig.Presenter.FailureHeldForTest);
        });
    }
}
