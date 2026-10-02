using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Away from this PC, the estimate: a part charging at its last reading rises from it at its rate until 100 and stops, a
// part that was not charging never rises, a clock behind the read time gives no rise, nothing falls, the case is never
// estimated before a charge of it has been seen, and the buds are never estimated from the seed while it is off. All
// through BatteryFreshness.Shown, the one definition every surface reads, with the saved book handed in.
[TestClass]
public sealed class AwayEstimateTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private const ushort Model = BroadcastFixtures.PairedModel;

    private static LastReadingBook Book(SavedReading? left = null, SavedReading? right = null, SavedReading? box = null, params LearnedRate[] rates) =>
        new(left, right, box, rates);

    private static SavedReading Saved(int percent, bool charging, DateTimeOffset? at = null, ushort model = Model) =>
        new(percent, charging, at ?? ReadAt, model);

    private static LearnedRate Rate(ChargePart part, double perHour, ushort model = Model) =>
        new(model, part, perHour, ReadAt - TimeSpan.FromDays(1), TimeSpan.FromMinutes(30));

    private static ShownBattery Shown(LastReadingBook book, DateTimeOffset now, DateTimeOffset? estimateClock = null, bool seed = false, bool onThisPc = false) =>
        BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, onThisPc, linked: false, now,
            new SavedBattery(book, Model, estimateClock, seed));

    [TestMethod]
    public void AChargingPartRisesFromItsReadingAtItsRate()
    {
        LastReadingBook book = Book(box: Saved(40, charging: true), rates: Rate(ChargePart.Case, 30));

        ShownPart after = Shown(book, ReadAt + TimeSpan.FromMinutes(40)).Case;

        Assert.AreEqual(ReadingKind.Estimated, after.Kind);
        Assert.AreEqual(60, after.Percent, "40 + 30 an hour for 40 minutes.");
        Assert.AreEqual(40, after.ReadPercent, "It says what reading it grew from.");
        Assert.AreEqual(ReadAt, after.ReadAt, "...and when that was read.");
    }

    [TestMethod]
    public void TheEstimateStopsAt100()
    {
        LastReadingBook book = Book(box: Saved(90, charging: true), rates: Rate(ChargePart.Case, 30));

        Assert.AreEqual(100, Shown(book, ReadAt + TimeSpan.FromMinutes(20)).Case.Percent);
        Assert.AreEqual(100, Shown(book, ReadAt + TimeSpan.FromHours(30)).Case.Percent, "And stays there.");
        Assert.AreEqual(ReadingKind.Estimated, Shown(book, ReadAt + TimeSpan.FromHours(30)).Case.Kind);

        LastReadingBook full = Book(box: Saved(100, charging: true), rates: Rate(ChargePart.Case, 30));
        Assert.AreEqual(ReadingKind.Last, Shown(full, ReadAt + TimeSpan.FromHours(1)).Case.Kind, "A full reading has nothing to grow.");
        Assert.AreEqual(100, Shown(full, ReadAt + TimeSpan.FromHours(1)).Case.Percent);
    }

    [TestMethod]
    public void TheCaseWithNoChargeSeenGivesNoEstimate()
    {
        // A bud rate is known, the case is charging, but no charge of the case has ever been seen: no case rate.
        LastReadingBook book = Book(box: Saved(40, charging: true), rates: Rate(ChargePart.Bud, 60));

        ShownPart box = Shown(book, ReadAt + TimeSpan.FromHours(2), seed: true).Case;

        Assert.AreEqual(ReadingKind.Last, box.Kind, "Not even the bud seed is used for the case.");
        Assert.AreEqual(40, box.Percent);
    }

    [TestMethod]
    public void ANonChargingBudNeverRises()
    {
        LastReadingBook book = Book(left: Saved(30, charging: false), right: Saved(50, charging: true), rates: Rate(ChargePart.Bud, 60));

        ShownBattery shown = Shown(book, ReadAt + TimeSpan.FromHours(3));

        Assert.AreEqual(ReadingKind.Last, shown.Left.Kind);
        Assert.AreEqual(30, shown.Left.Percent, "A bud out of the case, or not charging, keeps its value.");
        Assert.AreEqual(ReadingKind.Estimated, shown.Right.Kind, "The bud in the case and charging rises.");
        Assert.AreEqual(100, shown.Right.Percent);
    }

    [TestMethod]
    public void ACaseThatWasNotItselfChargingNeverRises()
    {
        LastReadingBook book = Book(box: Saved(60, charging: false), rates: Rate(ChargePart.Case, 30));

        Assert.AreEqual(ReadingKind.Last, Shown(book, ReadAt + TimeSpan.FromHours(5)).Case.Kind);
        Assert.AreEqual(60, Shown(book, ReadAt + TimeSpan.FromHours(5)).Case.Percent);
    }

    [TestMethod]
    public void TheAppleSeedOffGivesNoBudEstimateBeforeARateIsLearned()
    {
        LastReadingBook book = Book(left: Saved(20, charging: true, model: ChargeRates.AppleBudSeedModel));

        ShownPart off = BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, false, false, ReadAt + TimeSpan.FromMinutes(10),
            new SavedBattery(book, ChargeRates.AppleBudSeedModel, null)).Left;

        Assert.AreEqual(ReadingKind.Last, off.Kind);
        Assert.AreEqual(20, off.Percent);
        Assert.IsNull(ChargeRates.For(book, ChargeRates.AppleBudSeedModel, ChargePart.Bud), "The seed ships off until the owner confirms Apple's wording.");
    }

    // What the switch would do once flipped: 150 points an hour (one hour of eight in five minutes), for that model's
    // buds only. A learned rate replaces it.
    [TestMethod]
    public void TheAppleSeedOnlyEverAppliesToItsModelsBudsAndALearnedRateReplacesIt()
    {
        LastReadingBook book = Book();
        Assert.AreEqual(150.0, ChargeRates.For(book, ChargeRates.AppleBudSeedModel, ChargePart.Bud, useAppleBudSeed: true));
        Assert.IsNull(ChargeRates.For(book, ChargeRates.AppleBudSeedModel, ChargePart.Case, useAppleBudSeed: true));
        Assert.IsNull(ChargeRates.For(book, Model, ChargePart.Bud, useAppleBudSeed: true), "No other model gets a seed.");

        LastReadingBook learned = Book(rates: Rate(ChargePart.Bud, 45, ChargeRates.AppleBudSeedModel));
        Assert.AreEqual(45.0, ChargeRates.For(learned, ChargeRates.AppleBudSeedModel, ChargePart.Bud, useAppleBudSeed: true));
    }

    // A clock behind the read time gives no rise, and an estimate already shown is never shown lower when the clock is
    // put back: the service hands in the latest time it has worked anything out at.
    [TestMethod]
    public void AClockThatGoesBackwardsGivesNoRiseAndNoFall()
    {
        LastReadingBook book = Book(box: Saved(40, charging: true), rates: Rate(ChargePart.Case, 30));

        ShownPart behind = Shown(book, ReadAt - TimeSpan.FromHours(1)).Case;
        Assert.AreEqual(ReadingKind.Last, behind.Kind, "Behind the read time: no rise.");
        Assert.AreEqual(40, behind.Percent, "...and no fall.");

        DateTimeOffset later = ReadAt + TimeSpan.FromHours(1);
        Assert.AreEqual(70, Shown(book, later, estimateClock: later).Case.Percent);

        ShownPart putBack = Shown(book, ReadAt + TimeSpan.FromMinutes(20), estimateClock: later).Case;
        Assert.AreEqual(70, putBack.Percent, "Put back by 40 minutes: the estimate holds where it was.");

        ShownPart putBackBehindTheRead = Shown(book, ReadAt - TimeSpan.FromDays(1), estimateClock: later).Case;
        Assert.AreEqual(70, putBackBehindTheRead.Percent, "Put back behind the reading itself: still no fall.");
    }

    [TestMethod]
    public void TheEstimatorItselfNeverGivesLessThanTheReadingAndNothingForANonChargingPart()
    {
        DateTimeOffset[] times = [ReadAt - TimeSpan.FromHours(1), ReadAt, ReadAt + TimeSpan.FromSeconds(1), ReadAt + TimeSpan.FromHours(1), ReadAt + TimeSpan.FromDays(10)];
        foreach (DateTimeOffset now in times)
        {
            int? grown = ChargeEstimator.Estimate(40, charging: true, ReadAt, 30, now);
            Assert.IsTrue(grown is null || (grown > 40 && grown <= 100), now.ToString("O"));
            Assert.IsNull(ChargeEstimator.Estimate(40, charging: false, ReadAt, 30, now), "Not charging: " + now.ToString("O"));
            Assert.IsNull(ChargeEstimator.Estimate(40, charging: true, ReadAt, null, now), "No rate: " + now.ToString("O"));
        }
    }

    // A saved reading is never live, however close its read time is to now: it was not heard by this run.
    [TestMethod]
    public void ASavedReadingIsNeverLive()
    {
        LastReadingBook book = Book(left: Saved(70, charging: false));

        ShownPart left = Shown(book, ReadAt + TimeSpan.FromSeconds(1)).Left;

        Assert.AreEqual(ReadingKind.Last, left.Kind);
        Assert.IsFalse(left.Fresh);
    }

    // A saved reading of another model is not the paired pair's, so it is not shown.
    [TestMethod]
    public void ASavedReadingOfAnotherModelIsNotShown()
    {
        LastReadingBook book = Book(left: Saved(70, charging: false, model: (ushort)(Model + 1)));

        Assert.IsFalse(Shown(book, ReadAt + TimeSpan.FromHours(1)).Left.HasValue);
        Assert.IsFalse(BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, PartReading.Unknown, false, false, ReadAt,
            new SavedBattery(Book(left: Saved(70, false)), null, null)).Left.HasValue, "With no paired model, nothing saved is shown.");
    }

    // A newer live reading replaces the estimate, even when it is lower than the estimate had grown to: it is a reading.
    [TestMethod]
    public void ANewerLiveReadingReplacesTheEstimate()
    {
        LastReadingBook book = Book(box: Saved(40, charging: true), rates: Rate(ChargePart.Case, 30));
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(1);
        var live = new PartReading(55, true, null) { ReadAt = now - TimeSpan.FromSeconds(2) };

        ShownPart box = BatteryFreshness.Shown(
            PartReading.Unknown, PartReading.Unknown, live, PartReading.Unknown, false, linked: true, now,
            new SavedBattery(book, Model, now)).Case;

        Assert.AreEqual(ReadingKind.Live, box.Kind);
        Assert.AreEqual(55, box.Percent);
    }
}
