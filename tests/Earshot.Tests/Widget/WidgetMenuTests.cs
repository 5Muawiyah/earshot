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
    // Round 3: this used to check UiaTaskbarReader.ConstructionCount, which meant constructing the real
    // class. TrayHarness now always injects a fake reader (TrayContextTests.TaskbarReaderFactoryCalls), so
    // the same proof - the menu click actually wires the widget, not just flips the setting - reads the
    // fake factory's own call count instead.
    [TestMethod]
    public void ClickingShowOnTheTaskbarFlipsWidgetEnabledThroughTheRealSettingsWritePath()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness();
            Assert.IsFalse(tray.Settings.Current.Widget.Enabled, "The harness starts the widget off (see TrayHarness).");

            tray.ClickMenu(WidgetCopy.ShowOnTaskbar);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.Widget.Enabled);
            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls,
                "Turning it on through the menu must actually wire the widget (WireWidget), not just flip the flag.");

            tray.ClickMenu(WidgetCopy.ShowOnTaskbar);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.Widget.Enabled);
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
