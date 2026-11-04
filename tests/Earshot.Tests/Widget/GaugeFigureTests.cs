using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The gauge's number among the buds: the lower of the buds read within the hold of the newer of the two, so a bud that crosses
// the live window while the other is still heard does not drop out and come back, and a bud read long before the other
// says nothing of the figure being heard.
[TestClass]
public sealed class GaugeFigureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static PartReading Read(int percent, double atSeconds) =>
        new(percent, false, InEar: null) { ReadAt = T0 + TimeSpan.FromSeconds(atSeconds) };

    private static GaugeFigure? Gauge(PartReading left, PartReading right, double nowSeconds) =>
        BatteryFreshness.Shown(left, right, PartReading.Unknown, PartReading.Unknown, onThisPc: true, linked: true, T0 + TimeSpan.FromSeconds(nowSeconds)).Gauge;

    [TestMethod]
    public void TheNumberStaysWithTheLowerBudWhenItPassesTheLiveWindowWhileTheOtherIsStillHeard()
    {
        PartReading left = Read(60, 0);

        GaugeFigure? both = Gauge(left, Read(70, 20), nowSeconds: 20);
        GaugeFigure? leftPast = Gauge(left, Read(70, 40), nowSeconds: 40);
        GaugeFigure? leftBack = Gauge(Read(60, 50), Read(70, 50), nowSeconds: 50);

        Assert.AreEqual(60, both?.Percent);
        Assert.AreEqual(60, leftPast?.Percent, "Left is 40 s old and right is heard: the number does not jump to right's 70.");
        Assert.AreEqual(ReadingKind.Last, leftPast?.Kind, "It is drawn as a last reading, since the bud it is from is not live.");
        Assert.AreEqual(60, leftBack?.Percent);
        Assert.AreEqual(ReadingKind.Live, leftBack?.Kind);
    }

    [TestMethod]
    public void ABudReadHoursBeforeTheOtherDoesNotLowerTheNumber()
    {
        GaugeFigure? figure = Gauge(Read(60, 5 * 3600), Read(40, 0), nowSeconds: 5 * 3600);

        Assert.AreEqual(60, figure?.Percent, "Right was read five hours before left: the number is left's.");
        Assert.AreEqual(ReadingKind.Live, figure?.Kind);
    }

    [TestMethod]
    public void WithNeitherBudLiveAnOldBudDoesNotLowerTheOneReadLast()
    {
        GaugeFigure? figure = Gauge(Read(60, 5 * 3600), Read(40, 0), nowSeconds: 6 * 3600);

        Assert.AreEqual(60, figure?.Percent, "Left was the last read, an hour ago; right six hours before: the number is left's.");
        Assert.AreEqual(ReadingKind.Last, figure?.Kind);
    }

    [TestMethod]
    public void TwoBudsReadWithinTheHoldOfEachOtherAreCompared()
    {
        TimeSpan hold = BatteryFreshness.GaugeBudHold;

        GaugeFigure? inside = Gauge(Read(50, 0), Read(70, hold.TotalSeconds), nowSeconds: 3600);
        GaugeFigure? outside = Gauge(Read(50, 0), Read(70, hold.TotalSeconds + 1), nowSeconds: 3600);

        Assert.AreEqual(50, inside?.Percent, "Exactly the hold apart: still compared.");
        Assert.AreEqual(70, outside?.Percent, "A second more: the older bud is out of the comparison.");
    }

    [TestMethod]
    public void TheHoldIsTheLostLimitAndNoLongerThanAnyRecordedGapNeeds()
    {
        Assert.AreEqual(BroadcastRules.LostLimit, BatteryFreshness.GaugeBudHold);
        Assert.IsGreaterThan(TimeSpan.FromSeconds(6.14), BatteryFreshness.GaugeBudHold, "Longer than the longest gap seen between messages of a set.");
    }

    [TestMethod]
    public void OfTwoBudsAtTheSameValueTheFresherOneIsTheNumbers()
    {
        GaugeFigure? figure = Gauge(Read(60, 0), Read(60, 40), nowSeconds: 40);

        Assert.AreEqual(60, figure?.Percent);
        Assert.AreEqual(ReadingKind.Live, figure?.Kind, "Right is live and the same value, so the number is live.");
        Assert.AreEqual(T0, figure?.ReadAt, "ReadAt is still the oldest bud's.");
    }

    [TestMethod]
    public void AnUnknownBudIsSkippedAndNoBudValueGivesNoNumber()
    {
        Assert.AreEqual(70, Gauge(PartReading.Unknown, Read(70, 0), nowSeconds: 1)?.Percent);
        Assert.IsNull(Gauge(PartReading.Unknown, PartReading.Unknown, nowSeconds: 1));
    }

    [TestMethod]
    public void ANumberFlipsAtMostOncePerChangeOfReadingAcrossAMinuteOfTheBudsTakingTurnsToBeHeard()
    {
        // Right is heard every 2 s, left every 20 s: the number never goes to right's 70 and back.
        var numbers = new List<int>();
        for (int s = 0; s <= 100; s += 2)
        {
            int lastLeft = s / 20 * 20;
            numbers.Add(Gauge(Read(60, lastLeft), Read(70, s), nowSeconds: s)!.Percent);
        }

        CollectionAssert.AreEqual(Enumerable.Repeat(60, numbers.Count).ToList(), numbers);
    }
}
