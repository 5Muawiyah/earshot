using System.Threading;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// A security review of HEAD found that every TrayContext test construction went through
// TrayContext.WireWidget -> CompositionRoot.BuildWidget with WidgetSettings.Enabled left at its production
// default (true), so each of the roughly 104 TrayHarness-built TrayContexts per check.ps1 run stood up a
// real BluetoothLEAdvertisementWatcher (WinRtAdvertisementSource), polled the real taskbar with UI
// Automation (UiaTaskbarReader) on a background thread, and could go on to show a real topmost window
// (GaugeWindow) on whatever screen ran the tests, register a real appbar (AppBarRegistration, ABM_NEW
// against the real Explorer) and make the real tray icon (NotifyIcon.Visible) visible, overriding
// ShowIcon=false. TrayHarness now defaults the widget to off (the same off state CompositionRoot.BuildWidget
// already documents as building nothing at all) and, for the few tests that turn it on to prove wiring,
// always supplies a fake advertisement source and a fake taskbar reader (TrayContextTests.TrayHarness), so
// no TrayContext test constructs any of the four real classes below.
//
// The first version of this guard only checked one TrayHarness run built with the widget left off.
// It missed WidgetShellSignalTests, AppBarWiringTests, WidgetRuntimeToggleTests and WidgetMenuTests, which
// each turned the widget on and drove it far enough to construct a real UiaTaskbarReader (and, before the
// advertisement source was made injectable, a real WinRtAdvertisementSource) against the owner's own
// desktop and radio. The four real classes already carried a test-only construction counter for exactly
// this kind of proof: it is the only way to tell "a fake stood in" from "the real thing ran and just did
// not happen to succeed" (a real UiaTaskbarReader that fails to find Shell_TrayWnd still ran for real). This
// version widens the check to the whole assembly, for the whole run, rather than one harness build: an
// [AssemblyInitialize] zeroes every counter before the first test runs, and an [AssemblyCleanup] sums them
// after the last one, checked against a running total of the real constructions a short, explicit allow-list
// of named read-only real executions recorded for themselves as they ran (AllowRealConstruction, called from
// AppBarRegistrationTests, GaugeWindowTests, UiaTaskbarReaderTests, WinRtAdvertisementSourceBindingTests,
// WidgetStatusServiceRealWatcherBindingTests and one WidgetRuntimeToggleTests case that feeds the real TrayContext pipeline a genuine free-space layout to
// prove a since-fixed stale-bounds defect against the real gauge it shows; see each file's own header for
// why it is kept real and never shows anything on the default desktop). A mismatch means something outside
// that named list constructed one of the four for real.
[TestClass]
public sealed class WidgetRealSurfaceGuardTests
{
    private static int _allowedRealConstructions;

    // Called by a named, read-only real execution right at the point it deliberately constructs one of the
    // four real surface types, so NoTestOutsideTheNamedAllowListTouchesARealWidgetSurface can tell it apart
    // from an unnoticed real construction anywhere else in the assembly. Never called for a real NotifyIcon:
    // no execution is ever allowed to make that one visible for real (see the assertion below).
    internal static void AllowRealConstruction() => Interlocked.Increment(ref _allowedRealConstructions);

    [AssemblyInitialize]
    public static void ResetRealSurfaceCountersBeforeAnyTestRuns(TestContext context)
    {
        WinRtAdvertisementSource.ConstructionCount = 0;
        UiaTaskbarReader.ConstructionCount = 0;
        GaugeWindow.ConstructionCount = 0;
        AppBarRegistration.RealRegistrationCount = 0;
        NotifyIconVisibility.RealVisibleTrueCount = 0;
        _allowedRealConstructions = 0;

        // TopLevelWindowVisibilityGuard: armed before the first test runs, for the same reason the counters
        // above are zeroed here rather than per-test - one process-wide proof over the whole run, not one
        // harness build. See its own header for what it watches and why a per-class counter is not enough.
        TopLevelWindowVisibilityGuard.Start();
    }

    [TestMethod]
    public void TheStandardHarnessNeverConstructsARealWidgetSurface()
    {
        int beforeAdvertisement = WinRtAdvertisementSource.ConstructionCount;
        int beforeReader = UiaTaskbarReader.ConstructionCount;
        int beforeGauge = GaugeWindow.ConstructionCount;

        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
        });

        Assert.AreEqual(beforeAdvertisement, WinRtAdvertisementSource.ConstructionCount,
            "TrayHarness built a real BLE advertisement watcher; every TrayContext test would touch the real radio.");
        Assert.AreEqual(beforeReader, UiaTaskbarReader.ConstructionCount,
            "TrayHarness built a real UI Automation taskbar reader; every TrayContext test would poll the real taskbar.");
        Assert.AreEqual(beforeGauge, GaugeWindow.ConstructionCount,
            "TrayHarness built a real gauge window; a TrayContext test could show a topmost window on screen.");
    }

    // The same proof as above, but with the widget turned on: this is exactly the shape WidgetShellSignalTests,
    // AppBarWiringTests, WidgetRuntimeToggleTests and WidgetMenuTests all use (settings: s => s.Widget = s.Widget
    // with { Enabled = true }), so the harness's fakes are proved against a widget that is actually wired up,
    // not only against the off state every other TrayContext test leaves it in. Run on a private desktop
    // (Earshot.Tests.Phase5.CardDesktop.Run), the same as those four files: turning the widget on also
    // registers a real AppBarRegistration (ABM_NEW), and the interactive desktop's StaThread.Run is exactly
    // where that reaches the owner's real Explorer instead of being refused (this test itself failed that
    // way, against the real Explorer, until it was moved here - see the commit history for the red run).
    [TestMethod]
    public void AWidgetEnabledHarnessStillNeverConstructsARealWidgetSurface()
    {
        int beforeAdvertisement = WinRtAdvertisementSource.ConstructionCount;
        int beforeReader = UiaTaskbarReader.ConstructionCount;
        int beforeGauge = GaugeWindow.ConstructionCount;
        int beforeAppBar = AppBarRegistration.RealRegistrationCount;
        int beforeVisible = NotifyIconVisibility.RealVisibleTrueCount;

        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected),
                settings: s => s.Widget = s.Widget with { Enabled = true });
            tray.PumpUntilIdle();
        });

        Assert.AreEqual(beforeAdvertisement, WinRtAdvertisementSource.ConstructionCount,
            "A widget-enabled TrayHarness built a real BLE advertisement watcher.");
        Assert.AreEqual(beforeReader, UiaTaskbarReader.ConstructionCount,
            "A widget-enabled TrayHarness built a real UI Automation taskbar reader.");
        Assert.AreEqual(beforeGauge, GaugeWindow.ConstructionCount,
            "A widget-enabled TrayHarness built a real gauge window.");
        Assert.AreEqual(beforeAppBar, AppBarRegistration.RealRegistrationCount,
            "A widget-enabled TrayHarness registered a real appbar against the real Explorer.");
        Assert.AreEqual(beforeVisible, NotifyIconVisibility.RealVisibleTrueCount,
            "A widget-enabled TrayHarness made the real tray icon visible, overriding ShowIcon=false.");
    }

    // The assembly-wide proof: after every test in this assembly has run (DoNotParallelize keeps the whole
    // run single-threaded, so these static counters are never read mid-write), the four real surface types'
    // total constructions must equal exactly what the named allow-list recorded for itself. A mismatch means
    // some other test, anywhere in the assembly, constructed one of them without being on that list.
    [AssemblyCleanup]
    public static void NoTestOutsideTheNamedAllowListEverTouchesARealWidgetSurface()
    {
        int total = WinRtAdvertisementSource.ConstructionCount + UiaTaskbarReader.ConstructionCount +
            GaugeWindow.ConstructionCount + AppBarRegistration.RealRegistrationCount;
        Assert.AreEqual(_allowedRealConstructions, total,
            "A test outside the named allow-list (AppBarRegistrationTests, GaugeWindowTests, UiaTaskbarReaderTests, " +
            "WinRtAdvertisementSourceBindingTests, WidgetStatusServiceRealWatcherBindingTests, WidgetRuntimeToggleTests) constructed a real widget surface: advertisement-source=" +
            WinRtAdvertisementSource.ConstructionCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", taskbar-reader=" + UiaTaskbarReader.ConstructionCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", gauge-window=" + GaugeWindow.ConstructionCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", app-bar=" + AppBarRegistration.RealRegistrationCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ", allowed=" + _allowedRealConstructions.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        Assert.AreEqual(0, NotifyIconVisibility.RealVisibleTrueCount,
            "A test set the real tray icon (NotifyIcon.Visible) to true: no execution is ever allowed to do that for real.");

        IReadOnlyList<TopLevelWindowViolation> windowViolations = TopLevelWindowVisibilityGuard.Violations;
        TopLevelWindowVisibilityGuard.Stop();
        Assert.AreEqual(0, windowViolations.Count,
            "A top-level window belonging to this process was shown, or took the foreground, outside a private test desktop:" +
            Environment.NewLine + string.Join(Environment.NewLine, windowViolations));
    }
}
