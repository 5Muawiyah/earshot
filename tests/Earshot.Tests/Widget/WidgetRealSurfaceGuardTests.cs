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
// (GaugeWindow) on whatever screen ran the tests. TrayHarness now defaults the widget to off, the same off
// state CompositionRoot.BuildWidget already documents as building nothing at all.
//
// The three real classes carry a test-only construction counter for exactly this proof: it is the only way
// to tell "a fake stood in" from "the real thing ran and just did not happen to succeed" (a real
// UiaTaskbarReader that fails to find Shell_TrayWnd still ran for real).
[TestClass]
public sealed class WidgetRealSurfaceGuardTests
{
    [TestMethod]
    public void TheStandardHarnessNeverConstructsARealWidgetSurface()
    {
        WinRtAdvertisementSource.ConstructionCount = 0;
        UiaTaskbarReader.ConstructionCount = 0;
        GaugeWindow.ConstructionCount = 0;

        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));
            tray.PumpUntilIdle();
        });

        Assert.AreEqual(0, WinRtAdvertisementSource.ConstructionCount,
            "TrayHarness built a real BLE advertisement watcher; every TrayContext test would touch the real radio.");
        Assert.AreEqual(0, UiaTaskbarReader.ConstructionCount,
            "TrayHarness built a real UI Automation taskbar reader; every TrayContext test would poll the real taskbar.");
        Assert.AreEqual(0, GaugeWindow.ConstructionCount,
            "TrayHarness built a real gauge window; a TrayContext test could show a topmost window on screen.");
    }
}
