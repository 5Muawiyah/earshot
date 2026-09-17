using System.Drawing;
using Earshot.Icons;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The small pure pieces around the shell: ink, icon size, end-session flags, the picker rules and the
// tray command line.
[TestClass]
public sealed class TrayShellTests
{
    [TestMethod]
    [DataRow(0, null, true)]
    [DataRow(1, null, false)]
    [DataRow(null, 0, true)]
    [DataRow(null, 1, false)]
    [DataRow(0, 1, true)]
    [DataRow(1, 0, false)]
    [DataRow(null, null, false)]
    [DataRow(2, null, false)]
    public void InkFollowsTheTaskbarTheme(int? systemUsesLight, int? appsUseLight, bool white)
    {
        Color ink = ThemeReader.InkFor(highContrast: false, systemUsesLight, appsUseLight, Color.Red);

        Assert.AreEqual((white ? Color.White : Color.Black).ToArgb(), ink.ToArgb());
    }

    [TestMethod]
    public void HighContrastUsesTheWindowTextColour()
    {
        Color windowText = Color.FromArgb(255, 255, 255, 0);

        Assert.AreEqual(windowText.ToArgb(), ThemeReader.InkFor(highContrast: true, 0, 0, windowText).ToArgb());
        Assert.AreEqual(windowText.ToArgb(), ThemeReader.InkFor(highContrast: true, 1, null, windowText).ToArgb());
    }

    [TestMethod]
    [DataRow(16, 96u, 16)]
    [DataRow(24, 144u, 24)]
    [DataRow(32, 192u, 32)]
    [DataRow(0, 96u, 16)]
    [DataRow(0, 120u, 20)]
    [DataRow(0, 144u, 24)]
    [DataRow(0, 192u, 32)]
    [DataRow(0, 0u, 16)]
    [DataRow(-1, 96u, 16)]
    [DataRow(4, 96u, 8)]
    [DataRow(1000, 96u, 256)]
    public void TheIconSizeComesFromTheSystemMetricWithAScaledFallback(int metric, uint dpi, int expected)
    {
        Assert.AreEqual(expected, TrayIconFactory.SizeFor(metric, dpi));
    }

    [TestMethod]
    [DataRow(0u, "shutdown or restart")]
    [DataRow(0x00000001u, "close app")]
    [DataRow(0x40000000u, "critical")]
    [DataRow(0x80000000u, "logoff")]
    [DataRow(0xC0000001u, "close app, critical, logoff")]
    [DataRow(0x00000010u, "other 0x00000010")]
    public void EndSessionFlagsAreSpelledOut(uint flags, string expected)
    {
        Assert.AreEqual(expected, ShellMessageWindow.DescribeEndSessionFlags(flags));
    }

    [TestMethod]
    public void AddressesAreShownAsPairs()
    {
        Assert.AreEqual("0A:1B:2C:3D:4E:8C", DevicePickerForm.FormatAddress(AirPodsAddress));
        Assert.AreEqual("not-an-address", DevicePickerForm.FormatAddress("not-an-address"));
    }

    [TestMethod]
    public void ThePickerAcceptsOnlyAUsableDeviceWhoseNameContainsTheMatch()
    {
        var airPods = new PairedDevice("id", AirPodsName, AirPodsAddress, AirPodsContainer, IsPresent: false);

        Assert.IsTrue(DevicePickerForm.CanAccept(airPods, "AirPods"));
        Assert.IsTrue(DevicePickerForm.CanAccept(airPods, "  airpods pro "));
        Assert.IsTrue(DevicePickerForm.CanAccept(airPods, AirPodsName));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods, "Buds"));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods, " "));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods, null));
        Assert.IsFalse(DevicePickerForm.CanAccept(null, "AirPods"));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods with { ContainerId = Guid.Empty }, "AirPods"));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods with { ContainerId = Earshot.Contracts.NodeMatch.PcContainer }, "AirPods"));
        Assert.IsFalse(DevicePickerForm.CanAccept(airPods with { Address = "0a1b2c3d4e8c" }, "AirPods"));
    }

    [TestMethod]
    public void TheTrayRunsWithNoArgumentsOrWithStartup()
    {
        Assert.IsTrue(Program.IsTrayCommandLine([], out string? none));
        Assert.IsNull(none);
        Assert.IsTrue(Program.IsTrayCommandLine(["--startup"], out string? startup));
        Assert.IsNull(startup);
    }

    [TestMethod]
    [DataRow("--startup", "x")]
    [DataRow("probes")]
    [DataRow("Gate")]
    [DataRow("--STARTUP")]
    public void AnyOtherCommandLineIsAUsageError(params string[] args)
    {
        Assert.IsFalse(Program.IsTrayCommandLine(args, out string? error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void TheInstanceNameIsFixedAndPerUserPerSession()
    {
        Assert.IsFalse(Program.TrayInstanceName.Contains('\\', StringComparison.Ordinal));
        Assert.IsTrue(Program.TrayInstanceOptions.CurrentUserOnly);
        Assert.IsTrue(Program.TrayInstanceOptions.CurrentSessionOnly);
        Assert.AreEqual(Program.TrayInstanceName + ".show", Program.ShowEventName(Program.TrayInstanceName));
    }

    // Each test uses its own instance name, so a real running tray is never signalled.
    private static string TestInstanceName() => "Earshot.test." + Guid.NewGuid().ToString("N");

    [TestMethod]
    public void ASecondCopySetsTheRunningTraysShowEvent()
    {
        string name = TestInstanceName();
        var log = new CapturingLog();
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName(name), Program.TrayInstanceOptions, out bool created);
        Assert.IsTrue(created);
        Assert.IsFalse(showEvent.WaitOne(0));

        Assert.IsTrue(Program.SignalRunningTray(log, name));

        Assert.IsTrue(showEvent.WaitOne(0), "The running tray's show event was not set.");
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Info, "asked to show its card"));
    }

    [TestMethod]
    public void WithoutAShowEventNothingIsSignalledAndItIsLogged()
    {
        var log = new CapturingLog();

        Assert.IsFalse(Program.SignalRunningTray(log, TestInstanceName()));
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "has no show event yet"));
    }
}
