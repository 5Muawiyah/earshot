using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The case closing closes the card the case opened and nothing else: a notice that stays up and the card a failure opened are not
// the case's, so they stay until they are closed. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    [TestMethod]
    public void TheCaseClosingLeavesANoticeThatStaysUpOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            CompactRequest stays = CompactNoticePresenter.RequestFor(new CardContent("Earshot", "Needs you", null, true), CardAnchor.NearTray, null, null);
            Assert.IsTrue(rig.Presenter.ShowCompact(stays).GetAwaiter().GetResult());
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the notice is open.");

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The case closing is not what closes a notice that stays up.");
        });
    }

    [TestMethod]
    public void TheCaseClosingLeavesTheCardAFailureOpenedOpen()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Fail(AReason, "Connect in Windows");
            rig.PassTheCross();
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the failed card is open.");

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "The case closing does not take the failure away from him.");
            Assert.IsTrue(rig.Presenter.FailureActiveForTest);
        });
    }

    [TestMethod]
    public void TheCaseClosingStillClosesTheCaseCard()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new ConnectRig(asked: false);
            rig.Presenter.CaseOpened(AGauge);
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the case card is open.");

            rig.Presenter.CaseClosed();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown);
        });
    }
}
