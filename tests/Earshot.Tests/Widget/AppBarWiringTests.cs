using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// AppBarRegistration's wiring into TrayContext: registered when the widget's gauge machinery is wired up
// (matching TaskbarWatcher's own lifetime exactly, Enabled toggle included), reregistered on TaskbarCreated,
// and its notification callback routed to an immediate hide for ABN_FULLSCREENAPP. Drives the real
// TrayContext.Window the same way WidgetShellSignalTests already does (Window.Dispatch, the real WndProc),
// against a real AppBarRegistration (ABM_NEW/ABM_REMOVE), never a fake: both calls are cheap, already proved
// safe for a test window by AppBarRegistrationTests, and TrayHarness.Dispose (via TrayContext.Close ->
// CloseWidget) always removes whatever is left registered by the time a test ends.
[TestClass]
public sealed class AppBarWiringTests
{
    // The appbar notification fast path reaches the gauge controller synchronously, on the same thread that
    // dispatches the message: it never posts to TaskbarWatcher's own background thread or waits for a read.
    // No PumpUntilIdle after Dispatch, no SpinWait, no timeout: if the wiring instead only poked the watcher
    // and waited for its next read, this would need a real read to complete first and could show the old
    // state at the point this asserts; asserting immediately is what proves the notification itself moved
    // the controller, not a read that happened to land afterwards.
    [TestMethod]
    public void FullScreenAppOpeningHidesTheGaugeControllerAtOnce()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            // WireWidget's initial Poke() starts a real background read; its result only reaches the
            // controller once TaskbarWatcher posts it back to this thread, so this pumps until that first
            // OnLayout has actually run (Off is the controller's starting state before any OnLayout call).
            TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is not (null or GaugeState.Off),
                "Sanity: the widget's own first taskbar read never reached the gauge controller.");

            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            tray.Context.Window.Dispatch(ref message);

            GaugeState? state = tray.Context.WidgetGaugeStateForTest;
            Assert.IsInstanceOfType<GaugeState.Hidden>(state, "ABN_FULLSCREENAPP opening must hide the gauge at once.");
            Assert.AreEqual(HiddenReason.NotificationState, ((GaugeState.Hidden)state!).Reason);
        });
    }

    // A stray notification while the widget is off must do nothing: Off has no surface and no controller
    // state worth moving into Hidden.
    [TestMethod]
    public void FullScreenAppOpeningDoesNothingWhileTheWidgetIsOff()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            Assert.IsNull(tray.Context.WidgetGaugeStateForTest, "Sanity: the widget starts off, so WireWidget never built a controller.");

            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 1);
            tray.Context.Window.Dispatch(ref message);

            Assert.IsNull(tray.Context.WidgetGaugeStateForTest, "Still nothing to hide: the widget was never wired up.");
        });
    }

    // Explorer's own appbar list is new after it restarts: TaskbarCreated must remove the old registration
    // and add a new one, real SHAppBarMessage calls both, alongside (not instead of) the existing
    // ResetBackoff/Poke calls the same event already drives (WidgetShellSignalTests.TaskbarCreatedPokesTheTaskbarWatcher).
    [TestMethod]
    public void TaskbarCreatedReregistersTheAppBar()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.AreEqual(1, CountAppBarLog(tray, "abm-new"), "Sanity: WireWidget must already have registered the appbar once.");
            Assert.AreEqual(0, CountAppBarLog(tray, "abm-remove"), "Sanity: nothing has removed it yet.");

            uint taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
            Assert.AreNotEqual(0u, taskbarCreated, "Sanity: RegisterWindowMessage must succeed on this machine.");
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)taskbarCreated), 0, 0);
            tray.Context.Window.Dispatch(ref message);

            Assert.AreEqual(1, CountAppBarLog(tray, "abm-remove"), "TaskbarCreated must remove the old registration.");
            Assert.AreEqual(2, CountAppBarLog(tray, "abm-new"), "TaskbarCreated must add a new one: one from WireWidget, one from the reregister.");
        });
    }

    // AppBarRegistration's lifetime is tied to Enabled exactly the way TaskbarWatcher's own start/stop
    // already is (WidgetRuntimeToggleTests, which proves the same off/on cycle the same way: a fresh
    // UiaTaskbarReader is built the second time, not silently skipped because a stale field looked wired).
    // AppBarRegistration.Dispose() does not log its own ABM_REMOVE outcome (mirroring TaskbarWatcher.Dispose,
    // GaugeController.Dispose: neither logs on the way out either), so a fresh ABM_NEW on the second Enabled
    // is what proves WireWidget's "if (_taskbarWatcher is null)" guard was re-armed by the off branch clearing
    // _appBarRegistration, the same guard that gates rebuilding TaskbarWatcher itself.
    [TestMethod]
    public void TogglingTheWidgetOffAndOnRegistersAFreshAppBar()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.AreEqual(1, CountAppBarLog(tray, "abm-new"), "Sanity: starting on registers once.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false });
            tray.PumpUntilIdle();

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();

            Assert.AreEqual(2, CountAppBarLog(tray, "abm-new"),
                "Turning the widget off and back on must register a fresh appbar, the same way TaskbarWatcher's " +
                "own field is rebuilt rather than reused.");
        });
    }

    private static int CountAppBarLog(TrayHarness tray, string fragment) =>
        tray.Log.Entries.Count(e => e.Message.Contains("AppBar: sh-app-bar-message:" + fragment, StringComparison.Ordinal));
}
