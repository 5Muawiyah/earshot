using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase5;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The result of his own click is shown whatever the notification state or a full-screen application say, and it stays so: the
// full-screen recheck does not close it. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    [TestMethod]
    public void TheFullScreenRecheckLeavesTheResultOfHisClickOpenAndClosesACardNobodyClickedFor()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var rig = new NoticeRig(environment: new FakeCardEnvironment());
            Assert.IsTrue(rig.Show(Notice("Connected", CardAnchor.NearCursor, new Point(960, 1056))));
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the result of his click is open.");

            rig.Environment.Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN);
            rig.Presenter.RecheckFullScreen();
            Application.DoEvents();

            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "A full-screen application starting does not take his click's result away.");

            // The control: a card nobody clicked for does close at the same recheck.
            rig.Presenter.HideCompact();
            Application.DoEvents();
            rig.Environment.Notifications = State(Shell.QUNS_ACCEPTS_NOTIFICATIONS);
            Assert.IsTrue(rig.Show(Notice("Checking")));
            Application.DoEvents();
            Assert.IsTrue(rig.Presenter.IsUnaskedShown, "Sanity: the other notice is open.");
            rig.Environment.Notifications = State(Shell.QUNS_RUNNING_D3D_FULL_SCREEN);
            rig.Presenter.RecheckFullScreen();
            Application.DoEvents();

            Assert.IsFalse(rig.Presenter.IsUnaskedShown, "It closes at the recheck.");
        });
    }
}
