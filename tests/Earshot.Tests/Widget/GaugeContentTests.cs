using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the gauge shows and says, from a snapshot and a time. The first matching state wins, a bud with no value
// is left out, and a value that is not live is still shown, at any age, as a last reading in tertiary ink.
[TestClass]
public sealed class GaugeContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly GaugeDisplaySettings Settings = GaugeDisplaySettings.Default;

    private static PartReading Bud(int percent, bool? charging = null, TimeSpan? age = null) =>
        new(percent, charging, null) { ReadAt = Now - (age ?? TimeSpan.FromMinutes(2)) };

    private static WidgetSnapshot Snapshot(AirPodsWhere where, PartReading? left = null, PartReading? right = null, PartReading? headset = null) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Selection = BroadcastSelectionState.Linked,
            Left = left ?? PartReading.Unknown,
            Right = right ?? PartReading.Unknown,
            Headset = headset ?? PartReading.Unknown,
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
    public void OnThisPcWithAValueTheGaugeCanDrawIsTheRing()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70), Bud(60)), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(60, c.Percent, "The lower bud.");
        Assert.IsFalse(c.Low);
        Assert.IsFalse(c.Charging);
        Assert.AreEqual("AirPods\r\nL 70%   R 60%\r\nLast read 2 min ago", c.Tooltip, "Two minutes old is not live: a last reading.");
        Assert.IsTrue(c.Tertiary);
    }

    [TestMethod]
    public void OnThisPcWithNoReadingIsTheMarkAlone()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc), Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
        Assert.IsNull(c.Percent);
    }

    // ---- The no reading tooltip ----

    [TestMethod]
    public void WithNoRecentReadingTheTooltipSaysSo()
    {
        GaugeContent none = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc), Now, Settings);
        GaugeContent old = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromHours(3)), Bud(60, age: TimeSpan.FromHours(3))), Now, Settings);

        Assert.AreEqual("No recent reading", none.Tooltip);

        // Replaces "an old reading counts as none": it is the gauge's number, as a last reading.
        Assert.AreEqual("AirPods\r\nL 70%   R 60%\r\nLast read 3 h ago", old.Tooltip);
        Assert.AreEqual(GaugeMode.Reading, old.Mode);
        Assert.IsTrue(old.Tertiary);
    }

    // Connected with no pair linked: the mark alone, and the tooltip says what makes a figure show. The same broadcast
    // figures, unlinked, are never drawn.
    [TestMethod]
    public void ConnectedWithNoPairLinkedTheGaugeIsTheMarkAloneAndTheTooltipAsksToOpenTheCase()
    {
        WidgetSnapshot unlinked = Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromSeconds(1)), Bud(60, age: TimeSpan.FromSeconds(1))) with
        {
            Selection = BroadcastSelectionState.Listening,
        };

        GaugeContent c = GaugeContent.From(unlinked, Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
        Assert.IsNull(c.Percent);
        Assert.AreEqual("Open the case to show battery", c.Tooltip);
        Assert.AreEqual("No recent reading", GaugeContent.From(unlinked with { Selection = BroadcastSelectionState.NoPairedModel }, Now, Settings).Tooltip, "With no paired model opening the case would not help.");
    }

    // Windows' figure is for the connected headset and needs no link.
    [TestMethod]
    public void ConnectedWithNoPairLinkedWindowsFigureIsStillOnTheGauge()
    {
        WidgetSnapshot unlinked = Snapshot(AirPodsWhere.ThisPc, headset: new PartReading(55, null, null) { ReadAt = Now - TimeSpan.FromSeconds(5) }) with
        {
            Selection = BroadcastSelectionState.Listening,
        };

        GaugeContent c = GaugeContent.From(unlinked, Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(55, c.Percent);
    }

    // Replaces "exactly an hour is still recent, a minute more is not": there is no age at which a reading leaves the gauge.
    [TestMethod]
    public void AReadingOfAnHourOrMoreIsStillOnTheGaugeAsALastReading()
    {
        GaugeContent atAnHour = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromHours(1)), Bud(60, age: TimeSpan.FromHours(1))), Now, Settings);
        GaugeContent past = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromMinutes(61)), Bud(60, age: TimeSpan.FromMinutes(61))), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, atAnHour.Mode);
        Assert.AreEqual(GaugeMode.Reading, past.Mode, "61 minutes old is still shown.");
        Assert.AreEqual(60, past.Percent);
        Assert.IsTrue(past.Tertiary);
    }

    // ---- Freshness ----

    // Replaces "a reading is still recent at one hour and gone after it": the tooltip says how old it is instead.
    [TestMethod]
    public void AnOldReadingSaysLastReadWithItsAge()
    {
        GaugeContent at59 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(59)), Bud(60, age: TimeSpan.FromMinutes(59))), Now, Settings);
        GaugeContent at60 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(60)), Bud(60, age: TimeSpan.FromMinutes(60))), Now, Settings);
        GaugeContent at61 = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(60, age: TimeSpan.FromMinutes(61)), Bud(60, age: TimeSpan.FromMinutes(61))), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, at59.Mode);
        Assert.AreEqual("AirPods\r\nL 60%   R 60%\r\nLast read 59 min ago", at59.Tooltip);
        Assert.AreEqual("AirPods\r\nL 60%   R 60%\r\nLast read 1 h ago", at60.Tooltip);
        Assert.AreEqual(GaugeMode.Reading, at61.Mode);
        Assert.AreEqual("AirPods\r\nL 60%   R 60%\r\nLast read 1 h ago", at61.Tooltip);
    }

    // Replaces "a value older than an hour is dropped bud by bud": the old low bud is still the number, as a last
    // reading, and the tooltip gives the age of the oldest bud shown.
    [TestMethod]
    public void AnOldLowBudIsStillTheNumberAsALastReading()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TimeSpan.FromMinutes(2)), Bud(30, age: TimeSpan.FromHours(2))), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(30, c.Percent);
        Assert.IsTrue(c.Tertiary);
        Assert.AreEqual("AirPods\r\nL 70%   R 30%\r\nLast read 2 h ago", c.Tooltip);
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

        StringAssert.EndsWith(c.Tooltip, "Last read 3 min ago");
        Assert.AreEqual("Read 1 min ago", WidgetCopy.GaugeReadLine(TimeSpan.FromSeconds(119)));
    }

    // The card greys a figure after BatteryFreshness.FreshWindow, so the tooltip never calls an older one "just now".
    [TestMethod]
    public void ReadJustNowIsSaidOnlyWhileTheReadingIsCurrent()
    {
        Assert.AreEqual("Read just now", WidgetCopy.GaugeReadLine(BatteryFreshness.FreshWindow));
        Assert.AreEqual("Read 31 s ago", WidgetCopy.GaugeReadLine(BatteryFreshness.FreshWindow + TimeSpan.FromSeconds(1)));
        Assert.AreEqual("Read 59 s ago", WidgetCopy.GaugeReadLine(TimeSpan.FromSeconds(59)));
        Assert.AreEqual("Read 1 min ago", WidgetCopy.GaugeReadLine(TimeSpan.FromSeconds(60)));
    }

    // ---- Only what has a value ----

    [TestMethod]
    public void OneBudIsShownAloneAndABudWithNoValueIsLeftOutNotDashed()
    {
        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(70), PartReading.Unknown), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(70, c.Percent);
        Assert.AreEqual("AirPods\r\nL 70%\r\nLast read 2 min ago", c.Tooltip);
    }

    [TestMethod]
    public void AChargingFlagThatIsNotTrueIsNotABolt()
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

    // ---- Windows' own figure ----

    [TestMethod]
    public void WindowsFigureIsTheNumberWhenNoBudHasAFreshValueAndItIsCurrent()
    {
        PartReading windows = new(70, null, null) { ReadAt = Now - TimeSpan.FromSeconds(10) };

        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(30, age: TimeSpan.FromMinutes(5)), headset: windows), Now, Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(70, c.Percent);
        Assert.AreEqual("AirPods\r\nWindows reads 70%\r\nRead just now", c.Tooltip);
        Assert.IsFalse(c.Charging, "Windows' figure carries no charging flag.");
    }

    [TestMethod]
    public void AFreshBudBeatsWindowsFigure()
    {
        PartReading windows = new(70, null, null) { ReadAt = Now - TimeSpan.FromSeconds(10) };

        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, Bud(30, age: TimeSpan.FromSeconds(5)), headset: windows), Now, Settings);

        Assert.AreEqual(30, c.Percent);
        Assert.AreEqual("AirPods\r\nL 30%\r\nRead just now", c.Tooltip);
    }

    [TestMethod]
    public void WindowsFigureIsNeverTheNumberForAirPodsThatAreNotOnThisPc()
    {
        PartReading windows = new(70, null, null) { ReadAt = Now - TimeSpan.FromSeconds(10) };

        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.NotInUse, headset: windows), Now, Settings);

        Assert.AreEqual(GaugeMode.NotOnThisPc, c.Mode);
        Assert.IsNull(c.Percent);
    }

    [TestMethod]
    public void AnOldWindowsFigureIsNotShown()
    {
        PartReading windows = new(70, null, null) { ReadAt = Now - TimeSpan.FromSeconds(121) };

        GaugeContent c = GaugeContent.From(Snapshot(AirPodsWhere.ThisPc, headset: windows), Now, Settings);

        Assert.AreEqual(GaugeMode.MarkOnly, c.Mode);
    }
}
