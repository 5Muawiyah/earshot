using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// TrayContext.Widget.cs's WireWidget/ApplyWidget: a harness-built TrayContext starts with the widget off
// (TrayHarness's own default, WidgetRealSurfaceGuardTests), so turning the setting on afterwards is the
// only way this codebase ever asks WireWidget to run a second time. Before the fix, WireWidget's
// construction only ever ran from the constructor: a widget that started disabled had no TaskbarWatcher,
// GaugeController or WidgetCardPresenter for a later "turn it on" to act on, and turning an already-running
// widget off left the UI Automation polling thread running (ApplyWidget only ever called Poke()).
[TestClass]
public sealed class WidgetRuntimeToggleTests
{
    [TestMethod]
    public void TurningTheWidgetOnAfterStartingOffBuildsTheWatcher()
    {
        UiaTaskbarReader.ConstructionCount = 0;
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            Assert.AreEqual(0, UiaTaskbarReader.ConstructionCount, "Sanity: the widget starts off, so nothing is built yet.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(1, UiaTaskbarReader.ConstructionCount,
                "Turning the widget on after starting off must build a taskbar watcher: WireWidget's construction " +
                "logic must be re-runnable, not something only the constructor ever calls.");
        });
    }

    [TestMethod]
    public void TurningTheWidgetOffStopsTheWatcherAndOnStartsANewOne()
    {
        UiaTaskbarReader.ConstructionCount = 0;
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.AreEqual(1, UiaTaskbarReader.ConstructionCount, "Sanity: starting on builds one watcher.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false });
            tray.PumpUntilIdle();

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(2, UiaTaskbarReader.ConstructionCount,
                "Turning the widget off must stop the old watcher (so it is not just left running unpolled), and " +
                "turning it back on must build a fresh one, not silently do nothing because a stale non-null field " +
                "from before it was disposed made WireWidget think the UI side was still wired.");
        });
    }
}
