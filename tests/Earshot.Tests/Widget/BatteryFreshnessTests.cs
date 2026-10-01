using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class BatteryFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static PartReading Part(int? percent, TimeSpan age, bool? charging = null) =>
        percent is null
            ? PartReading.Unknown
            : new PartReading(percent, charging, InEar: null) { ReadAt = Now - age };

    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    [TestMethod]
    public void AValueReadWithinThirtySecondsIsFreshAndOlderIsNot()
    {
        Assert.IsTrue(BatteryFreshness.IsFresh(Part(70, Seconds(0)), Now));
        Assert.IsTrue(BatteryFreshness.IsFresh(Part(70, Seconds(30)), Now));
        Assert.IsFalse(BatteryFreshness.IsFresh(Part(70, Seconds(30.1)), Now));
        Assert.IsFalse(BatteryFreshness.IsFresh(PartReading.Unknown, Now), "No value is never fresh.");
    }

    [TestMethod]
    public void AValueIsRecentForAnHourAndNotAfter()
    {
        Assert.IsTrue(BatteryFreshness.IsRecent(Part(70, TimeSpan.FromHours(1)), Now));
        Assert.IsFalse(BatteryFreshness.IsRecent(Part(70, TimeSpan.FromHours(1) + Seconds(1)), Now));
    }

    [TestMethod]
    public void AValueWithNoReadTimeIsNeitherFreshNorRecent()
    {
        var undated = new PartReading(70, null, null);

        Assert.IsFalse(BatteryFreshness.IsFresh(undated, Now));
        Assert.IsFalse(BatteryFreshness.IsRecent(undated, Now));
    }

    [TestMethod]
    public void ThePartsCarryTheirOwnFreshness()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(70, Seconds(2)), Part(60, Seconds(90)), PartReading.Unknown, PartReading.Unknown, onThisPc: true, Now);

        Assert.IsTrue(shown.Left.Fresh);
        Assert.IsFalse(shown.Right.Fresh);
        Assert.AreEqual(60, shown.Right.Percent, "An old value is still shown, greyed.");
        Assert.IsFalse(shown.Case.HasValue);
        Assert.IsFalse(shown.Case.Fresh);
    }

    // The gauge number is the lower of the buds that have a value no older than an hour.
    [TestMethod]
    public void TheGaugeDropsABudPerBudAfterAnHour()
    {
        PartReading headset = PartReading.Unknown;

        ShownBattery both = BatteryFreshness.Shown(Part(40, TimeSpan.FromMinutes(10)), Part(80, Seconds(1)), PartReading.Unknown, headset, true, Now);
        Assert.AreEqual(40, both.Gauge?.Percent);

        ShownBattery oneStale = BatteryFreshness.Shown(Part(40, TimeSpan.FromMinutes(61)), Part(80, Seconds(1)), PartReading.Unknown, headset, true, Now);
        Assert.AreEqual(80, oneStale.Gauge?.Percent, "The old low bud no longer pulls the number down.");
        Assert.IsNull(oneStale.Gauge?.Left);
        Assert.AreEqual(80, oneStale.Gauge?.Right);

        ShownBattery bothStale = BatteryFreshness.Shown(Part(40, TimeSpan.FromMinutes(61)), Part(80, TimeSpan.FromMinutes(120)), PartReading.Unknown, headset, true, Now);
        Assert.IsNull(bothStale.Gauge, "No bud value, no number.");
    }

    [TestMethod]
    public void AnUnknownBudIsSkippedForTheGauge()
    {
        ShownBattery shown = BatteryFreshness.Shown(PartReading.Unknown, Part(55, Seconds(1)), Part(90, Seconds(1)), PartReading.Unknown, true, Now);

        Assert.AreEqual(55, shown.Gauge?.Percent);
    }

    [TestMethod]
    public void TheCaseNeverSetsTheGaugeNumber()
    {
        ShownBattery shown = BatteryFreshness.Shown(PartReading.Unknown, PartReading.Unknown, Part(20, Seconds(1)), PartReading.Unknown, true, Now);

        Assert.IsNull(shown.Gauge);
    }

    [TestMethod]
    public void TheGaugeIsChargingWhenABudItIsDrawnFromIs()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(40, Seconds(1), charging: true), Part(80, Seconds(1), charging: false), PartReading.Unknown, PartReading.Unknown, true, Now);
        Assert.IsTrue(shown.Gauge?.Charging);

        ShownBattery droppedBud = BatteryFreshness.Shown(
            Part(40, TimeSpan.FromHours(2), charging: true), Part(80, Seconds(1), charging: false), PartReading.Unknown, PartReading.Unknown, true, Now);
        Assert.IsFalse(droppedBud.Gauge?.Charging, "A bud the gauge dropped does not make it charging.");
    }

    [TestMethod]
    public void TheGaugeReadTimeIsTheOldestOfTheBudsItIsDrawnFrom()
    {
        ShownBattery shown = BatteryFreshness.Shown(Part(40, TimeSpan.FromMinutes(10)), Part(80, Seconds(1)), PartReading.Unknown, PartReading.Unknown, true, Now);

        Assert.AreEqual(Now - TimeSpan.FromMinutes(10), shown.Gauge?.ReadAt);
    }

    // A fresh broadcast value wins; Windows' figure is shown only when no bud has one and it is current.
    [TestMethod]
    public void AFreshBroadcastBudBeatsWindowsFigure()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(40, Seconds(1)), PartReading.Unknown, PartReading.Unknown, Part(70, Seconds(5)), true, Now);

        Assert.IsNull(shown.WindowsPercent);
        Assert.AreEqual(BatterySource.Broadcast, shown.Gauge?.Source);
        Assert.AreEqual(40, shown.Gauge?.Percent);
    }

    [TestMethod]
    public void WindowsFigureFillsInWhenNoBudIsFreshAndItIsCurrent()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(40, TimeSpan.FromMinutes(5)), Part(30, TimeSpan.FromMinutes(5)), PartReading.Unknown, Part(70, Seconds(5)), true, Now);

        Assert.AreEqual(70, shown.WindowsPercent);
        Assert.AreEqual(BatterySource.Windows, shown.Gauge?.Source);
        Assert.AreEqual(70, shown.Gauge?.Percent);
        Assert.AreEqual(30, shown.Right.Percent, "It is never written into Left or Right.");
        Assert.AreEqual(40, shown.Left.Percent);
    }

    [TestMethod]
    public void WindowsFigureIsNotCurrentAfterTwoMinutes()
    {
        ShownBattery current = BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, Part(70, Seconds(120)), true, Now);
        ShownBattery old = BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, Part(70, Seconds(121)), true, Now);

        Assert.AreEqual(70, current.WindowsPercent);
        Assert.IsNull(old.WindowsPercent);
        Assert.IsNull(old.Gauge);
    }

    [TestMethod]
    public void WindowsFigureIsNeverShownForAirPodsThatAreNotOnThisPc()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, Part(70, Seconds(5)), onThisPc: false, Now);

        Assert.IsNull(shown.WindowsPercent);
        Assert.IsNull(shown.Gauge);
    }

    [TestMethod]
    public void WithWindowsFigureAndAnOldBroadcastTheWindowsFigureIsTheGauge()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(20, TimeSpan.FromMinutes(30)), PartReading.Unknown, PartReading.Unknown, Part(70, Seconds(5)), true, Now);

        Assert.AreEqual(BatterySource.Windows, shown.Gauge?.Source);
        Assert.AreEqual(70, shown.Gauge?.Percent);
    }

    [TestMethod]
    public void AnOldBroadcastIsTheGaugeWhenWindowsHasNothingCurrent()
    {
        ShownBattery shown = BatteryFreshness.Shown(
            Part(20, TimeSpan.FromMinutes(30)), PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, true, Now);

        Assert.AreEqual(BatterySource.Broadcast, shown.Gauge?.Source);
        Assert.AreEqual(20, shown.Gauge?.Percent);
    }
}
