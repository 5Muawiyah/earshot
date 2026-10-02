using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// How a charge rate is learned from the owner's own live readings (ChargeRateLearner): from one step up of a part to a
// later one, both while charging, the first heard closely enough to know its time, the last below 100%, a quarter of an
// hour to three hours apart.
[TestClass]
public sealed class AwayRateLearnerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private const ushort Model = BroadcastFixtures.PairedModel;

    private static DateTimeOffset At(double minutes) => T0 + TimeSpan.FromMinutes(minutes);

    // Readings of one part every half minute from start to end, at the level the function gives for each time.
    private static List<LearnedRate> Feed(ChargeRateLearner learner, ChargeComponent component, double start, double end, Func<double, int> level, bool charging = true)
    {
        var rates = new List<LearnedRate>();
        for (double m = start; m <= end; m += 0.5)
        {
            if (learner.Observe(component, Model, level(m), charging, At(m)) is { } rate)
            {
                rates.Add(rate);
            }
        }

        return rates;
    }

    [TestMethod]
    public void ARateIsLearnedBetweenTwoStepsUpOfOneCharge()
    {
        var learner = new ChargeRateLearner();

        // The case charges a step every 10 minutes: 40 until minute 5, 50 from 5, 60 from 15, 70 from 25.
        List<LearnedRate> rates = Feed(learner, ChargeComponent.Case, 0, 30, m => m < 5 ? 40 : m < 15 ? 50 : m < 25 ? 60 : 70);

        Assert.HasCount(1, rates, "The step at 15 is only 10 minutes after the start; the step at 25 completes a sample.");
        Assert.AreEqual(ChargePart.Case, rates[0].Part);
        Assert.AreEqual(Model, rates[0].Model);
        Assert.AreEqual(60.0, rates[0].PercentPerHour, 1e-9, "20 points from the step at 5 to the step at 25: 60 an hour.");
        Assert.AreEqual(TimeSpan.FromMinutes(20), rates[0].Span);
    }

    [TestMethod]
    public void BothBudsTeachOneBudRate()
    {
        var learner = new ChargeRateLearner();

        List<LearnedRate> left = Feed(learner, ChargeComponent.Left, 0, 30, m => m < 5 ? 40 : m < 15 ? 50 : m < 25 ? 60 : 70);
        List<LearnedRate> right = Feed(learner, ChargeComponent.Right, 0, 30, m => m < 5 ? 30 : m < 25 ? 40 : 50);

        Assert.AreEqual(ChargePart.Bud, left.Single().Part);
        Assert.AreEqual(ChargePart.Bud, right.Single().Part);
        Assert.AreEqual(30.0, right.Single().PercentPerHour, 1e-9);

        LastReadingBook book = LastReadingBook.Empty.WithRate(left.Single()).WithRate(right.Single());
        Assert.HasCount(1, book.Rates, "One rate per model and part: the later sample replaces the earlier.");
        Assert.AreEqual(30.0, ChargeRates.For(book, Model, ChargePart.Bud));
    }

    [TestMethod]
    public void AStartStepHeardAfterAGapIsNotTrustedSoNoSampleRunsFromIt()
    {
        var learner = new ChargeRateLearner();
        Assert.IsNull(learner.Observe(ChargeComponent.Case, Model, 40, true, At(0)));

        // Not heard for ten minutes, then a step up: when it happened is not known.
        Assert.IsNull(learner.Observe(ChargeComponent.Case, Model, 50, true, At(10)));
        List<LearnedRate> rates = Feed(learner, ChargeComponent.Case, 10.5, 50, m => m < 25 ? 50 : m < 35 ? 60 : m < 45 ? 70 : 80);

        Assert.HasCount(1, rates, "From the step at 25 to the one at 35 is too short; to the one at 45 is a sample.");
        Assert.AreEqual(60.0, rates[0].PercentPerHour, 1e-9, "Measured from the step at 25, seen as it happened, never from the one read late at 10.");
    }

    [TestMethod]
    public void NothingIsLearnedFromAPartThatIsNotChargingOrThatFalls()
    {
        var learner = new ChargeRateLearner();
        Assert.IsEmpty(Feed(learner, ChargeComponent.Left, 0, 60, m => 40 + (10 * (int)(m / 10)), charging: false), "Not charging.");

        var falls = new ChargeRateLearner();
        List<LearnedRate> rates = Feed(falls, ChargeComponent.Left, 0, 60, m => m < 5 ? 40 : m < 15 ? 50 : m < 20 ? 30 : m < 30 ? 40 : 50);
        Assert.IsEmpty(rates, "The fall at 15 ends the charge; from the step at 20 to the step at 30 is too short.");
    }

    [TestMethod]
    public void AStepTo100EndsNoSample()
    {
        var learner = new ChargeRateLearner();

        List<LearnedRate> rates = Feed(learner, ChargeComponent.Case, 0, 40, m => m < 5 ? 80 : m < 25 ? 90 : 100);

        Assert.IsEmpty(rates, "A part reads 100 for as long as it stays full, so when it got there is not known.");
    }

    [TestMethod]
    public void ResetForgetsTheRunSoAnotherSetsReadingNeverCompletesASample()
    {
        var learner = new ChargeRateLearner();
        Feed(learner, ChargeComponent.Case, 0, 10, m => m < 5 ? 40 : 50);

        learner.Reset();

        Assert.IsNull(learner.Observe(ChargeComponent.Case, Model, 70, true, At(30)), "After a reset the next step is not measured from the old one.");
    }
}
