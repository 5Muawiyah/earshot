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

// The gauge and a taskbar that keeps going over it. The owner's log showed the taskbar over the gauge again a quarter of
// a second after every raise for about five seconds at a time, with the gauge raised four times a second and, each
// time, visible for a moment and gone again: a flash. Two things are held here: the gauge is made the taskbar's owned
// window, so the system itself keeps it above the taskbar wherever the shell moves the taskbar (an owned window is always
// above its owner in the z-order,
// https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows), and a raise that does not
// hold is not repeated at once but less and less often.
[TestClass]
public sealed class GaugeTaskbarFightTests
{
    private static readonly Rectangle Bar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Start = new(762, 1032, 45, 48);

    private static TaskbarLayout FreeSpace(nint taskbar = 0) =>
        new(taskbar, Bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1920, 1080),
            [Start, new Rectangle(807, 1032, 44, 48), new Rectangle(1678, 1032, 242, 48)], Start,
            96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, GaugeCentreIsGauge: null,
            NotificationArea: new Rectangle(1678, 1032, 242, 48));

    private static TaskbarLayout PollFoundTaskbar(nint taskbar = 0) =>
        FreeSpace(taskbar) with { GaugeCentreIsGauge = false, WindowAtGaugeCentre = new WindowIdentity("Shell_TrayWnd", true) };

    private sealed class FakeCover : IGaugeCoverProbe
    {
        public GaugeCover Next { get; set; } = new(IsGauge: true, RootClassName: "");

        public GaugeCover Probe(ShownGauge gauge) => Next;
    }

    private static readonly GaugeCover TaskbarOver = new(IsGauge: false, RootClassName: "Shell_TrayWnd", BelongsToExplorer: true);
    private static readonly GaugeCover GaugeOnTop = new(IsGauge: true, RootClassName: "");

    private sealed class Rig
    {
        public FakeGaugeSurface Surface { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public CapturingLog Log { get; } = new();

        public FakeCover Cover { get; } = new();

        public GaugeController Controller { get; }

        public Rig(nint taskbar = 0, bool ownerFails = false)
        {
            if (ownerFails)
            {
                Surface.NextSetOwnerResult = StepOutcomes.FromWin32("set-window-long-ptr:gauge-owner", 1400);
            }

            Controller = new GaugeController(
                () => Surface, new FakeTrayIcon(), () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false), Log, Time, Cover);
            Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(taskbar)));
        }

        public int Raises => Surface.Calls.Count(c => c == "Raise");
    }

    // A taskbar that covers the gauge again after every raise, with shell events and polls coming all the while: what the
    // owner's log showed. Raises become rarer as the fight goes on: a handful in the first ten seconds, one in about eight
    // seconds after that, never more than three in any second.
    [TestMethod]
    public void ATaskbarThatCoversTheGaugeAgainAfterEveryRaiseIsAnsweredLessAndLessOften()
    {
        var rig = new Rig();
        rig.Cover.Next = TaskbarOver;
        var raiseTimes = new List<TimeSpan>();
        int seen = 0;
        TimeSpan elapsed = TimeSpan.Zero;

        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
        for (int i = 0; i < 240; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(250));
            elapsed += TimeSpan.FromMilliseconds(250);
            if (i % 4 == 3)
            {
                rig.Controller.OnForegroundChanged("ApplicationManager_DesktopShellWindow");
                rig.Controller.OnLayout(ITaskbarReader.Result.Ok(PollFoundTaskbar()));
            }

            while (seen < rig.Raises)
            {
                raiseTimes.Add(elapsed);
                seen++;
            }
        }

        Assert.IsGreaterThanOrEqualTo(2, raiseTimes.Count, "The first raise is never held back.");
        Assert.IsLessThanOrEqualTo(7, raiseTimes.Count(t => t <= TimeSpan.FromSeconds(10)), "Ten seconds of a taskbar that will not stay down: a handful of raises, not forty.");
        Assert.IsLessThanOrEqualTo(14, raiseTimes.Count, "A minute of it: well under one raise in four seconds on average.");
        foreach (TimeSpan at in raiseTimes)
        {
            Assert.IsLessThanOrEqualTo(3, raiseTimes.Count(t => t > at - TimeSpan.FromSeconds(1) && t <= at), "No more than three raises in any second.");
        }

        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "backed off"), "The log says the raises were slowed and why.");
    }

    // A raise that held leaves nothing behind: the taskbar rising over the gauge again later is answered at once, however
    // many times it does.
    [TestMethod]
    public void ARaiseThatHeldDoesNotSlowTheNextOne()
    {
        var rig = new Rig();
        for (int round = 1; round <= 4; round++)
        {
            rig.Cover.Next = TaskbarOver;
            rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
            Assert.AreEqual(round, rig.Raises, "Round " + round + ": raised at once.");

            // The gauge stays on top for two seconds of checks.
            rig.Cover.Next = GaugeOnTop;
            for (int i = 0; i < 8; i++)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(250));
            }
        }

        Assert.IsFalse(rig.Log.Has(LogLevel.Info, "backed off"));
    }

    // The bar that rose a moment late is still answered by the second look 250 ms after the first.
    [TestMethod]
    public void TheSecondRaiseOfAFightComesAQuarterSecondAfterTheFirst()
    {
        var rig = new Rig();
        rig.Cover.Next = TaskbarOver;

        rig.Controller.OnForegroundChanged("A");
        Assert.AreEqual(1, rig.Raises);
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(2, rig.Raises);
    }

    // ---- The gauge is the taskbar's owned window ----

    [TestMethod]
    public void TheGaugeIsMadeTheOwnedWindowOfTheTaskbarBeforeItIsShownAndOnlyOnce()
    {
        var rig = new Rig(taskbar: 0x777);

        int owner = rig.Surface.Calls.IndexOf("SetOwner 0x777");
        int show = rig.Surface.Calls.FindIndex(c => c.StartsWith("ShowAt", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, owner, "The owner was set. Calls: " + string.Join(" | ", rig.Surface.Calls));
        Assert.IsLessThan(show, owner, "Before the show, so the first frame is already above the bar.");
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "Gauge owned by the taskbar (Shell_TrayWnd)"));

        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));

        Assert.AreEqual(1, rig.Surface.Calls.Count(c => c.StartsWith("SetOwner", StringComparison.Ordinal)), "Not again on every poll.");
    }

    [TestMethod]
    public void ANewTaskbarWindowAfterExplorerStartedAgainIsOwnedInPlaceOfTheOldOne()
    {
        var rig = new Rig(taskbar: 0x777);

        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x888)));

        CollectionAssert.Contains(rig.Surface.Calls, "SetOwner 0x888");
        Assert.AreEqual((nint)0x888, rig.Surface.OwnerWindow);
    }

    [TestMethod]
    public void ALayoutThatNamesNoTaskbarWindowOwnsNothing()
    {
        var rig = new Rig(taskbar: 0);

        Assert.IsFalse(rig.Surface.Calls.Any(c => c.StartsWith("SetOwner", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnOwnerThatCannotBeSetIsOneWarningAndTheGaugeIsStillRaisedWhenTheBarCoversIt()
    {
        var rig = new Rig(taskbar: 0, ownerFails: true);
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));

        Assert.AreEqual(1, rig.Surface.Calls.Count(c => c.StartsWith("SetOwner", StringComparison.Ordinal)), "Not asked again for a window it failed for.");
        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.StartsWith("Gauge could not be owned by the taskbar: ", StringComparison.Ordinal) && e.Message.Contains("(1400)", StringComparison.Ordinal)));
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "A refusal does not hide the gauge.");

        rig.Cover.Next = TaskbarOver;
        rig.Controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
        Assert.AreEqual(1, rig.Raises, "The safety net still works.");
    }

    // Before a wait this thread makes that holds it up (the hand-back at shut down and sleep, the closing of the tray), the gauge
    // is taken off the taskbar and the next layouts do not own it again until the machine has woken.
    [TestMethod]
    public void ReleasingTheOwnerTakesTheGaugeOffTheTaskbarAndKeepsItOffUntilResumed()
    {
        var rig = new Rig(taskbar: 0x777);
        Assert.AreEqual((nint)0x777, rig.Surface.OwnerWindow, "Sanity: owned.");

        rig.Controller.ReleaseOwner();

        Assert.AreEqual((nint)0, rig.Surface.OwnerWindow);
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "taken off the taskbar's ownership"));
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x888)));
        Assert.AreEqual((nint)0, rig.Surface.OwnerWindow, "A layout during the wait does not own it again.");
        Assert.IsInstanceOfType<GaugeState.Shown>(rig.Controller.State, "The gauge is still shown.");

        rig.Controller.ResumeOwner();
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x888)));

        Assert.AreEqual((nint)0x888, rig.Surface.OwnerWindow, "Owned again by the first layout after the machine woke.");
    }

    [TestMethod]
    public void ReleasingTheOwnerOfAGaugeThatWasNeverOwnedOnlyHoldsOffOwning()
    {
        var rig = new Rig(taskbar: 0);

        rig.Controller.ReleaseOwner();
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));

        Assert.IsFalse(rig.Surface.Calls.Any(c => c.StartsWith("SetOwner", StringComparison.Ordinal)), "Nothing to take off, and nothing owned while held off.");
        rig.Controller.ResumeOwner();
        rig.Controller.OnLayout(ITaskbarReader.Result.Ok(FreeSpace(0x777)));
        Assert.AreEqual((nint)0x777, rig.Surface.OwnerWindow);
    }

    [TestMethod]
    public void AGaugeThatCannotBeTakenOffTheTaskbarIsOneWarningWithTheRawCode()
    {
        var rig = new Rig(taskbar: 0x777);
        rig.Surface.NextSetOwnerResult = StepOutcomes.FromWin32("set-window-long-ptr:gauge-owner", 1400);

        rig.Controller.ReleaseOwner();

        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.StartsWith("Gauge could not be taken off the taskbar's ownership: ", StringComparison.Ordinal) && e.Message.Contains("(1400)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ARealGaugeWindowTakesItsOwnerReadsItBackAndReportsTheCodeOfAnOwnerThatIsNotThere()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var owner = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(10, 10, 200, 100) };
            owner.Show();
            using var gauge = new GaugeWindow(new CapturingLog());
            WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
            Application.DoEvents();

            StepOutcome set = gauge.SetOwner(owner.Handle);

            Assert.IsTrue(set.Ok, "SetOwner: " + set.CodeName + " " + set.Detail);
            Assert.AreEqual(owner.Handle, gauge.OwnerWindow);
            Assert.AreEqual((nint)0, GetWindow(gauge.Handle, 3 /* GW_CHILD */), "Owned, not parented.");

            StepOutcome bad = gauge.SetOwner(0x7FFF0);
            Assert.IsFalse(bad.Ok, "A window that is not there cannot own it.");
            Assert.AreNotEqual(0, bad.Code, "The raw code is kept.");
            Assert.AreEqual(owner.Handle, gauge.OwnerWindow, "The owner it had stays.");

            Assert.IsTrue(gauge.SetOwner(0).Ok);
            Assert.AreEqual((nint)0, gauge.OwnerWindow, "Zero clears the owner.");
        });
    }

    // ---- The owner does not join Earshot to the shell's input queue ----

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attachIt);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetActiveWindow(nint window);

    // Owning a window of another process joins the two threads' input queues, which no page says and a probe showed: it is the
    // reason the shell's GetActiveWindow answered with the gauge. Earshot must not stay joined to Explorer. The check is the
    // call that undoes a join: it succeeds only when there was one (so it would also undo it, which is the point), and fails with
    // ERROR_INVALID_PARAMETER when the two are apart. Made right after the owner is set, and again after the gauge has been shown,
    // activated and the shell has raised its own window on its own for a while.
    [TestMethod]
    public void AGaugeOwnedByATaskbarInAnotherProcessIsNotLeftJoinedToItsInputQueue()
    {
        Phase5.CardDesktop.Run(
            desktop =>
            {
                var bar = new Rectangle(100, 300, 700, 48);
                using FakeShellProcess shell = FakeShellProcess.Start(desktop, "Shell_TrayWnd", bar, raiseEveryMs: 100, seconds: 30);
                using var gauge = new GaugeWindow(new CapturingLog());
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
                Application.DoEvents();
                uint shellThread = GetWindowThreadProcessId(shell.Handle, out _);
                uint ownThread = GetWindowThreadProcessId(gauge.Handle, out _);
                Assert.AreNotEqual(0u, shellThread);
                Assert.AreNotEqual(shellThread, ownThread, "Sanity: the shell's window is on another thread, in another process.");

                StepOutcome set = gauge.SetOwner(shell.Handle);

                Assert.IsTrue(set.Ok, "SetOwner: " + set.Step + " " + set.CodeName + " " + set.Detail);
                Assert.AreEqual(shell.Handle, gauge.OwnerWindow, "Still owned: the z-order rule is what the gauge keeps.");
                bool joined = AttachThreadInput(ownThread, shellThread, false);
                Assert.IsFalse(joined, "The gauge's input queue was still joined to the shell's right after it was owned.");
                Assert.AreEqual(87, Marshal.GetLastPInvokeError(), "ERROR_INVALID_PARAMETER: the two were apart.");

                Assert.IsTrue(gauge.ShowAt(new Rectangle(120, 300, 74, 40)).Ok);
                _ = SetActiveWindow(gauge.Handle);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(1))
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                Assert.IsFalse(AttachThreadInput(ownThread, shellThread, false), "Shown, activated and with the shell raising itself, the two joined again.");
                Assert.AreEqual(shell.Handle, gauge.OwnerWindow);
                Assert.IsFalse(IsAbove(shell.Handle, gauge.Handle), "Apart from the shell's input queue, the gauge is still above the shell's window.");
            },
            TimeSpan.FromSeconds(90));
    }

    // A gauge whose input queue cannot be separated from the shell's is not left owned: it stays joined to Explorer otherwise.
    // The code the system gave is kept. ERROR_INVALID_PARAMETER is the one answer that is not a failure: the two were not joined.
    [TestMethod]
    public void AGaugeThatCannotBeSeparatedFromTheShellsInputIsNotLeftOwnedAndTheCodeIsKept()
    {
        Phase5.CardDesktop.Run(
            desktop =>
            {
                using FakeShellProcess shell = FakeShellProcess.Start(desktop, "Shell_TrayWnd", new Rectangle(100, 300, 700, 48), raiseEveryMs: 0, seconds: 20);
                using var gauge = new GaugeWindow(new CapturingLog());
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
                Application.DoEvents();

                gauge.SeparateThreads = (_, _, _) =>
                {
                    Marshal.SetLastPInvokeError(5);
                    return false;
                };
                StepOutcome refused = gauge.SetOwner(shell.Handle);

                Assert.IsFalse(refused.Ok);
                Assert.AreEqual(5, refused.Code, "ERROR_ACCESS_DENIED, as the system said it.");
                StringAssert.StartsWith(refused.Step, "attach-thread-input");
                Assert.AreEqual((nint)0, gauge.OwnerWindow, "Not left owned, so not left joined.");

                gauge.SeparateThreads = (_, _, _) =>
                {
                    Marshal.SetLastPInvokeError(87);
                    return false;
                };
                StepOutcome notJoined = gauge.SetOwner(shell.Handle);

                Assert.IsTrue(notJoined.Ok, "Nothing to separate is not a failure: " + notJoined.CodeName);
                Assert.AreEqual(shell.Handle, gauge.OwnerWindow);
            },
            TimeSpan.FromSeconds(90));
    }

    // ---- The real chain against a taskbar in another process ----

    private sealed class CountingSurface(IGaugeSurface inner) : IGaugeSurface
    {
        public int Raises { get; private set; }

        public bool IsDisposed => inner.IsDisposed;

        public nint WindowHandle => inner.WindowHandle;

        public event EventHandler? LeftClicked
        {
            add => inner.LeftClicked += value;
            remove => inner.LeftClicked -= value;
        }

        public event EventHandler<Point>? RightClicked
        {
            add => inner.RightClicked += value;
            remove => inner.RightClicked -= value;
        }

        public StepOutcome ShowAt(Rectangle bounds) => inner.ShowAt(bounds);

        public StepOutcome MoveTo(Rectangle bounds) => inner.MoveTo(bounds);

        public StepOutcome Raise()
        {
            Raises++;
            return inner.Raise();
        }

        public nint OwnerWindow => inner.OwnerWindow;

        public StepOutcome SetOwner(nint owner) => inner.SetOwner(owner);

        public void Render(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings, int dpi, Rectangle bounds, Color ink, string fontFamily) =>
            inner.Render(snapshot, now, settings, dpi, bounds, ink, fontFamily);

        public void HideWindow() => inner.HideWindow();

        public void Dispose() => inner.Dispose();
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WindowFromPoint(Point point);

    private static bool IsAbove(nint upper, nint lower)
    {
        const uint GwHwndNext = 2;
        for (nint w = GetWindow(upper, GwHwndNext); w != 0; w = GetWindow(w, GwHwndNext))
        {
            if (w == lower)
            {
                return true;
            }
        }

        return false;
    }

    // The whole chain, real: the gauge window, the cover probe and the controller, against a taskbar window in another
    // process that puts itself back on top of the topmost band ten times a second while the shell's events and the poll
    // come as they do in life. Held: the taskbar is under the gauge in nearly every sample, and the gauge was raised hardly
    // at all. Before the gauge was the taskbar's owned window, the same run raised it on every event the limit allowed and
    // the taskbar was over it for most of the time.
    [TestMethod]
    public void AGaugeOverATaskbarInAnotherProcessThatKeepsRaisingItselfStaysAboveWithoutARaiseStorm()
    {
        Phase5.CardDesktop.Run(
            desktop =>
            {
                var bar = new Rectangle(100, 300, 700, 48);
                using FakeShellProcess shell = FakeShellProcess.Start(desktop, "Shell_TrayWnd", bar, raiseEveryMs: 100, seconds: 40);
                var start = new Rectangle(100, 300, 45, 48);
                var notification = new Rectangle(600, 300, 200, 48);
                TaskbarLayout MakeLayout(bool? centreIsGauge, WindowIdentity? over) =>
                    new(shell.Handle, bar, TaskbarEdge.Bottom, false, new Rectangle(0, 0, 1000, 648), [start, new Rectangle(145, 300, 44, 48), notification],
                        start, 96, Shell.QUNS_ACCEPTS_NOTIFICATIONS, Covered: false, centreIsGauge, NotificationArea: notification, WindowAtGaugeCentre: over);

                var log = new CapturingLog();
                var gauge = new GaugeWindow(log);
                WidgetRealSurfaceGuardTests.AllowRealConstruction(WidgetRealSurfaceGuardTests.RealWidgetSurface.GaugeWindow);
                var surface = new CountingSurface(gauge);
                SynchronizationContext context = SynchronizationContext.Current ?? new SynchronizationContext();
                using var controller = new GaugeController(
                    () => surface, new FakeTrayIcon(), () => new GaugeControllerSettings(Enabled: true, LeftClickConnects: false), log, TimeProvider.System,
                    new WindowCoverProbe(), action => context.Post(static state => ((Action)state!)(), action));

                controller.OnLayout(ITaskbarReader.Result.Ok(MakeLayout(null, null)));
                Assert.IsInstanceOfType<GaugeState.Shown>(controller.State);
                Rectangle bounds = ((GaugeState.Shown)controller.State).Bounds;
                Application.DoEvents();

                int samples = 0;
                int taskbarOver = 0;
                var clock = Stopwatch.StartNew();
                long nextEvent = 0;
                long nextPoll = 0;
                while (clock.Elapsed < TimeSpan.FromSeconds(8))
                {
                    Application.DoEvents();
                    long now = clock.ElapsedMilliseconds;
                    if (now >= nextEvent)
                    {
                        controller.OnForegroundChanged("Windows.UI.Core.CoreWindow");
                        nextEvent = now + 100;
                    }

                    if (now >= nextPoll)
                    {
                        Point centre = new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
                        nint atCentre = WindowFromPoint(centre);
                        bool isGauge = atCentre == surface.WindowHandle;
                        controller.OnLayout(ITaskbarReader.Result.Ok(MakeLayout(isGauge, isGauge ? null : new WindowIdentity("Shell_TrayWnd", true))));
                        nextPoll = now + 1000;
                    }

                    samples++;
                    if (IsAbove(shell.Handle, surface.WindowHandle))
                    {
                        taskbarOver++;
                    }

                    Thread.Sleep(1);
                }

                Assert.IsGreaterThan(150, samples, "Sanity: the loop sampled the z-order many times.");
                Assert.IsLessThanOrEqualTo(2, surface.Raises, "Raised: " + surface.Raises + " times in 8 s. Log: " + string.Join(" | ", log.Entries.Select(e => e.Message).Where(m => m.Contains("raise", StringComparison.OrdinalIgnoreCase)).Take(6)));
                Assert.IsLessThanOrEqualTo(samples / 100, taskbarOver, "The taskbar was over the gauge in " + taskbarOver + " of " + samples + " samples.");
            },
            TimeSpan.FromSeconds(90));
    }
}
