using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// Fix connection is reachable from the tray menu, not only from the failure card: a person whose AirPods stick on Connecting
// needs it before any Connect has failed twice. It is always there and starts the same fix run the card's button starts.
[TestClass]
public sealed class TrayFixMenuTests
{
    [TestMethod]
    public void TheMenuOffersFixConnectionAfterSetUpOrRepairAndRaisesItsOwnCommand()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            // The tray's own handler runs too: a stand-in launcher, so no process is started.
            tray.Context.FixLauncher = new Earshot.Tests.Integration.Coordinator.FakeFixLauncher();
            TrayMenu menu = tray.Context.Menu;
            menu.Refresh();
            ToolStripMenuItem fix = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.FixConnection);
            Assert.IsTrue(fix.Available);
            Assert.IsTrue(fix.Enabled);
            var raised = new List<string>();
            menu.FixConnectionClicked += (_, _) => raised.Add("fix");

            fix.PerformClick();

            Assert.HasCount(1, raised);
            Assert.AreEqual("fix", raised[0]);
        });
    }

    [TestMethod]
    public void ChoosingFixConnectionInTheMenuStartsTheFixRun()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            var launcher = new FakeFixLauncher();
            tray.Context.FixLauncher = launcher;
            tray.Context.Menu.Refresh();

            tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.FixConnection).PerformClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, launcher.Started);
        });
    }
}
