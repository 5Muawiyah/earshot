using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What a screen reader is told of a battery value, on the card's columns and on the gauge. "≈" is said as "about", an estimate is
// said to be one with the age of the reading it grew from, and an old reading says "last read". Pure.
[TestClass]
public sealed class SpokenTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static ShownPart Live(int percent, bool? charging = null) => new(percent, charging, ReadingKind.Live, Now - TimeSpan.FromSeconds(3));

    private static ShownPart Last(int percent, TimeSpan age, bool? charging = null) => new(percent, charging, ReadingKind.Last, Now - age);

    private static ShownPart Estimate(int percent, TimeSpan age, bool? charging = true) => new(percent, charging, ReadingKind.Estimated, Now - age);

    // ---- Card columns

    [TestMethod]
    public void ALiveColumnSaysItsValueAndThatItIsCharging()
    {
        Assert.AreEqual("Left 70%, charging", WidgetCopy.SpokenReading("Left", Live(70, true), Now));
        Assert.AreEqual("Right 80%", WidgetCopy.SpokenReading("Right", Live(80, false), Now));
    }

    [TestMethod]
    public void AnEstimatedColumnSaysAboutAndEstimatedAndTheAgeOfTheReadingItGrewFrom()
    {
        Assert.AreEqual("Left about 90%, charging, estimated, read 2 h ago", WidgetCopy.SpokenReading("Left", Estimate(90, TwoHours), Now));
        Assert.AreEqual("Left about 90%, estimated, read 2 h ago", WidgetCopy.SpokenReading("Left", Estimate(90, TwoHours, charging: false), Now));
        Assert.DoesNotContain("≈", WidgetCopy.SpokenReading("Left", Estimate(90, TwoHours), Now), "The sign is not read well, so the words say it.");
    }

    [TestMethod]
    public void AnOldReadingSaysWhenItWasRead()
    {
        Assert.AreEqual("Case 40%, last read 2 h ago", WidgetCopy.SpokenReading("Case", Last(40, TwoHours), Now));
    }

    [TestMethod]
    public void APartWithNoValueSaysSoAndNeverInventsOne()
    {
        Assert.AreEqual("Case, no reading", WidgetCopy.SpokenReading("Case", ShownPart.None, Now));
    }

    // ---- The gauge

    private static PartReading Bud(int percent, bool? charging = null, TimeSpan? age = null) =>
        new(percent, charging, null) { ReadAt = Now - (age ?? TimeSpan.FromSeconds(3)) };

    private static WidgetSnapshot Snapshot(AirPodsWhere where, PartReading? left = null, PartReading? right = null, PartReading? box = null) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Selection = BroadcastSelectionState.Linked,
            Left = left ?? PartReading.Unknown,
            Right = right ?? PartReading.Unknown,
            Case = box ?? PartReading.Unknown,
        };

    [TestMethod]
    public void TheGaugeHasAFixedNameAndTheBatteryAsItsValue()
    {
        string value = GaugeSpeech.Value(Snapshot(AirPodsWhere.ThisPc, Bud(70, true), Bud(80, true)), Now, GaugeDisplaySettings.Default);

        Assert.AreEqual("Left 70%, Right 80%, charging", value);
    }

    [TestMethod]
    public void AnOldReadingOnTheGaugeSaysLastReadWithItsAge()
    {
        string value = GaugeSpeech.Value(Snapshot(AirPodsWhere.ThisPc, Bud(70, age: TwoHours), Bud(80, age: TwoHours)), Now, GaugeDisplaySettings.Default);

        Assert.AreEqual("Left 70%, last read 2 h ago, Right 80%, last read 2 h ago", value);
    }

    [TestMethod]
    public void LowBatteryIsSaidOnlyForALiveFigure()
    {
        GaugeDisplaySettings settings = GaugeDisplaySettings.Default;

        string live = GaugeSpeech.Value(Snapshot(AirPodsWhere.ThisPc, Bud(settings.LowBatteryThresholdPercent), Bud(80)), Now, settings);
        string old = GaugeSpeech.Value(Snapshot(AirPodsWhere.ThisPc, Bud(settings.LowBatteryThresholdPercent, age: TwoHours), Bud(80, age: TwoHours)), Now, settings);

        StringAssert.EndsWith(live, ", low battery");
        Assert.DoesNotContain("low battery", old);
    }

    [TestMethod]
    public void AwayTheGaugeSaysNotOnThisPcAndTheCaseAsItIs()
    {
        string value = GaugeSpeech.Value(
            Snapshot(AirPodsWhere.NotInUse, box: Bud(80, age: TwoHours)), Now, GaugeDisplaySettings.Default);

        Assert.AreEqual("Not on this PC. Case 80%, last read 2 h ago", value);
        Assert.AreEqual("Not on this PC", GaugeSpeech.Value(Snapshot(AirPodsWhere.NotInUse), Now, GaugeDisplaySettings.Default));
    }

    [TestMethod]
    public void OnAnotherDeviceTheGaugeNamesIt()
    {
        string value = GaugeSpeech.Value(Snapshot(AirPodsWhere.Elsewhere, Bud(70), Bud(60)), Now, GaugeDisplaySettings.Default);

        Assert.AreEqual("On iPhone", value);
        Assert.AreEqual("On another device", GaugeSpeech.Value(Snapshot(AirPodsWhere.Elsewhere), Now, GaugeDisplaySettings.Default with { OtherDeviceLabel = "" }));
    }

    [TestMethod]
    public void WithNoReadingTheGaugeSaysWhatItShows()
    {
        string value = GaugeSpeech.Value(Snapshot(AirPodsWhere.ThisPc), Now, GaugeDisplaySettings.Default);

        Assert.IsFalse(string.IsNullOrWhiteSpace(value));
        Assert.DoesNotContain("\r\n", value);
    }
}
