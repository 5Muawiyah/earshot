using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using static Earshot.Tests.Phase1.Phase1Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// TaskbarCreated, WM_SETTINGCHANGE and WM_DISPLAYCHANGE, already raised by ShellMessageWindow for the
// tray icon's own listeners (TrayContext's constructor wires RefreshIcon to all three), must also poke the
// widget's TaskbarWatcher for an immediate re-measure: none of the three arrives on the watcher's own
// polling schedule, so without this a new taskbar (Explorer restarting), a text-scale or theme change, or a
// monitor/resolution change would leave the gauge showing a stale layout for up to a second.
//
// Drives the real TrayContext.Window (a harness-built ShellMessageWindow) exactly the way
// TrayContextTests already dispatches WM_CLOSE to it: Window.Dispatch(ref message), the same real WndProc
// a genuine broadcast would reach. The widget is built for real (TrayHarness's default is off, so this
// asks for it explicitly, the same as WidgetRuntimeToggleTests): an extra "Taskbar read took" debug line in
// the log is the observable proof a poke actually reached the watcher's real background thread, since
// TaskbarWatcher exposes no read-count of its own. TrayHarness's fake taskbar reader and advertisement
// source (WidgetRealSurfaceGuardTests) still let the watcher run and log every read; only the real UI
// Automation and Bluetooth calls are skipped. Run on a private desktop (Earshot.Tests.Phase5.CardDesktop.Run),
// never StaThread's own interactive-desktop thread: WireWidget still registers a real AppBarRegistration
// (ABM_NEW) alongside the watcher, and a private desktop is where that call is refused rather than reaching
// the owner's real Explorer (AppBarRegistrationTests's own header).
//
// TaskbarWatcher's own natural poll cadence is 1 s (ShownPollIntervalMs): a naive "does a read happen within
// 5 s of dispatching" assertion would pass even with the wiring removed, since the watcher would read again
// on its own schedule regardless. Each test therefore first proves a short window (ControlWindow, well under
// the 1 s cadence) sees no read on its own - the control - before dispatching and requiring one within that
// same short window, so only an actual poke from the dispatched message can explain a pass.
[TestClass]
public sealed class WidgetShellSignalTests
{
    private const string ReadLogFragment = "Taskbar read took";
    private static readonly TimeSpan ControlWindow = TimeSpan.FromMilliseconds(350);

    [TestMethod]
    public void SettingChangedPokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            var settingChanged = Message.Create(tray.Context.Window.Handle, NativeMethods.WM_SETTINGCHANGE, 0, 0);
            tray.Context.Window.Dispatch(ref settingChanged);
        }, "WM_SETTINGCHANGE");

    [TestMethod]
    public void DisplayChangedPokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            var displayChanged = Message.Create(tray.Context.Window.Handle, NativeMethods.WM_DISPLAYCHANGE, 0, 0);
            tray.Context.Window.Dispatch(ref displayChanged);
        }, "WM_DISPLAYCHANGE");

    [TestMethod]
    public void TaskbarCreatedPokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            // The same registered message id ShellMessageWindow itself computes for "TaskbarCreated"
            // (Windows caches RegisterWindowMessage by name, so this returns the identical value).
            uint taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
            Assert.AreNotEqual(0u, taskbarCreated, "Sanity: RegisterWindowMessage must succeed on this machine.");
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)taskbarCreated), 0, 0);
            tray.Context.Window.Dispatch(ref message);
        }, "TaskbarCreated");

    // The appbar notification callback (AppBarRegistration.CallbackMessage), ABN_STATECHANGE and
    // ABN_POSCHANGED: neither arrives on the watcher's own schedule either, so both must poke it the same
    // way TaskbarCreated, WM_SETTINGCHANGE and WM_DISPLAYCHANGE already do.
    [TestMethod]
    public void AppBarStateChangePokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_STATECHANGE, 0);
            tray.Context.Window.Dispatch(ref message);
        }, "ABN_STATECHANGE");

    [TestMethod]
    public void AppBarPosChangedPokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_POSCHANGED, 0);
            tray.Context.Window.Dispatch(ref message);
        }, "ABN_POSCHANGED");

    // A full-screen application closing (ABN_FULLSCREENAPP, lParam 0) must poke for a fresh read rather than
    // forcing the gauge back on: the taskbar's actual state still needs re-reading.
    [TestMethod]
    public void AppBarFullScreenClosingPokesTheTaskbarWatcher() =>
        AssertDispatchPokes(tray =>
        {
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)AppBarRegistration.CallbackMessage), Shell.ABN_FULLSCREENAPP, 0);
            tray.Context.Window.Dispatch(ref message);
        }, "ABN_FULLSCREENAPP closing");

    private static void AssertDispatchPokes(Action<TrayHarness> dispatch, string signalName)
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            Assert.IsTrue(SpinWait.SpinUntil(() => ReadCount(tray) >= 1, TimeSpan.FromSeconds(5)),
                "Sanity: WireWidget's own initial Poke must already have produced a read.");

            int beforeControl = ReadCount(tray);
            Thread.Sleep(ControlWindow);
            tray.PumpUntilIdle();
            Assert.AreEqual(beforeControl, ReadCount(tray),
                "Control: " + ControlWindow.TotalMilliseconds + " ms must be well under the watcher's own 1 s cadence, " +
                "or a read here would not distinguish the watcher's own schedule from an actual poke.");

            dispatch(tray);
            tray.PumpUntilIdle();

            Assert.IsTrue(SpinWait.SpinUntil(() => ReadCount(tray) > beforeControl, ControlWindow),
                signalName + " must poke the taskbar watcher for an immediate re-measure, well within the control window.");
        });
    }

    private static int ReadCount(TrayHarness tray) =>
        tray.Log.Entries.Count(e => e.Message.Contains(ReadLogFragment, StringComparison.Ordinal));
}
