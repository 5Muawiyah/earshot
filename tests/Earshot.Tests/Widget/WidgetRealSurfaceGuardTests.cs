using System.Globalization;
using System.Runtime.CompilerServices;
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
// no TrayContext test constructs any of the five real surfaces below.
//
// The first version of this guard only checked one TrayHarness run built with the widget left off.
// It missed WidgetShellSignalTests, AppBarWiringTests, WidgetRuntimeToggleTests and WidgetMenuTests, which
// each turned the widget on and drove it far enough to construct a real UiaTaskbarReader (and, before the
// advertisement source was made injectable, a real WinRtAdvertisementSource) against the owner's own
// desktop and radio. Each real class already carried a test-only construction counter for exactly this kind
// of proof: it is the only way to tell "a fake stood in" from "the real thing ran and just did not happen
// to succeed" (a real UiaTaskbarReader that fails to find Shell_TrayWnd still ran for real). A later version
// widened the check to the whole assembly, for the whole run, rather than one harness build, but summed the
// five real counters into one running total checked against one summed allowance: a shortfall against one
// surface could silently cancel an unnoticed excess against another and the combined assertion would still
// read clean, and nothing distinguished which named caller's own allowance a mismatch belonged to.
//
// This version keeps one running total, per surface, and asserts each of the five independently rather than
// summed: RealWidgetSurface names them, AllowRealConstruction takes which one a caller is vouching for (the
// calling test method's own name is captured automatically, CallerMemberName, so every call site only has to
// say which surface, never repeat its own name), and the per-(surface, caller) breakdown that is built from
// that is read out in full on a mismatch rather than only the five running totals. AppBarRegistrationTests,
// GaugeWindowTests, UiaTaskbarReaderTests, WinRtAdvertisementSourceBindingTests,
// WidgetStatusServiceRealWatcherBindingTests and one WidgetRuntimeToggleTests case (see its own header) each
// call it for the one surface they deliberately, and safely, construct for real; one further method
// (NotifyIconVisibleCountedAdapterCountsAConstructionTimeShowIcon, below) is the sole caller ever allowed to
// let RealWidgetSurface.NotifyIconVisible move off 0.
[TestClass]
public sealed class WidgetRealSurfaceGuardTests
{
    internal enum RealWidgetSurface
    {
        AdvertisementSource,
        TaskbarReader,
        GaugeWindow,
        AppBar,
        NotifyIconVisible,
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<RealWidgetSurface, int> AllowedByType = new();
    private static readonly Dictionary<(string Caller, RealWidgetSurface Surface), int> AllowedByCaller = new();

    // Called by a named, read-only real execution right at the point it deliberately constructs one of the
    // five real surface types, so NoTestOutsideTheNamedAllowListEverTouchesARealWidgetSurface can tell it
    // apart from an unnoticed real construction anywhere else in the assembly. The calling test method's own
    // name is captured automatically (CallerMemberName), so the per-caller breakdown below needs nothing
    // extra from any call site.
    internal static void AllowRealConstruction(RealWidgetSurface surface, [CallerMemberName] string caller = "")
    {
        lock (Gate)
        {
            AllowedByType[surface] = AllowedByType.GetValueOrDefault(surface) + 1;
            (string caller, RealWidgetSurface surface) key = (caller, surface);
            AllowedByCaller[key] = AllowedByCaller.GetValueOrDefault(key) + 1;
        }
    }

    [AssemblyInitialize]
    public static void ResetRealSurfaceCountersBeforeAnyTestRuns(TestContext context)
    {
        WinRtAdvertisementSource.ConstructionCount = 0;
        UiaTaskbarReader.ConstructionCount = 0;
        GaugeWindow.ConstructionCount = 0;
        AppBarRegistration.RealRegistrationCount = 0;
        NotifyIconVisibility.RealVisibleTrueCount = 0;
        lock (Gate)
        {
            AllowedByType.Clear();
            AllowedByCaller.Clear();
        }

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

    // TrayContext used to write _notifyIcon.Visible = options.ShowIcon directly against the raw NotifyIcon
    // field, bypassing NotifyIconVisibility (the counted adapter every other Visible write, and the one
    // GaugeController is handed as ITrayIconVisibility, already goes through). Every assertion above, and
    // every other test in this assembly, sets ShowIcon false (TrayHarness's own default), so that direct
    // write always happened to pass false too - a real regression that flipped it, or a mutant that dropped
    // the routing, would make the real tray icon visible on the owner's own desktop with nothing here ever
    // noticing, since RealVisibleTrueCount would never move. Routed through the same adapter now
    // (TrayContext.cs's own _notifyIconVisibility field), so this is provable: the one place a construction
    // is deliberately asked to show the icon (showIcon: true) is this test, named on the allow-list for
    // exactly the surface it touches, and nothing else in the suite is ever allowed to.
    [TestMethod]
    public void NotifyIconVisibleCountedAdapterCountsAConstructionTimeShowIcon()
    {
        int before = NotifyIconVisibility.RealVisibleTrueCount;

        Phase5.CardDesktop.Run(() =>
        {
            AllowRealConstruction(RealWidgetSurface.NotifyIconVisible);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), showIcon: true);
            tray.PumpUntilIdle();
        });

        Assert.AreEqual(before + 1, NotifyIconVisibility.RealVisibleTrueCount,
            "A construction-time ShowIcon=true must reach the counted adapter, the same as every other real " +
            "Visible=true write does, not bypass it through the raw NotifyIcon field.");
    }

    // The assembly-wide proof: after every test in this assembly has run (DoNotParallelize keeps the whole
    // run single-threaded, so these static counters are never read mid-write), each of the five real surface
    // types' own running total must equal exactly what its own named allow-list entries recorded, checked
    // independently rather than summed together - a shortfall on one surface can no longer cancel an excess
    // on another and still read as a clean total. A mismatch means some other test, anywhere in the
    // assembly, constructed that surface for real without being on its own allow-list.
    [AssemblyCleanup]
    public static void NoTestOutsideTheNamedAllowListEverTouchesARealWidgetSurface()
    {
        var actualBySurface = new Dictionary<RealWidgetSurface, int>
        {
            [RealWidgetSurface.AdvertisementSource] = WinRtAdvertisementSource.ConstructionCount,
            [RealWidgetSurface.TaskbarReader] = UiaTaskbarReader.ConstructionCount,
            [RealWidgetSurface.GaugeWindow] = GaugeWindow.ConstructionCount,
            [RealWidgetSurface.AppBar] = AppBarRegistration.RealRegistrationCount,
            [RealWidgetSurface.NotifyIconVisible] = NotifyIconVisibility.RealVisibleTrueCount,
        };

        Dictionary<RealWidgetSurface, int> allowedBySurface;
        lock (Gate)
        {
            allowedBySurface = new Dictionary<RealWidgetSurface, int>(AllowedByType);
        }

        IReadOnlyList<string> mismatches = FindMismatches(actualBySurface, allowedBySurface, DescribeByCaller);
        Assert.AreEqual(0, mismatches.Count,
            "A test outside a surface's own named allow-list constructed it for real:" +
            Environment.NewLine + string.Join(Environment.NewLine, mismatches));

        IReadOnlyList<TopLevelWindowViolation> windowViolations = TopLevelWindowVisibilityGuard.Violations;
        TopLevelWindowVisibilityGuard.Stop();
        Assert.AreEqual(0, windowViolations.Count,
            "A top-level window belonging to this process was shown, or took the foreground, outside a private test desktop:" +
            Environment.NewLine + string.Join(Environment.NewLine, windowViolations));
    }

    // The comparison itself, pulled out so PerSurfaceComparisonCatchesEveryMismatchASummedTotalWouldHaveMasked
    // below can prove it directly against synthetic data, not only by trusting a whole assembly run to happen
    // to exercise the shape that would have defeated the old summed check. Every surface in actualBySurface is
    // checked, independently, against its own allowance (0 when a surface has no entry in allowedBySurface at
    // all): unlike a single running total, an excess on one surface can never cancel a shortfall on another
    // here, since neither ever gets added to anything outside its own key.
    internal static IReadOnlyList<string> FindMismatches(
        IReadOnlyDictionary<RealWidgetSurface, int> actualBySurface,
        IReadOnlyDictionary<RealWidgetSurface, int> allowedBySurface,
        Func<RealWidgetSurface, string>? describeByCaller = null)
    {
        var mismatches = new List<string>();
        foreach ((RealWidgetSurface surface, int actual) in actualBySurface)
        {
            int allowed = allowedBySurface.GetValueOrDefault(surface);
            if (actual != allowed)
            {
                string byCaller = describeByCaller?.Invoke(surface) ?? "";
                mismatches.Add(surface + ": actual=" + actual.ToString(CultureInfo.InvariantCulture) +
                    ", allowed=" + allowed.ToString(CultureInfo.InvariantCulture) +
                    (byCaller.Length == 0 ? "" : " (by caller: " + byCaller + ")") + ".");
            }
        }

        return mismatches;
    }

    private static string DescribeByCaller(RealWidgetSurface surface)
    {
        lock (Gate)
        {
            string byCaller = string.Join(", ", AllowedByCaller
                .Where(kv => kv.Key.Surface == surface)
                .Select(kv => kv.Key.Caller + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)));
            return byCaller.Length == 0 ? "none" : byCaller;
        }
    }

    // The exact defect class this restructuring closes, proved directly rather than only by trusting a whole
    // assembly run to happen to exercise it: three surfaces over their own allowance and two under, chosen so
    // the totals sum to the same number either way (10 actual, 10 allowed) - the single running total the old
    // version of this guard compared would have read this as perfectly clean. FindMismatches must report every
    // one of the five surfaces independently instead.
    [TestMethod]
    public void PerSurfaceComparisonCatchesEveryMismatchASummedTotalWouldHaveMasked()
    {
        var actual = new Dictionary<RealWidgetSurface, int>
        {
            [RealWidgetSurface.AdvertisementSource] = 3, // 2 over
            [RealWidgetSurface.TaskbarReader] = 0, // 1 under
            [RealWidgetSurface.GaugeWindow] = 4, // 3 over
            [RealWidgetSurface.AppBar] = 1, // 2 under
            [RealWidgetSurface.NotifyIconVisible] = 0, // 2 under
        };
        var allowed = new Dictionary<RealWidgetSurface, int>
        {
            [RealWidgetSurface.AdvertisementSource] = 1,
            [RealWidgetSurface.TaskbarReader] = 1,
            [RealWidgetSurface.GaugeWindow] = 1,
            [RealWidgetSurface.AppBar] = 3,
            [RealWidgetSurface.NotifyIconVisible] = 2,
        };
        Assert.AreEqual(actual.Values.Sum(), allowed.Values.Sum(),
            "Sanity: the two totals must actually be equal for this to prove anything about summing.");

        IReadOnlyList<string> mismatches = FindMismatches(actual, allowed);

        Assert.AreEqual(5, mismatches.Count,
            "Every one of the five surfaces is mismatched and must be reported, even though the two totals " +
            "are equal: " + string.Join(" | ", mismatches));
    }

    // The clean case, proved the same direct way: every surface's actual exactly matches its own allowance,
    // including a surface with no allowance at all (0 == 0, not a KeyNotFoundException from a missing entry).
    [TestMethod]
    public void PerSurfaceComparisonReadsCleanWhenEverySurfaceMatchesItsOwnAllowance()
    {
        var actual = new Dictionary<RealWidgetSurface, int>
        {
            [RealWidgetSurface.AdvertisementSource] = 2,
            [RealWidgetSurface.TaskbarReader] = 0,
            [RealWidgetSurface.GaugeWindow] = 5,
            [RealWidgetSurface.AppBar] = 3,
            [RealWidgetSurface.NotifyIconVisible] = 0,
        };
        var allowed = new Dictionary<RealWidgetSurface, int>
        {
            [RealWidgetSurface.AdvertisementSource] = 2,
            [RealWidgetSurface.GaugeWindow] = 5,
            [RealWidgetSurface.AppBar] = 3,
        };

        IReadOnlyList<string> mismatches = FindMismatches(actual, allowed);

        Assert.AreEqual(0, mismatches.Count, "Nothing is mismatched: " + string.Join(" | ", mismatches));
    }
}
