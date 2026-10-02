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
        Assert.AreEqual("AirPods, on iPhone\r\nCase 80%\r\nLast read 5 min ago", c.Tooltip);
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
        Assert.AreEqual("AirPods 60%\r\nL 70%   R 60%\r\nLast read 3 h ago", c.Tooltip);
    }

    [TestMethod]
    public void OnThisPcAnEstimatedBudIsMarkedInTheNumberAndTheTooltip()
    {
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, Book(Saved(40, true), Saved(90, false), null, Rate(ChargePart.Bud, 30)));

        GaugeContent c = GaugeContent.From(snapshot, ReadAt + TimeSpan.FromHours(1), Settings);

        Assert.AreEqual(70, c.Percent);
        Assert.IsTrue(c.Estimated);
        Assert.IsTrue(c.Tertiary);
        Assert.AreEqual("AirPods ≈70%, charging\r\nL ≈70%   R 90%\r\nEstimated, read 1 h ago", c.Tooltip);
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
        Assert.AreEqual("AirPods 50%\r\nL 50%   R 50%\r\nRead just now", c.Tooltip);
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

    // When any bud is live the number is the lower of the live buds: a bud's old reading is not a figure about now, and a
    // live bud's must not be hidden under it.
    [TestMethod]
    public void OnThisPcALiveBudIsNeverOutvotedByAnOlderLowerReadingOfTheOther()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(1);
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, Book(null, Saved(15, false), null)) with
        {
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(80, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
        };

        GaugeContent c = GaugeContent.From(snapshot, now, new GaugeDisplaySettings(20, "iPhone"));

        Assert.AreEqual(80, c.Percent, "The live bud's figure, not the other bud's reading from an hour ago.");
        Assert.IsFalse(c.Tertiary);
        Assert.IsFalse(c.Low);
        StringAssert.StartsWith(c.Tooltip, "AirPods 80%\r\n");
        Assert.IsFalse(c.Tooltip.Contains("Low battery", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OnThisPcTheNumberIsTheLowerOfTheLiveBudsWhenBothAreLive()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(1);
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.ThisPc, LastReadingBook.Empty) with
        {
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(80, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
            Right = new PartReading(60, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
        };

        Assert.AreEqual(60, GaugeContent.From(snapshot, now, Settings).Percent);
    }

    // A low battery head says what the battery is now: a last reading or an estimate never gives it.
    [TestMethod]
    public void ALowBatteryHeadIsNeverGivenByAFigureThatIsNotLive()
    {
        WidgetSnapshot last = Snapshot(AirPodsWhere.ThisPc, Book(Saved(10, false), Saved(12, false), null));

        GaugeContent c = GaugeContent.From(last, ReadAt + TimeSpan.FromHours(2), new GaugeDisplaySettings(20, "iPhone"));

        Assert.AreEqual(10, c.Percent, "The number is still the last reading, in tertiary ink.");
        Assert.IsFalse(c.Low);
        Assert.IsFalse(c.Tooltip.StartsWith("Low battery", StringComparison.Ordinal), c.Tooltip);

        WidgetSnapshot estimated = Snapshot(AirPodsWhere.ThisPc, Book(Saved(5, true), null, null, Rate(ChargePart.Bud, 6)));
        GaugeContent e = GaugeContent.From(estimated, ReadAt + TimeSpan.FromHours(1), new GaugeDisplaySettings(20, "iPhone"));
        Assert.AreEqual(11, e.Percent);
        Assert.IsFalse(e.Low);
        Assert.IsFalse(e.Tooltip.StartsWith("Low battery", StringComparison.Ordinal), e.Tooltip);

        DateTimeOffset now = ReadAt + TimeSpan.FromMinutes(1);
        WidgetSnapshot live = Snapshot(AirPodsWhere.ThisPc, LastReadingBook.Empty) with
        {
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(10, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
        };
        GaugeContent l = GaugeContent.From(live, now, new GaugeDisplaySettings(20, "iPhone"));
        Assert.IsTrue(l.Low, "A live reading at or under the level is low.");
        StringAssert.StartsWith(l.Tooltip, "Low battery");
    }

    // A case that is being heard now is live: its number is not drawn in the tertiary ink of a last reading.
    [TestMethod]
    public void AwayALiveCaseIsNotDrawnInTertiaryInk()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromMinutes(1);
        WidgetSnapshot snapshot = Snapshot(AirPodsWhere.NotInUse, LastReadingBook.Empty) with
        {
            Selection = BroadcastSelectionState.Linked,
            Case = new PartReading(70, false, null) { ReadAt = now - TimeSpan.FromSeconds(3) },
        };

        GaugeContent c = GaugeContent.From(snapshot, now, Settings);

        Assert.AreEqual(GaugeMode.CaseAway, c.Mode);
        Assert.AreEqual(70, c.Percent);
        Assert.IsFalse(c.Tertiary, "A live value is drawn as current.");
        Assert.IsFalse(c.Estimated);
    }
}
