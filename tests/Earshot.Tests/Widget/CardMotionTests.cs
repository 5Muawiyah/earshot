using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The card's entrance and exit as pure functions of time. The easing curve for cubic-bezier(0, 0, 0, 1) has a closed
// form, x(s) = s^3 and y(s) = 3s^2 - 2s^3, so Progress(t) = 3 t^(2/3) - 2t, which the solver is checked against.
[TestClass]
public sealed class CardMotionTests
{
    private const int Travel = 40;

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    [TestMethod]
    public void TheDecelerateCurveMatchesItsClosedForm()
    {
        Assert.AreEqual(0.0, CardMotion.Decelerate.Progress(0), 1e-9);
        Assert.AreEqual(0.5, CardMotion.Decelerate.Progress(0.125), 1e-9, "Half the way in the first eighth of the time.");
        Assert.AreEqual(0.889882, CardMotion.Decelerate.Progress(0.5), 1e-6);
        Assert.AreEqual(1.0, CardMotion.Decelerate.Progress(1), 1e-9);
        for (double t = 0.01; t < 1; t += 0.01)
        {
            double expected = (3 * Math.Pow(t, 2.0 / 3.0)) - (2 * t);
            Assert.AreEqual(expected, CardMotion.Decelerate.Progress(t), 1e-9, "At " + t);
        }
    }

    [TestMethod]
    public void TheCurveNeverLeavesZeroToOneAndOnlyRises()
    {
        double previous = 0;
        for (double t = -0.5; t <= 1.5; t += 0.005)
        {
            double p = CardMotion.Decelerate.Progress(t);
            Assert.IsTrue(p is >= 0 and <= 1, "In range at " + t);
            Assert.IsTrue(p >= previous - 1e-12, "Monotonic at " + t);
            previous = p;
        }
    }

    [TestMethod]
    public void AnEntranceStartsOneTravelDownAndInvisibleThenFadesInOverEightyThreeMilliseconds()
    {
        CardMotionPlan plan = CardMotion.EnterFromBelow(Travel);

        MotionFrame start = CardMotion.FrameAt(plan, TimeSpan.Zero);
        Assert.AreEqual(new MotionFrame(Travel, 0, Done: false), start);

        MotionFrame early = CardMotion.FrameAt(plan, Ms(10.4));
        Assert.AreEqual(32, early.Alpha, "255 x 10.4 / 83, rounded.");

        MotionFrame eighth = CardMotion.FrameAt(plan, Ms(31.25));
        Assert.AreEqual(Travel / 2, eighth.OffsetPx, "Half the travel after an eighth of 250 ms.");
        Assert.AreEqual(96, eighth.Alpha, "255 x 31.25 / 83, rounded: still fading in.");

        MotionFrame faded = CardMotion.FrameAt(plan, Ms(83));
        Assert.AreEqual(255, faded.Alpha);
        Assert.IsFalse(faded.Done, "The slide goes on after the fade.");

        MotionFrame end = CardMotion.FrameAt(plan, Ms(250));
        Assert.AreEqual(new MotionFrame(0, 255, Done: true), end);
        Assert.AreEqual(new MotionFrame(0, 255, Done: true), CardMotion.FrameAt(plan, Ms(900)), "Nothing moves after the end.");
    }

    // The exit's curve is cubic-bezier(1, 0, 1, 1): x(s) = 3s - 3s^2 + s^3 and y(s) = 3s^2 - 2s^3, slow at first and fast at the end.
    [TestMethod]
    public void TheAccelerateCurveIsTheExitsAndStartsSlow()
    {
        Assert.AreEqual(0.0, CardMotion.Accelerate.Progress(0), 1e-9);
        Assert.AreEqual(1.0, CardMotion.Accelerate.Progress(1), 1e-9);
        Assert.AreEqual(0.1101, CardMotion.Accelerate.Progress(0.5), 1e-3, "Barely a tenth of the way at half the time.");
        Assert.IsLessThan(CardMotion.Decelerate.Progress(0.125), CardMotion.Accelerate.Progress(0.125), "It starts slower than the entrance's curve.");
    }

    [TestMethod]
    public void AnExitSlidesDownAndFadesOutOverOneHundredAndSixtySevenMilliseconds()
    {
        CardMotionPlan plan = CardMotion.ExitFromRest(Travel);

        Assert.AreEqual(new MotionFrame(0, 255, Done: false), CardMotion.FrameAt(plan, TimeSpan.Zero));
        Assert.AreEqual((int)Math.Round(Travel * CardMotion.Accelerate.Progress(0.5), MidpointRounding.AwayFromZero), CardMotion.FrameAt(plan, Ms(83.5)).OffsetPx, "The offset follows the exit's curve.");
        Assert.AreEqual(128, CardMotion.FrameAt(plan, Ms(83.5)).Alpha, "Half faded at half the time, linearly.");
        Assert.AreEqual(new MotionFrame(Travel, 0, Done: true), CardMotion.FrameAt(plan, Ms(167)));
    }

    [TestMethod]
    public void AnUpwardTravelForATopTaskbarMirrorsTheDownwardOne()
    {
        CardMotionPlan down = CardMotion.EnterFromBelow(Travel);
        CardMotionPlan up = CardMotion.EnterFromBelow(-Travel);
        foreach (double ms in new[] { 0.0, 10, 31.25, 100, 250 })
        {
            Assert.AreEqual(-CardMotion.FrameAt(down, Ms(ms)).OffsetPx, CardMotion.FrameAt(up, Ms(ms)).OffsetPx, "At " + ms + " ms.");
        }
    }

    [TestMethod]
    public void WithAnimationEffectsOffThereIsOneFrameAtRestAndNoMovement()
    {
        Assert.AreEqual(new MotionFrame(0, 255, Done: true), CardMotion.FrameAt(CardMotion.EnterFromBelow(Travel), TimeSpan.Zero, reducedMotion: true));
        MotionFrame exit = CardMotion.FrameAt(CardMotion.ExitFromRest(Travel), TimeSpan.Zero, reducedMotion: true);
        Assert.IsTrue(exit.Done);
        Assert.AreEqual(0, exit.Alpha, "An exit with no animation hides at once.");
    }

    [TestMethod]
    public void AnEntranceCutShortByAnExitCarriesOnFromWhereTheCardIs()
    {
        CardMotionPlan enter = CardMotion.EnterFromBelow(Travel);
        MotionFrame at125 = CardMotion.FrameAt(enter, Ms(125));
        Assert.IsTrue(at125.OffsetPx is > 0 and < Travel, "Part way up.");

        CardMotionPlan exit = CardMotion.Exit(Travel, at125.OffsetPx, at125.Alpha / 255.0);
        MotionFrame first = CardMotion.FrameAt(exit, TimeSpan.Zero);
        Assert.AreEqual(at125.OffsetPx, first.OffsetPx, "The exit starts at the offset the entrance had reached.");
        Assert.AreEqual(at125.Alpha, first.Alpha, "And at its opacity.");
        MotionFrame end = CardMotion.FrameAt(exit, Ms(167));
        Assert.AreEqual(Travel, end.OffsetPx);
        Assert.AreEqual(0, end.Alpha);
        Assert.IsTrue(end.Done);
    }
}
