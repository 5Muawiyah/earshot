using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the gauge shows and says, from a snapshot and a time. The first matching state wins, only proved
// fields count, and a reading older than an hour is no reading.
[TestClass]
public sealed class GaugeContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly GaugeDisplaySettings Settings = GaugeDisplaySettings.Default;

    private static PartReading Bud(int percent, bool? charging = null, TimeSpan? age = null) =>
        new(percent, charging, null) { ReadAt = Now - (age ?? TimeSpan.FromMinutes(2)) };

    private static WidgetSnapshot Snapshot(AirPodsWhere where, PartReading? left = null, PartReading? right = null, bool claim = true) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started, claim) with
        {
            Where = where,
            Left = left ?? PartReading.Unknown,
            Right = right ?? PartReading.Unknown,
        };

    // ---- Precedence ----

    [TestMethod]
    public void NotOnThisPcAndInUseElsewhereIsThePhoneState()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.Elsewhere, Bud(70), Bud(60)), Now, Settings);

        Assert.AreEqual(GaugeMode.OnOtherDevice, c.Mode);
        Assert.AreEqual("On your iPhone", c.Tooltip);
        Assert.IsNull(c.Percent, "No ring and no number, whatever the buds last read.");
    }

    [TestMethod]
    public void ThePhoneStateUsesTheOtherDeviceNameSetting()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.Elsewhere), Now, Settings with { OtherDeviceLabel = "Pixel" });

        Assert.AreEqual("On your Pixel", c.Tooltip);
    }

    [TestMethod]
    [DataRow(AirPodsWhere.Unknown)]
    [DataRow(AirPodsWhere.NotInUse)]
    public void NotOnThisPcOtherwiseIsTheDimmedMark(AirPodsWhere where)
    {
        GaugeContent c = GaugeContent.From(Snapshot(where, Bud(70), Bud(60)), Now, Settings);

        Assert.AreEqual(GaugeMode.NotOnThisPc, c.Mode);
        Assert.AreEqual("Not on this PC", c.Tooltip);
        Assert.IsNull(c.Percent);
    }

    [TestMethod]
    public void OnThisPcWithAFreshProvedReadingIsTheRing()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70), Bud(60)), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(60, c.Percent, "The lower bud.");
        Assert.IsFalse(c.Low);
        Assert.IsFalse(c.Charging);
        Assert.AreEqual("AirPods\r\nL 70%   R 60%\r\nRead 2 min ago", c.Tooltip);
    }

    [TestMethod]
    public void OnThisPcWithNoReadingIsTheMarkAlone()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc), Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
        Assert.IsNull(c.Percent);
    }

    // ---- The no reading tooltips ----

    [TestMethod]
    public void WithNoSetUpTheTooltipSaysTheBatteryIsNotSetUp()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, claim: false), Now, Settings);

        Assert.AreEqual("Battery not set up", c.Tooltip);
        Assert.IsTrue(c.BatteryNotSetUp);
    }

    [TestMethod]
    public void AfterSetUpWithNoRecentReadingTheTooltipSaysSo()
    {
        GaugeContent none = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, claim: true), Now, Settings);
        GaugeContent old = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromHours(3)), Bud(60, age: TimeSpan.FromHours(3))), Now, Settings);

        Assert.AreEqual("No recent reading", none.Tooltip);
        Assert.AreEqual("No recent reading", old.Tooltip);
        Assert.IsFalse(old.BatteryNotSetUp);
        Assert.AreEqual(GaugeMode.MarkOnly, old.Mode);
    }

    // The one rule for how old a reading may be: exactly an hour is still recent, a minute more is not.
    [TestMethod]
    public void AReadingOfExactlyOneHourIsRecentAndOneMinuteOlderIsNot()
    {
        GaugeContent atAnHour = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromHours(1)), Bud(60, age: TimeSpan.FromHours(1))), Now, Settings);
        GaugeContent past = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromMinutes(61)), Bud(60, age: TimeSpan.FromMinutes(61))), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, atAnHour.Mode, "An hour old is still recent.");
        Assert.AreEqual(GaugeMode.MarkOnly, past.Mode, "61 minutes old counts as no recent reading.");
    }

    // ---- Freshness ----

    [TestMethod]
    public void AReadingIsStillRecentAtOneHourAndGoneAfterIt()
    {
        GaugeContent at59 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(59)), Bud(60, age: TimeSpan.FromMinutes(59))), Now, Settings);
        GaugeContent at60 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(60)), Bud(60, age: TimeSpan.FromMinutes(60))), Now, Settings);
        GaugeContent at61 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(61)), Bud(60, age: TimeSpan.FromMinutes(61))), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, at59.Mode);
        Assert.AreEqual("AirPods\r\nL 60%   R 60%\r\nRead 59 min ago", at59.Tooltip);
        Assert.AreEqual(GaugeMode.Reading, at60.Mode);
        Assert.AreEqual(GaugeMode.MarkOnly, at61.Mode);
    }

    // The number is the lower bud, so a stale bud could be the lower one: one stale bud is no reading, not a
    // reading of the other bud.
    [TestMethod]
    public void OneStaleBudMeansNoRecentReadingNotTheOtherBudsFigure()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromMinutes(2)), Bud(30, age: TimeSpan.FromHours(2))), Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
        Assert.IsNull(c.Percent);
    }

    [TestMethod]
    public void APercentWithNoReadTimeIsNotShown()
    {
        var undated = new PartReading(70, null, null);

        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, undated, undated), Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
    }

    [TestMethod]
    public void TheReadLineIsTheOldestBudsAndNeverYoungerThanTheReading()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromSeconds(20)), Bud(60, age: TimeSpan.FromSeconds(200))), Now, Settings);

        StringAssert.EndsWith(c.Tooltip, "Read 3 min ago");
        Assert.AreEqual("Read just now", WidgetCopy.GaugeReadLine(TimeSpan.FromSeconds(59)));
        Assert.AreEqual("Read 1 min ago", WidgetCopy.GaugeReadLine(TimeSpan.FromSeconds(119)));
    }

    // ---- Only proved fields ----

    [TestMethod]
    public void OneProvedBudIsShownAloneAndAnUnprovedBudIsLeftOutNotDashed()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70), PartReading.Unknown), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(70, c.Percent);
        Assert.AreEqual("AirPods\r\nL 70%\r\nRead 2 min ago", c.Tooltip);
    }

    [TestMethod]
    public void AnUnprovedChargingFlagIsNotABolt()
    {
        GaugeContent unknown = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, charging: null), Bud(60, charging: null)), Now, Settings);
        GaugeContent notCharging = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, charging: false), Bud(60, charging: false)), Now, Settings);
        GaugeContent charging = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, charging: true), Bud(60, charging: false)), Now, Settings);

        Assert.IsFalse(unknown.Charging);
        Assert.IsFalse(notCharging.Charging);
        Assert.IsTrue(charging.Charging);
        StringAssert.StartsWith(charging.Tooltip, "Charging\r\n");
    }

    // ---- Low battery ----

    [TestMethod]
    public void TheLowBatteryLevelIsInclusiveAndFollowsTheSetting()
    {
        GaugeContent at20 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(20), Bud(50)), Now, Settings);
        GaugeContent at30 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(30), Bud(50)), Now, Settings);
        GaugeContent at30WithHigherLevel = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(30), Bud(50)), Now, Settings with { LowBatteryThresholdPercent = 30 });

        Assert.IsTrue(at20.Low);
        Assert.IsFalse(at30.Low);
        Assert.IsTrue(at30WithHigherLevel.Low);
        StringAssert.StartsWith(at20.Tooltip, "Low battery\r\n");
    }

    [TestMethod]
    public void LowBatteryOutranksChargingInTheFirstLine()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(10, charging: true), Bud(20)), Now, Settings);

        StringAssert.StartsWith(c.Tooltip, "Low battery\r\nL 10%   R 20%");
        Assert.IsTrue(c.Charging, "The bolt still shows while it is charging.");
    }

    [TestMethod]
    public void ZeroAndOneHundredPercentAreShownAsRead()
    {
        GaugeContent empty = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(0), Bud(10)), Now, Settings);
        GaugeContent full = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(100), Bud(100)), Now, Settings);

        Assert.AreEqual(0, empty.Percent);
        Assert.IsTrue(empty.Low);
        Assert.AreEqual(100, full.Percent);
    }
}
