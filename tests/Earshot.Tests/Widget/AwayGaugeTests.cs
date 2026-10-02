using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the gauge and the card say for the owner's pair away from this PC, and for last readings and estimates on it.
[TestClass]
public sealed class AwayGaugeTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private const ushort Model = BroadcastFixtures.PairedModel;

    private static readonly GaugeDisplaySettings Settings = GaugeDisplaySettings.Default;

    private static WidgetSnapshot Snapshot(AirPodsWhere where, LastReadingBook book) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Selection = BroadcastSelectionState.Listening,
            LastReadings = book,
            PairedModel = Model,
        };

    private static LastReadingBook Book(SavedReading? left, SavedReading? right, SavedReading? box, params LearnedRate[] rates) =>
        new(left, right, box, rates);

    private static SavedReading Saved(int percent, bool charging) => new(percent, charging, ReadAt, Model);

    private static LearnedRate Rate(ChargePart part, double perHour) =>
        new(Model, part, perHour, ReadAt - TimeSpan.FromDays(1), TimeSpan.FromMinutes(30));

    [TestMethod]
    [DataRow(AirPodsWhere.Unknown)]
    [DataRow(AirPodsWhere.NotInUse)]
    public void AwayTheGaugeShowsTheCasesLastReadingWithTheCaseMarkInTertiaryInk(AirPodsWhere where)
    {
        WidgetSnapshot snapshot = Snapshot(where, Book(Saved(70, false), Saved(60, false), Saved(80, false)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(2), Settings);

        Assert.AreEqual(GaugeMode.CaseAway, c.Mode);
        Assert.AreEqual(80, c.Percent, "The case's value, not the buds'.");
        Assert.IsTrue(c.CaseMark);
        Assert.IsTrue(c.Tertiary);
        Assert.IsFalse(c.Estimated);
        Assert.IsFalse(c.Charging);
        Assert.AreEqual("Not on this PC\r\nCase 80%\r\nLast read 2 h ago", c.Tooltip);
    }

    [TestMethod]
    public void AwayAChargingCaseWithALearnedRateIsEstimatedWithItsBolt()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.Unknown, Book(null, null, Saved(50, true), Rate(ChargePart.Case, 30)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(1), Settings);

        Assert.AreEqual(GaugeMode.CaseAway, c.Mode);
        Assert.AreEqual(80, c.Percent);
        Assert.IsTrue(c.Estimated);
        Assert.IsTrue(c.Charging, "The case was charging when read: the bolt.");
        Assert.AreEqual("Not on this PC\r\nCase ≈80%\r\nEstimated, read 1 h ago", c.Tooltip);
    }

    [TestMethod]
    public void AwayWithNoCaseValueTheGaugeKeepsTodaysLook()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.Unknown, Book(Saved(70, false), Saved(60, false), null));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(2), Settings);

        Assert.AreEqual(GaugeMode.NotOnThisPc, c.Mode);
        Assert.IsNull(c.Percent);
        Assert.IsFalse(c.CaseMark);
        Assert.AreEqual("Not on this PC", c.Tooltip);
    }

    [TestMethod]
    public void OnTheOtherDeviceThePhoneStaysAndTheCaseIsInTheTooltip()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.Elsewhere, Book(Saved(70, false), Saved(60, false), Saved(80, false)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromMinutes(5), new GaugeDisplaySettings(20, "iPhone"));

        Assert.AreEqual(GaugeMode.OnOtherDevice, c.Mode);
        Assert.IsFalse(c.CaseMark);
        Assert.AreEqual("On your iPhone\r\nCase 80%\r\nLast read 5 min ago", c.Tooltip);
    }

    [TestMethod]
    public void OnThisPcALastReadingIsTheLowerBudInTertiaryInkAndTheTooltipSaysSo()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, Book(Saved(70, false), Saved(60, false), Saved(80, false)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(3), Settings);

        Assert.AreEqual(GaugeMode.Reading, c.Mode);
        Assert.AreEqual(60, c.Percent);
        Assert.IsTrue(c.Tertiary);
        Assert.IsFalse(c.CaseMark, "On this PC the earbud mark stays.");
        Assert.AreEqual("AirPods\r\nL 70%   R 60%\r\nLast read 3 h ago", c.Tooltip);
    }

    [TestMethod]
    public void OnThisPcAnEstimatedBudIsMarkedInTheNumberAndTheTooltip()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, Book(Saved(40, true), Saved(90, false), null, Rate(ChargePart.Bud, 30)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(1), Settings);

        Assert.AreEqual(70, c.Percent);
        Assert.IsTrue(c.Estimated);
        Assert.IsTrue(c.Tertiary);
        Assert.AreEqual("Charging\r\nL ≈70%   R 90%\r\nEstimated, read 1 h ago", c.Tooltip);
    }

    [TestMethod]
    public void OnThisPcALiveBudIsDrawnInFullInk()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(1);
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, Book(Saved(70, false), Saved(60, false), null)) with
        {
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(50, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
            Right = new PartReading(50, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
        };

        GaugeContent c = GaugeContent.From(snapshot, now, Settings);

        Assert.AreEqual(50, c.Percent, "The newer live readings replace the saved ones.");
        Assert.IsFalse(c.Tertiary);
        Assert.IsFalse(c.Estimated);
        Assert.AreEqual("AirPods\r\nL 50%   R 50%\r\nRead just now", c.Tooltip);
    }

    [TestMethod]
    public void TheCardSaysEachPartsKindAndAge()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(2);
        var live = new ShownPart(80, false, ReadingKind.Live, now);
        var last = new ShownPart(80, false, ReadingKind.Last, ReadAt);
        var estimate = new ShownPart(90, true, ReadingKind.Estimated, ReadAt) { ReadPercent = 60 };

        Assert.AreEqual("80%", WidgetCopy.PartLine(live, now));
        Assert.AreEqual("80% · 2 h", WidgetCopy.PartLine(last, now));
        Assert.AreEqual("≈90% · 2 h", WidgetCopy.PartLine(estimate, now));
        Assert.AreEqual("No reading", WidgetCopy.PartLine(ShownPart.None, now));
        Assert.IsNull(WidgetCopy.PartTip(live, now));
        Assert.AreEqual("Last read 2 h ago", WidgetCopy.PartTip(last, now));
        Assert.AreEqual("Estimated, read 2 h ago", WidgetCopy.PartTip(estimate, now));
        Assert.AreEqual("3 d", WidgetCopy.AgeAmount(TimeSpan.FromDays(3)));
    }
}
