using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A failure said on a full card nobody asked for stays there until the failure itself ends, whatever the card is: the case opening, the
// case closing and the case card's close time do not take it away unread. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    private const int FiveSeconds = 5;

    private static readonly TimeSpan PastFiveSeconds = TimeSpan.FromSeconds(FiveSeconds + 1);

    private static ConnectRig CaseCardWithAFailure(int closeSeconds = CaseOpenCardClose.UntilCaseCloses)
    {
        var rig = new ConnectRig(asked: false, closeSeconds: closeSeconds);
        rig.Presenter.CaseOpened(AGauge);
        Application.DoEvents();
        Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the case card is open.");
        Assert.AreEqual(FailureShown.Shown, rig.Fail(AReason, "Connect in Windows"));
        Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text, "Sanity: the case card says the failure.");
        return rig;
    }

    [TestMethod]
    public void TheCaseCardsCloseTimeDoesNotCloseAFailureDrawnOnIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ConnectRig rig = CaseCardWithAFailure(FiveSeconds);

            rig.Time.Advance(PastFiveSeconds);
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The failure is still unread.");
            Assert.AreEqual(AReason, rig.Unasked.StatusRow.Text);
        });
    }

    [TestMethod]
    public void TheCaseClosingDoesNotCloseAFailureDrawnOnTheCaseCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ConnectRig rig = CaseCardWithAFailure();

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown);
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);
        });
    }

    [TestMethod]
    public void ACaseOpeningDoesNotTakeOverAFullCardThatIsSayingAFailureWhateverItsKind()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ConnectRig rig = CaseCardWithAFailure(FiveSeconds);

            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            rig.Time.Advance(PastFiveSeconds);
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The second opening did not start the close time again.");
            Assert.IsTrue(rig.Log.Has(LogLevel.Debug, "Card: not shown (case), a failure is on the card"), string.Join("\n", rig.Log.Entries.Select(e => e.Message)));
        });
    }

    [TestMethod]
    public void WhenTheFailureEndsWithTheCaseStillOpenTheCaseCardGoesBackToItsCloseTime()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ConnectRig rig = CaseCardWithAFailure(FiveSeconds);
            rig.Time.Advance(PastFiveSeconds);

            rig.Presenter.ToggleStarted(connect: true);
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The case is open and its time has not run since the failure ended.");

            rig.Time.Advance(PastFiveSeconds);
            Application.DoEvents();
            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "Then it closes as a case card does.");
        });
    }

    [TestMethod]
    public void WhenTheFailureEndsAfterTheCaseClosedTheCaseCardCloses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using ConnectRig rig = CaseCardWithAFailure();
            rig.Presenter.CaseClosed();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown);

            rig.Presenter.ToggleStarted(connect: true);
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "The case it was waiting on has closed.");
        });
    }
}
