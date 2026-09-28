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
//
// This used to reset and read UiaTaskbarReader.ConstructionCount to prove the rebuild, which meant
// constructing the real class (and, until the reader became injectable, starting a real UI Automation
// poll against the owner's own taskbar the moment TaskbarWatcher.Start() ran). TrayHarness now always
// injects a fake reader (TrayContextTests.TaskbarReaderFactoryCalls), so the same proof - a fresh instance
// is built on the second Enabled, not silently skipped - is read from the fake factory's own call count
// instead, and WidgetRealSurfaceGuardTests's assembly-wide check confirms no real UiaTaskbarReader was
// ever touched by this test.
[TestClass]
public sealed class WidgetRuntimeToggleTests
{
    [TestMethod]
    public void TurningTheWidgetOnAfterStartingOffBuildsTheWatcher()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            Assert.AreEqual(0, tray.TaskbarReaderFactoryCalls, "Sanity: the widget starts off, so nothing is built yet.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls,
                "Turning the widget on after starting off must build a taskbar watcher: WireWidget's construction " +
                "logic must be re-runnable, not something only the constructor ever calls.");
        });
    }

    [TestMethod]
    public void TurningTheWidgetOffStopsTheWatcherAndOnStartsANewOne()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            Assert.AreEqual(1, tray.TaskbarReaderFactoryCalls, "Sanity: starting on builds one watcher.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(2, tray.TaskbarReaderFactoryCalls,
                "Turning the widget off must stop the old watcher (so it is not just left running unpolled), and " +
                "turning it back on must build a fresh one, not silently do nothing because a stale non-null field " +
                "from before it was disposed made WireWidget think the UI side was still wired.");
        });
    }

    // Before the fix, ShowOnTaskbar off meant Enabled off, which stopped the data
    // pipeline outright: the low battery alert, the case-open card and auto-pause died silently while their
    // own menu items stayed checked. The case-open card in particular does not need the gauge to exist at
    // all (it places itself near the tray with no gauge to anchor above), so it must stay wired here even
    // with the gauge fully off.
    [TestMethod]
    public void TheCaseOpenCardStaysWiredWithTheGaugeOffWhenAnotherConsumerWantsTheWatcher()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = (s.Widget with { ShowOnTaskbar = false, CaseOpenCard = true }).WithWatcherRecomputed());
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.Widget.Enabled, "Sanity: CaseOpenCard alone must still want the watcher.");
            Assert.IsTrue(tray.Context.WidgetDataPipelineWiredForTest, "The data pipeline must be wired for the case-open card to have anything to show.");
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardWiredForTest, "The case-open card itself must be wired even with the gauge off.");
            Assert.AreEqual(0, tray.TaskbarReaderFactoryCalls, "The gauge itself must stay off: nothing here asked for it.");
        });
    }
}
