using System.Windows.Forms;
using Earshot.App;
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

    // The menu, the card and the settings page share one launcher, so a second press while the first run is open says so.
    [TestMethod]
    public void ChoosingFixConnectionWhileARunIsOpenSaysSoAndStartsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            var launcher = new FakeFixLauncher { Running = true };
            tray.Context.FixLauncher = launcher;
            tray.Context.Menu.Refresh();

            tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.FixConnection).PerformClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(0, launcher.Started);
            Assert.AreEqual(FixOffer.AlreadyRunningMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void PairAirPodsIsInTheMenuUnderFixConnectionAndRaisesItsOwnCommand()
    {
        StaThread.Run(() =>
        {
            MenuState state = MenuModel.Build(Target(ConnectionState.Disconnected), null, null, Settings(), busy: false, StartupState.Off);
            using var menu = new TrayMenu(() => state);
            menu.Refresh();
            var items = menu.Items.OfType<ToolStripMenuItem>().ToList();
            ToolStripMenuItem fix = items.Single(i => i.Text == MenuModel.FixConnection);
            ToolStripMenuItem pair = items.Single(i => i.Text == MenuModel.PairAirPods);
            Assert.AreEqual("Pair AirPods...", pair.Text);
            var all = menu.Items.ToList();
            Assert.AreEqual(all.IndexOf(fix) + 1, all.IndexOf(pair), "Right under Fix connection.");
            Assert.IsTrue(pair.Available);
            Assert.IsTrue(pair.Enabled);
            var raised = 0;
            menu.PairAirPodsClicked += (_, _) => raised++;

            pair.PerformClick();

            Assert.AreEqual(1, raised);
        });
    }

    [TestMethod]
    public void PairAirPodsIsDisabledWhileBusyOrAnElevatedRunIsOpenAndSaysWhy()
    {
        MenuState busy = MenuModel.Build(Target(ConnectionState.Disconnected), null, null, Settings(), busy: true, StartupState.Off);
        MenuState elevated = MenuModel.Build(Target(ConnectionState.Disconnected), null, null, Settings(), busy: false, StartupState.Off, elevatedRun: "setup");
        MenuState idle = MenuModel.Build(Target(ConnectionState.Disconnected), null, null, Settings(), busy: false, StartupState.Off);

        Assert.IsFalse(busy.PairAirPods.Enabled);
        Assert.IsFalse(elevated.PairAirPods.Enabled);
        Assert.AreEqual("Pair AirPods... (finishing the setup first)", elevated.PairAirPods.Text);
        Assert.IsTrue(idle.PairAirPods.Enabled);
    }
}
