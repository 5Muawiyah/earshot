using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// A fake foreground change source: records Install and Dispose and lets a test raise a change.
internal sealed class FakeForegroundChangeSource : IForegroundChangeSource
{
    public int Installs { get; private set; }

    public bool Disposed { get; private set; }

    public StepOutcome NextInstallResult { get; set; } = new("set-win-event-hook:foreground", true, 0, "S_OK", null);

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public StepOutcome Install()
    {
        Installs++;
        return NextInstallResult;
    }

    public void Raise(string rootClass) => ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(1, rootClass, 1));

    public void Dispose() => Disposed = true;
}

// The foreground hook: its wiring into the gauge pipeline on fakes, and the one real execution of the hook, on
// a private desktop against the test's own two windows.
[TestClass]
public sealed class ForegroundChangeHookTests
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

            Assert.IsNotNull(tray.LastForegroundSource, "The gauge pipeline builds a foreground source.");
            Assert.AreEqual(1, tray.LastForegroundSource.Installs);
            Assert.IsFalse(tray.LastForegroundSource.Disposed);
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "Foreground hook: set-win-event-hook:foreground"), "The install outcome is logged.");
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
            FakeForegroundChangeSource first = tray.LastForegroundSource!;

            first.NextInstallResult = StepOutcomes.FromWin32("set-win-event-hook:foreground", 1400);
            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();
            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreNotSame(first, tray.LastForegroundSource, "A fresh source is built when the gauge comes back.");
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
            FakeForegroundChangeSource first = tray.LastForegroundSource!;

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false });
            tray.PumpUntilIdle();

            Assert.IsTrue(first.Disposed, "Turning the gauge off unhooks.");

            tray.Settings.Update(s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            Assert.AreNotSame(first, tray.LastForegroundSource);
            Assert.AreEqual(1, tray.LastForegroundSource!.Installs);
        });
    }

    [TestMethod]
    public void TheHookIsDisposedOnClose()
    {
        FakeForegroundChangeSource? source = null;
        Phase5.CardDesktop.Run(() =>
        {
            using (var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true }))
            {
                tray.PumpUntilIdle();
                source = tray.LastForegroundSource;
                Assert.IsNotNull(source);
                Assert.IsFalse(source.Disposed);
            }

            Assert.IsTrue(source.Disposed, "Closing the tray unhooks.");
        });
    }

    [TestMethod]
    public void AForegroundChangeReachesTheControllerWithoutTouchingAnythingWhenTheGaugeIsNotShown()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();

            tray.LastForegroundSource!.Raise("Chrome_WidgetWin_1");

            Assert.IsNotInstanceOfType<GaugeState.Shown>(tray.Context.WidgetGaugeStateForTest, "Nothing was shown to put back on top.");
        });
    }

    [TestMethod]
    public void NoHarnessTestConstructsARealForegroundHook()
    {
        int before = ForegroundChangeHook.ConstructionCount;

        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true });
            tray.PumpUntilIdle();
        });

        Assert.AreEqual(before, ForegroundChangeHook.ConstructionCount,
            "A widget-enabled TrayHarness built a real foreground hook; every such test would hook the desktop.");
    }

    // ---- The real hook ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Pumps messages on this thread (out-of-context hook events arrive as messages) until the condition holds
    // or the time is up.
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

    // The one real execution of the hook: installed for real, delivered a real foreground change between the
    // test's own two windows, on the thread that installed it. Then the same change with the production flags
    // (WINEVENT_SKIPOWNPROCESS) delivers nothing, which executes the documented filter the design leans on:
    // the tray's own windows never trigger it. SetForegroundWindow is called on the test's own window; no
    // input is simulated anywhere.
    [TestMethod]
    public void TheRealHookDeliversAForegroundChangeOnTheHookingThreadAndTheProductionFlagsFilterOwnWindows()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using Form a = NewForm(50);
            using Form b = NewForm(400);
            a.Show();
            b.Show();
            Application.DoEvents();

            // Without WINEVENT_SKIPOWNPROCESS: the test's own windows count.
            var log = new CapturingLog();
            var events = new List<(ForegroundChangedEventArgs Args, int ManagedThread)>();
            var hook = new ForegroundChangeHook(log, NativeMethods.WINEVENT_OUTOFCONTEXT);
            hook.ForegroundChanged += (_, e) => events.Add((e, Environment.CurrentManagedThreadId));
            StepOutcome install = hook.Install();
            Assert.IsTrue(install.Ok, "Install: " + install.CodeName + " " + install.Detail);

            SetForegroundWindow(a.Handle);
            Assert.IsTrue(PumpUntil(() => events.Any(e => e.Args.Hwnd == a.Handle), TimeSpan.FromSeconds(3)),
                "The hook must deliver the change to the first window. Log: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
            SetForegroundWindow(b.Handle);
            Assert.IsTrue(PumpUntil(() => events.Any(e => e.Args.Hwnd == b.Handle), TimeSpan.FromSeconds(3)), "The hook must deliver the change to the second window.");

            (ForegroundChangedEventArgs args, int managedThread) = events.Last(e => e.Args.Hwnd == b.Handle);
            StringAssert.Contains(args.RootClassName, "WindowsForms10", "The root class of a WinForms form, never a title.");
            Assert.AreEqual(GetCurrentThreadId(), args.ThreadId, "The window belongs to this thread.");
            Assert.AreEqual(Environment.CurrentManagedThreadId, managedThread, "Delivered on the thread that installed the hook.");

            hook.Dispose();
            int seen = events.Count;
            SetForegroundWindow(a.Handle);
            PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(seen, events.Count, "Nothing is delivered after Dispose.");

            // With the production flags the same change of the test's own windows raises nothing.
            var filtered = new List<ForegroundChangedEventArgs>();
            var production = new ForegroundChangeHook(new CapturingLog());
            production.ForegroundChanged += (_, e) => filtered.Add(e);
            Assert.IsTrue(production.Install().Ok);
            SetForegroundWindow(b.Handle);
            PumpUntil(() => filtered.Count > 0, TimeSpan.FromMilliseconds(800));
            SetForegroundWindow(a.Handle);
            PumpUntil(() => filtered.Count > 0, TimeSpan.FromMilliseconds(800));
            production.Dispose();

            Assert.IsEmpty(filtered, "WINEVENT_SKIPOWNPROCESS: this process's own windows deliver nothing.");
        });
    }

    [TestMethod]
    public void AHookThatIsNeverInstalledDisposesQuietly()
    {
        var log = new CapturingLog();

        new ForegroundChangeHook(log).Dispose();

        Assert.IsEmpty(log.Entries);
    }
}
