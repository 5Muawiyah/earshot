using System.Windows.Forms;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// The widget's own settings menu (MenuModel/ContextMenu, wired in TrayContext.cs), driven end to end
// through a real TrayContext exactly the way TrayContextTests already drives every other toggle: ClickMenu
// refreshes the menu, finds the item by its text and calls PerformClick(), the harness's settings store is
// a real JsonSettingsStore under %TEMP% (TrayHarness's own TempFolder), never the real
// %LOCALAPPDATA%\Earshot, and PumpUntilIdle drains OnSettingsChanged's posted callback (ApplyWidget among
// them) before each assertion. Run on a private desktop (Earshot.Tests.Phase5.CardDesktop.Run): the first
// test below turns the widget on through the real menu click, which reaches WireWidget's real
// AppBarRegistration (ABM_NEW) the same way AppBarWiringTests does, and a private desktop is where
// AppBarRegistrationTests's own header records that call being refused rather than reaching the owner's
// real Explorer. The other tests here never turn the widget on, but run the same way for consistency.
[TestClass]
public sealed class WidgetMenuTests
{
    // This used to check UiaTaskbarReader.ConstructionCount, which meant constructing the real class.
    // TrayHarness now always injects a fake reader (TrayContextTests.TaskbarReaderFactoryCalls), so the same
    // proof - the menu click actually wires the widget, not just flips the setting - reads the fake
    // factory's own call count instead. The menu item now writes ShowOnTaskbar, not Enabled directly;
    // Enabled follows it (WithWatcherRecomputed) since nothing else here asks for the watcher independently.
    //
    // The tray.Context.Menu.Refresh() plus tray.MenuItem(...).Checked pair below matches
    // AFirstRunTurnsOpenOnStartupOn's own precedent (TrayContextTests.cs): checking only
    // tray.Settings.Current.Widget.ShowOnTaskbar proves the click wrote the setting, never that the real
    // tray menu's own checkbox was ever told to reflect it. ContextMenu.Apply's Set(_showOnTaskbar, ...)
    // call could vanish entirely - the menu item left showing whatever it showed before, the owner's own
    // tickmark silently lying about the state of the gauge - with every assertion this test had before
    // this addition, and PerformClick's own Enabled/Available sanity check inside ClickMenu, still green:
    // Set assigns Enabled and Available together with Checked, so only the click into a disabled or
    // unavailable item is caught that way, never a Checked value stuck on the wrong side of a real click.
    [TestMethod]
    public void ClickingShowOnTheTaskbarFlipsShowOnTaskbarThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            Assert.IsFalse(tray.Settings.Current.Widget.ShowOnTaskbar, "The harness starts the gauge off (see TrayHarness).");
            Assert.IsFalse(tray.Settings.Current.Widget.Enabled, "The harness starts the watcher off (see TrayHarness).");

            tray.ClickMenu(WidgetCopy.ShowOnTaskbar);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.Widget.ShowOnTaskbar);
            Assert.IsTrue(tray.Settings.Current.Widget.Enabled, "Turning the gauge on must also turn the watcher on for it.");
            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls,
                "Turning it on through the menu must actually wire the gauge (WireGauge), not just flip the flag.");
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem(WidgetCopy.ShowOnTaskbar).Checked,
                "The real menu item's own tickmark must actually turn on, not just the setting behind it.");

            tray.ClickMenu(WidgetCopy.ShowOnTaskbar);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.Widget.ShowOnTaskbar);
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.MenuItem(WidgetCopy.ShowOnTaskbar).Checked,
                "The real menu item's own tickmark must actually turn off again too.");
            Assert.IsFalse(tray.Settings.Current.Widget.Enabled,
                "Turning the gauge back off must also turn the watcher off, since nothing else here wants it.");
        });
    }

    [TestMethod]
    public void ClickingLeftClickConnectsFlipsItThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            bool before = tray.Settings.Current.Widget.LeftClickConnects;

            tray.ClickMenu(WidgetCopy.LeftClickConnects);
            tray.PumpUntilIdle();

            Assert.AreEqual(!before, tray.Settings.Current.Widget.LeftClickConnects);
        });
    }

    [TestMethod]
    public void ClickingCardWhenTheCaseOpensFlipsItThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            bool before = tray.Settings.Current.Widget.CaseOpenCard;

            tray.ClickMenu(WidgetCopy.CardWhenCaseOpens);
            tray.PumpUntilIdle();

            Assert.AreEqual(!before, tray.Settings.Current.Widget.CaseOpenCard);
        });
    }

    [TestMethod]
    public void ClickingLowBatteryAlertFlipsItThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            bool before = tray.Settings.Current.Widget.LowBatteryAlert;

            tray.ClickMenu(WidgetCopy.LowBatteryAlert);
            tray.PumpUntilIdle();

            Assert.AreEqual(!before, tray.Settings.Current.Widget.LowBatteryAlert);
        });
    }

    // The set-up item is in the menu whatever the widget is doing. With the widget off there is no watcher, so
    // it reads disabled with its reason, and a click cannot start a listen that would fail.
    [TestMethod]
    public void TheSetUpItemIsDisabledWithItsReasonWhileNothingRuns()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            tray.Context.Menu.Refresh();

            Assert.IsFalse(tray.MenuItem(WidgetCopy.SetUpBatteryBluetoothOff).Enabled, "No watcher: the item is disabled and says why.");
        });
    }

    [TestMethod]
    public void TheSetUpItemIsEnabledWhileTheWatcherRuns()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();

            Assert.IsTrue(tray.MenuItem(WidgetCopy.SetUpBattery).Enabled, "With the watcher running the item is enabled.");
        });
    }

    [TestMethod]
    public void TheSetUpItemOpensTheCardAtListening()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.IsFalse(tray.Context.WidgetCardIsShownForTest);

            tray.ClickMenu(WidgetCopy.SetUpBattery);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Context.WidgetCardIsShownForTest, "The set-up opens the widget card.");
            Assert.AreEqual(WidgetCardView.SetupListening, tray.Context.WidgetCardViewForTest, "At the first page: listening.");
        });
    }

    // The threshold submenu sits under the top-level item, so it is reached through Context.Menu directly
    // rather than ClickMenu (which only looks at the menu's own top-level items).
    [TestMethod]
    public void ClickingAThresholdEntrySetsItThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.Widget = s.Widget with { LowBatteryAlert = true, LowBatteryThresholdPercent = 20 });
            tray.Context.Menu.Refresh();

            ToolStripMenuItem seventy = tray.Context.Menu.LowBatteryThresholdItems.Single(i => i.Text == "70%");
            Assert.IsTrue(seventy.Enabled);
            seventy.PerformClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(70, tray.Settings.Current.Widget.LowBatteryThresholdPercent);
        });
    }
}
