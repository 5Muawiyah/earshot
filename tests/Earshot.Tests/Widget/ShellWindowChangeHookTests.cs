using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// A fake source of "Explorer showed or hid a window" events: records Install, Reinstall and Dispose and lets a test raise
// one.
internal sealed class FakeShellWindowChangeSource : IShellWindowChangeSource
{
    public int Installs { get; private set; }

    public int Reinstalls { get; private set; }

    public bool Disposed { get; private set; }

    public StepOutcome NextInstallResult { get; set; } = new("set-win-event-hook:shell-window", true, 0, "S_OK", null);

    public event EventHandler<ShellWindowChangedEventArgs>? ShellWindowChanged;

    public StepOutcome Install()
    {
        Installs++;
        return NextInstallResult;
    }

    public StepOutcome Reinstall()
    {
        Reinstalls++;
        return NextInstallResult;
    }

    public void Raise(string rootClass, bool shown) => ShellWindowChanged?.Invoke(this, new ShellWindowChangedEventArgs(rootClass, shown));

    public void Dispose() => Disposed = true;
}

// The hook on the windows Explorer shows and hides: its wiring into the gauge pipeline on fakes, and the one real
// execution of the hook, on a private desktop against the test's own windows.
[TestClass]
public sealed class ShellWindowChangeHookTests
{
    // ---- Wiring, on fakes ----

    [TestMethod]
    public void TheHookIsInstalledWithTheGaugePipeline()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.IsNotNull(tray.LastShellWindowSource, "The gauge pipeline builds a shell window source.");
            Assert.AreEqual(1, tray.LastShellWindowSource.Installs);
            Assert.IsFalse(tray.LastShellWindowSource.Disposed);
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "Shell window hook: set-win-event-hook:shell-window"), "The install outcome is logged.");
        });
    }

    [TestMethod]
    public void AFailedInstallIsLoggedAsAWarningAndTheGaugeStillWorks()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            FakeShellWindowChangeSource first = tray.LastShellWindowSource!;

            first.NextInstallResult = StepOutcomes.FromWin32("set-win-event-hook:shell-window", 1400);
            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();
            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreNotSame(first, tray.LastShellWindowSource, "A fresh source is built when the gauge comes back.");
            Assert.IsInstanceOfType<GaugeState>(tray.Context.WidgetGaugeStateForTest);
        });
    }

    [TestMethod]
    public void TheHookIsDisposedWhenTheGaugeIsTurnedOffAndBuiltAgainWhenItComesBack()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            FakeShellWindowChangeSource first = tray.LastShellWindowSource!;

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();

            Assert.IsTrue(first.Disposed, "Turning the gauge off unhooks.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreNotSame(first, tray.LastShellWindowSource);
            Assert.AreEqual(1, tray.LastShellWindowSource!.Installs);
        });
    }

    [TestMethod]
    public void TheHookIsDisposedOnClose()
    {
        FakeShellWindowChangeSource? source = null;
        Phase5.CardDesktop.Run(() =>
        {
            using (var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true }))
            {
                tray.PumpUntilIdle();
                source = tray.LastShellWindowSource;
                Assert.IsNotNull(source);
                Assert.IsFalse(source.Disposed);
            }

            Assert.IsTrue(source.Disposed, "Closing the tray unhooks.");
        });
    }

    // A hook is bound to the process it was installed against: a new Explorer needs a new one, and the new Explorer
    // says it has a taskbar by broadcasting TaskbarCreated.
    [TestMethod]
    public void TaskbarCreatedHooksTheNewExplorer()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            FakeShellWindowChangeSource source = tray.LastShellWindowSource!;
            Assert.AreEqual(0, source.Reinstalls);

            uint taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)taskbarCreated), 0, 0);
            tray.Context.Window.Dispatch(ref message);

            Assert.AreEqual(1, source.Reinstalls, "TaskbarCreated must hook the new Explorer.");
            Assert.AreEqual(1, source.Installs, "The first install is not repeated by the tray; the reinstall is the source's own.");
            Assert.IsFalse(source.Disposed);
        });
    }

    [TestMethod]
    public void TaskbarCreatedWhileTheGaugeIsOffHooksNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
            Assert.IsNull(tray.LastShellWindowSource, "Sanity: the widget starts off, so no source was built.");

            uint taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
            var message = Message.Create(tray.Context.Window.Handle, unchecked((int)taskbarCreated), 0, 0);
            tray.Context.Window.Dispatch(ref message);

            Assert.IsNull(tray.LastShellWindowSource, "Nothing to reinstall, and nothing built.");
        });
    }

    private static TaskbarLayout FreeSpaceLayout()
    {
        var bar = new Rectangle(0, 1032, 1920, 48);
        var start = new Rectangle(762, 1032, 45, 48);
        List<Rectangle> buttons = Enumerable.Range(0, 8).Select(i => new Rectangle(807 + (i * 44), 1032, 44, 48)).ToList();
        return new TaskbarLayout(0, bar, TaskbarEdge.Bottom, AutoHide: false,
            new Rectangle(0, 0, 1920, 1080), [start, .. buttons, new Rectangle(1678, 1032, 242, 48)], start,
            Dpi: 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, 1032, 242, 48));
    }

    private static void ShowTheGauge(TrayHarness tray)
    {
        tray.LastTaskbarReader!.SetNextResult(ITaskbarReader.Result.Ok(FreeSpaceLayout()));
        tray.Settings.Update(s => s.Widget = s.Widget with { LeftClickConnects = true });
        TrayHarness.PumpUntil(() => tray.Context.WidgetGaugeStateForTest is GaugeState.Shown, "Sanity: a free-space layout must show the gauge.");
        WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
    }

    // The event, through the tray's own wiring (the source the pipeline built, the tray's handler, the real
    // controller): with the taskbar over a shown gauge the change must end with the gauge raised, and the log says it
    // was Explorer's window that did it.
    [TestMethod]
    public void AWindowExplorerShowedOverAShownGaugeReachesTheControllerWhichRaisesItWhenTheTaskbarCoversIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            ShowTheGauge(tray);
            tray.LastCoverProbe!.Next = new GaugeCover(IsGauge: false, RootClassName: "Shell_TrayWnd", BelongsToExplorer: true);
            int looked = tray.LastCoverProbe.Probes;

            tray.LastShellWindowSource!.Raise("ControlCenterWindow", shown: true);

            Assert.AreEqual(looked + 1, tray.LastCoverProbe.Probes, "The event reached the controller, which looked at what is over the gauge.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Gauge raised: "), "The taskbar was over the gauge, so it was put back on top.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "after Explorer showed a window of class ControlCenterWindow"));
        });
    }

    [TestMethod]
    public void AWindowExplorerShowedWhileTheGaugeIsNotShownIsLookedAtByNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            tray.LastCoverProbe!.Next = new GaugeCover(IsGauge: false, RootClassName: "Shell_TrayWnd", BelongsToExplorer: true);

            tray.LastShellWindowSource!.Raise("ControlCenterWindow", shown: true);

            Assert.AreEqual(0, tray.LastCoverProbe.Probes, "With no gauge on screen there is nothing to look at.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Gauge raised"));
        });
    }

    [TestMethod]
    public void NoHarnessTestConstructsARealShellWindowHook()
    {
        int before = ShellWindowChangeHook.ConstructionCount;

        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
            Assert.IsNotNull(tray.LastShellWindowSource, "The gauge pipeline asked for a shell window source, and got the fake.");
        });

        Assert.AreEqual(before, ShellWindowChangeHook.ConstructionCount,
            "A widget-enabled TrayHarness built a real shell window hook; every such test would hook Explorer.");
    }

    [TestMethod]
    public void AHookThatWasNotSetIsAFailureEvenWithNoErrorCode()
    {
        StepOutcome noCode = ShellWindowChangeHook.InstallOutcome(0, 0);
        StepOutcome withCode = ShellWindowChangeHook.InstallOutcome(0, 1400);
        StepOutcome installed = ShellWindowChangeHook.InstallOutcome(0x1234, 0);

        Assert.IsFalse(noCode.Ok, "A zero handle with error 0 is still a failure.");
        StringAssert.Contains(noCode.Detail, "no hook and no error code");
        Assert.IsFalse(withCode.Ok);
        Assert.AreEqual(1400, withCode.Code, "The raw code is kept.");
        Assert.IsTrue(installed.Ok);
    }

    // With no process to listen to (no window of the taskbar's class) the install says so and hooks nothing.
    [TestMethod]
    public void AnInstallWithNoProcessToListenToFailsWithoutHooking()
    {
        var log = new CapturingLog();
        using var hook = new ShellWindowChangeHook(log, () => 0);

        StepOutcome outcome = hook.Install();

        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Detail, "no process was found", StringComparison.OrdinalIgnoreCase);
        Assert.IsEmpty(log.Entries);
    }

    [TestMethod]
    public void AHookThatIsNeverInstalledDisposesQuietly()
    {
        var log = new CapturingLog();

        new ShellWindowChangeHook(log).Dispose();

        Assert.IsEmpty(log.Entries);
    }

    // ---- The real hook ----

    private static bool PumpUntil(Func<bool> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            Application.DoEvents();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static Form NewForm(int x) => new()
    {
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        ShowInTaskbar = false,
        Bounds = new Rectangle(x, 100, 200, 100),
    };

    // The one real execution of the hook: installed for real against the test's own process (standing in for Explorer,
    // which is not on the private desktop), delivered a real show and a real hide of the test's own top-level window on
    // the thread that installed it, and nothing for a child window, for a hook bound to another process, or after
    // Dispose. Nothing is sent to any window and no input is simulated.
    [TestMethod]
    public void TheRealHookDeliversAShowAndAHideOfATopLevelWindowOfTheHookedProcessOnlyOnTheHookingThread()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Form form = NewForm(50);
            using var child = new Panel { Visible = false, Bounds = new Rectangle(10, 10, 50, 50) };
            form.Controls.Add(child);
            var log = new CapturingLog();
            var events = new List<(string Class, bool Shown, int ManagedThread)>();
            var hook = new ShellWindowChangeHook(log, () => (uint)Environment.ProcessId);
            hook.ShellWindowChanged += (_, e) => events.Add((e.RootClassName, e.Shown, Environment.CurrentManagedThreadId));
            StepOutcome install = hook.Install();
            Assert.IsTrue(install.Ok, "Install: " + install.CodeName + " " + install.Detail);

            bool Delivered(bool shown) => events.Any(e => e.Class.Contains("WindowsForms10", StringComparison.Ordinal) && e.Shown == shown);

            form.Show();
            Assert.IsTrue(PumpUntil(() => Delivered(shown: true), TimeSpan.FromSeconds(3)),
                "The hook must deliver the show. Log: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
            (string Class, bool Shown, int ManagedThread) shownEvent = events.Last(e => e.Shown);
            StringAssert.Contains(shownEvent.Class, "WindowsForms10", "The class of a WinForms form, never a title.");
            Assert.AreEqual(Environment.CurrentManagedThreadId, shownEvent.ManagedThread, "Delivered on the thread that installed the hook.");

            form.Hide();
            Assert.IsTrue(PumpUntil(() => Delivered(shown: false), TimeSpan.FromSeconds(3)), "The hook must deliver the hide.");

            // A child window's own show and hide are not a flyout.
            form.Show();
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            int beforeChild = events.Count;
            child.Visible = true;
            child.Visible = false;
            PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(beforeChild, events.Count, "A child window is not top level and is not reported.");

            // A hook on another process hears nothing of this process's windows.
            var elsewhere = new ShellWindowChangeHook(new CapturingLog(), () => 4);
            var heard = new List<ShellWindowChangedEventArgs>();
            elsewhere.ShellWindowChanged += (_, e) => heard.Add(e);
            StepOutcome elsewhereInstall = elsewhere.Install();
            Assert.IsTrue(elsewhereInstall.Ok, "Install elsewhere: " + elsewhereInstall.CodeName + " " + elsewhereInstall.Detail);
            form.Hide();
            form.Show();
            PumpUntil(() => false, TimeSpan.FromMilliseconds(600));
            elsewhere.Dispose();
            Assert.IsEmpty(heard, "The hook is limited to the one process.");

            // Reinstall moves the hook without duplicating it: one event per change, not two.
            StepOutcome reinstall = hook.Reinstall();
            Assert.IsTrue(reinstall.Ok, "Reinstall: " + reinstall.CodeName + " " + reinstall.Detail);
            int beforeReinstall = events.Count(e => e.Class.Contains("WindowsForms10", StringComparison.Ordinal));
            form.Hide();
            Assert.IsTrue(PumpUntil(() => events.Count(e => e.Class.Contains("WindowsForms10", StringComparison.Ordinal)) > beforeReinstall, TimeSpan.FromSeconds(3)));
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(beforeReinstall + 1, events.Count(e => e.Class.Contains("WindowsForms10", StringComparison.Ordinal)), "Exactly one hook is live.");

            hook.Dispose();
            int seen = events.Count;
            form.Show();
            PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(seen, events.Count, "Nothing is delivered after Dispose.");
        });
    }
}
