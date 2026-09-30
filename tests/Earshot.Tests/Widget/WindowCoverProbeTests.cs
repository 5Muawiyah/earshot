using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The real WindowCoverProbe, and the real chain the gauge's re-raise rests on (foreground hook, controller, probe),
// executed on a private desktop against the test's own windows: a form standing in for the gauge, another form
// standing in for a flyout, and a window of the class the shell's taskbar has (Shell_TrayWnd), registered by the test
// itself on that desktop. Nothing here touches the owner's desktop or sends input; SetForegroundWindow is called
// on the test's own window only. The real GaugeWindow's own Raise is proved in GaugeWindowTests.
[TestClass]
public sealed class WindowCoverProbeTests
{
    private const string TaskbarClass = "Shell_TrayWnd";

    // A process id that is not this one: the tests that play the shell from this process use it so the window they
    // register as the taskbar is not taken for one of Earshot's own.
    private const uint NotThisProcess = uint.MaxValue;

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    // A window of the taskbar's class, topmost, over bounds. The class uses the system's own window procedure, so the
    // window does nothing but exist and be found where it is.
    private sealed class FakeTaskbarWindow : IDisposable
    {
        private readonly nint _instance = GetModuleHandleW(null);

        public FakeTaskbarWindow(Rectangle bounds)
        {
            nint user32 = NativeLibrary.Load("user32.dll");
            var windowClass = new WndClass
            {
                WndProc = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                Instance = _instance,
                ClassName = TaskbarClass,
            };
            if (RegisterClassW(ref windowClass) == 0)
            {
                throw new AssertFailedException("RegisterClassW failed with Win32 error " + Marshal.GetLastPInvokeError() + ".");
            }

            Handle = CreateWindowExW(WsExTopmost | WsExToolWindow, TaskbarClass, "", WsPopup | WsVisible,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, _instance, 0);
            if (Handle == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                UnregisterClassW(TaskbarClass, _instance);
                throw new AssertFailedException("CreateWindowExW failed with Win32 error " + error + ".");
            }
        }

        public nint Handle { get; private set; }

        public void Dispose()
        {
            if (Handle != 0)
            {
                DestroyWindow(Handle);
                Handle = 0;
                UnregisterClassW(TaskbarClass, _instance);
            }
        }
    }

    private static Form NewForm(Rectangle bounds) => new()
    {
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        ShowInTaskbar = false,
        TopMost = true,
        Bounds = bounds,
    };

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

    // The gauge stand-in, and where another window would have to be to sit over its centre.
    private static readonly Rectangle GaugeBounds = new(300, 300, 74, 40);

    private static readonly Rectangle OverTheGauge = new(280, 280, 200, 100);

    [TestMethod]
    public void TheRealProbeSaysWhatIsAtTheGaugesCentreTheGaugeAFlyoutOrTheTaskbar()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Form gauge = NewForm(GaugeBounds);
            gauge.Show();
            Application.DoEvents();
            var shown = new ShownGauge(GaugeBounds, gauge.Handle);
            var probe = new WindowCoverProbe();
            var shellProbe = new WindowCoverProbe(NotThisProcess);

            GaugeCover alone = probe.Probe(shown);
            Assert.IsTrue(alone.IsGauge, "Nothing over it: the gauge itself is at its centre.");

            // Another program's window over it (no taskbar on this desktop yet, so it is not the shell's).
            using (Form flyout = NewForm(OverTheGauge))
            {
                flyout.Show();
                Application.DoEvents();
                GaugeCover over = probe.Probe(shown);
                Assert.IsFalse(over.IsGauge);
                StringAssert.Contains(over.RootClassName, "WindowsForms10", "The class of the window over it, never a title.");
                Assert.IsFalse(over.BelongsToExplorer, "With no taskbar window on the desktop nothing belongs to the shell.");
                Assert.IsTrue(over.BelongsToThisProcess, "The window is one of this process's own, which is never a cover.");
                Assert.IsFalse(shellProbe.Probe(shown).BelongsToThisProcess, "Seen as another process's window when the own process is another.");
            }

            Application.DoEvents();
            Assert.IsTrue(probe.Probe(shown).IsGauge, "The flyout is gone, so the gauge is at its centre again.");

            // A window of the taskbar's class over it.
            using var taskbar = new FakeTaskbarWindow(OverTheGauge);
            Application.DoEvents();
            GaugeCover covered = shellProbe.Probe(shown);
            Assert.IsFalse(covered.IsGauge);
            Assert.AreEqual(TaskbarClass, covered.RootClassName, "The taskbar's class is what makes the controller raise the gauge.");
            Assert.IsTrue(covered.BelongsToExplorer, "The window is in the same process as the taskbar window, which is what belonging to the shell means.");
        });
    }

    [TestMethod]
    public void TheRealProbeAnswersForAGaugeWhoseHandleIsAChildOfNothingAndForAPointNoWindowOwns()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var probe = new WindowCoverProbe();

            // Bounds off every window of this desktop and a handle that is not there: never the gauge, and no failure.
            GaugeCover nothing = probe.Probe(new ShownGauge(new Rectangle(-3000, -3000, 74, 40), 0x7FFF0));

            Assert.IsFalse(nothing.IsGauge);
        });
    }

    // The whole chain, real but for the surface: a real foreground change (the test's own window becoming the
    // foreground window) reaches the controller through the real hook, the controller asks the real probe what is
    // over the gauge's centre, finds a window of the taskbar's class, and raises the gauge. A hook that does not
    // deliver, a probe that does not see the class, or a controller that does not use the answer each fail this.
    [TestMethod]
    public void ARealForegroundChangeToTheTaskbarOverTheGaugeIsRaisedThroughTheRealHookAndTheRealProbe()
    {
        Phase5.CardDesktop.Run(() =>
        {
            // A free-space layout small enough to sit on the private desktop.
            var bar = new Rectangle(0, 600, 1000, 48);
            var start = new Rectangle(100, 600, 45, 48);
            var notification = new Rectangle(800, 600, 200, 48);
            var layout = new TaskbarLayout(0, bar, TaskbarEdge.Bottom, AutoHide: false, new Rectangle(0, 0, 1000, 648),
                [start, new Rectangle(145, 600, 44, 48), notification], start,
                Dpi: 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null, NotificationArea: notification);
            var surface = new FakeGaugeSurface();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            var controller = new GaugeController(
                () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false, GaugePosition.RightEnd), log, time, new WindowCoverProbe(NotThisProcess));
            controller.OnLayout(ITaskbarReader.Result.Ok(layout));
            Assert.IsInstanceOfType<GaugeState.Shown>(controller.State, "Sanity: the layout shows the gauge.");
            Rectangle gaugeBounds = ((GaugeState.Shown)controller.State).Bounds;

            using Form gaugeStandIn = NewForm(gaugeBounds);
            gaugeStandIn.Show();
            surface.WindowHandle = gaugeStandIn.Handle;
            using var taskbar = new FakeTaskbarWindow(bar);
            Application.DoEvents();

            var hook = new ForegroundChangeHook(log, NativeMethods.WINEVENT_OUTOFCONTEXT);
            var delivered = new List<string>();
            hook.ForegroundChanged += (_, e) =>
            {
                delivered.Add(e.RootClassName);
                controller.OnForegroundChanged(e.RootClassName);
            };
            try
            {
                Assert.IsTrue(hook.Install().Ok, "The hook installs.");

                // What makes the taskbar rise in life is another program's window taking the foreground (Start, a
                // flyout), not the taskbar itself: a window elsewhere becomes the foreground window, and the
                // taskbar is over the gauge.
                using Form trigger = NewForm(new Rectangle(20, 20, 100, 100));
                trigger.Show();
                SetForegroundWindow(trigger.Handle);

                Assert.IsTrue(PumpUntil(() => surface.Calls.Contains("Raise"), TimeSpan.FromSeconds(5)),
                    "The gauge was never raised. Delivered: " + string.Join(", ", delivered) + ". Probe: " + new WindowCoverProbe(NotThisProcess).Probe(new ShownGauge(gaugeBounds, gaugeStandIn.Handle)) +
                    ". Log: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
                Assert.IsTrue(log.Has(LogLevel.Info, "was over it after a foreground change to"), "The log says why it was raised.");
            }
            finally
            {
                hook.Dispose();
                controller.Dispose();
            }
        });
    }
    // The same chain with one of Earshot's own windows over the gauge (its tooltip, the card) instead of the taskbar:
    // the real hook delivers the foreground change, the real probe sees a window of this process, and the gauge is
    // not raised over it.
    [TestMethod]
    public void ARealForegroundChangeWithAnOwnWindowOverTheGaugeRaisesNothing()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var bar = new Rectangle(0, 600, 1000, 48);
            var start = new Rectangle(100, 600, 45, 48);
            var notification = new Rectangle(800, 600, 200, 48);
            var layout = new TaskbarLayout(0, bar, TaskbarEdge.Bottom, AutoHide: false, new Rectangle(0, 0, 1000, 648),
                [start, new Rectangle(145, 600, 44, 48), notification], start,
                Dpi: 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null, NotificationArea: notification);
            var surface = new FakeGaugeSurface();
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            var probe = new RecordingProbe(new WindowCoverProbe());
            var controller = new GaugeController(
                () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(true, false, GaugePosition.RightEnd), log, time, probe);
            controller.OnLayout(ITaskbarReader.Result.Ok(layout));
            Rectangle gaugeBounds = ((GaugeState.Shown)controller.State).Bounds;

            using Form gaugeStandIn = NewForm(gaugeBounds);
            gaugeStandIn.Show();
            surface.WindowHandle = gaugeStandIn.Handle;
            using Form ownTooltip = NewForm(new Rectangle(gaugeBounds.X - 10, gaugeBounds.Y - 10, gaugeBounds.Width + 20, gaugeBounds.Height + 20));
            ownTooltip.Show();
            Application.DoEvents();

            var hook = new ForegroundChangeHook(log, NativeMethods.WINEVENT_OUTOFCONTEXT);
            hook.ForegroundChanged += (_, e) => controller.OnForegroundChanged(e.RootClassName);
            try
            {
                Assert.IsTrue(hook.Install().Ok, "The hook installs.");
                using Form trigger = NewForm(new Rectangle(20, 20, 100, 100));
                trigger.Show();
                SetForegroundWindow(trigger.Handle);

                Assert.IsTrue(PumpUntil(() => probe.Last is not null, TimeSpan.FromSeconds(5)), "The controller looked at what is over the gauge.");
                PumpUntil(() => false, TimeSpan.FromMilliseconds(600));
                Assert.IsTrue(probe.Last!.Value.BelongsToThisProcess, "The probe saw one of this process's own windows over the gauge.");
                Assert.DoesNotContain("Raise", surface.Calls, "Raising the gauge over its own tooltip would hide the tooltip.");
                Assert.IsFalse(log.Has(LogLevel.Info, "Gauge raised"));
            }
            finally
            {
                hook.Dispose();
                controller.Dispose();
            }
        });
    }

    // The hook on the windows Explorer shows and hides finds Explorer the way the tray does, by the process that owns the
    // window of the taskbar's class. Here that window is the test's own, registered on the private desktop, so the
    // process found is this one and the test's own flyout stand-in is heard: the whole default path, real.
    [TestMethod]
    public void TheRealShellWindowHookFindsTheProcessOfTheTaskbarWindowAndHearsItsFlyouts()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var taskbar = new FakeTaskbarWindow(new Rectangle(0, 600, 1000, 48));
            var log = new CapturingLog();
            var events = new List<ShellWindowChangedEventArgs>();
            var hook = new ShellWindowChangeHook(log);
            hook.ShellWindowChanged += (_, e) => events.Add(e);
            try
            {
                StepOutcome install = hook.Install();
                Assert.IsTrue(install.Ok, "Install: " + install.CodeName + " " + install.Detail);

                using Form flyout = NewForm(new Rectangle(20, 20, 100, 100));
                flyout.Show();

                Assert.IsTrue(PumpUntil(() => events.Any(e => e.Shown && e.RootClassName.Contains("WindowsForms10", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
                    "The flyout stand-in belongs to the taskbar window's process, so it is heard. Log: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
            }
            finally
            {
                hook.Dispose();
            }
        });
    }

    private sealed class RecordingProbe(IGaugeCoverProbe inner) : IGaugeCoverProbe
    {
        public GaugeCover? Last { get; private set; }

        public GaugeCover Probe(ShownGauge gauge)
        {
            GaugeCover cover = inner.Probe(gauge);
            Last = cover;
            return cover;
        }
    }
}
