using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// "Refresh battery" in the tray menu proved wired through the real TrayContext: the click opens the card and the card is
// reading the battery, asked of the real status service. Every tray runs on a private desktop.
[TestClass]
public sealed class WidgetRefreshWiringTests
{
    private static void WidgetOn(EarshotSettings s) =>
        s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true };

    [TestMethod]
    public void TheMenuItemOpensTheCardAndStartsReadingTheBattery()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn);
            tray.PumpUntilIdle();
            Assert.IsFalse(tray.Context.WidgetCardIsShownForTest);
            Assert.IsNull(tray.Context.WidgetCardRefreshViewForTest);

            tray.ClickMenu("Refresh battery");

            TrayHarness.PumpUntil(() => tray.Context.WidgetCardIsShownForTest, "The card never opened.");
            Assert.IsTrue(tray.Context.WidgetCardRefreshViewForTest!.Reading, "The click started a refresh that is reading.");
        });
    }

    [TestMethod]
    public void TheMenuItemIsNotThereWhileTheGaugeIsOff()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { ShowOnTaskbar = false });
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();

            Assert.IsFalse(tray.Context.Menu.Items.Any(i => i.Available && i.Text == "Refresh battery"));
        });
    }
}
