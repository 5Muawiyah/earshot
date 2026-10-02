using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The general cubic-bezier evaluator, and every motion other than the card's entrance and exit, on a fake display: one frame
// per vertical blank at the blank's time, the end reached exactly, the frame source let go at rest, and with animation effects
// off one step and no frame asked for.
[TestClass]
public sealed class FrameMotionTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    // ---- The evaluator

    // For any curve, the point at parameter s is (x(s), y(s)); asked for the time x(s), the evaluator must give y(s) back.
    // That holds every curve to an exact oracle, with no closed form needed.
    [TestMethod]
    [DataRow(0.0, 0.0, 0.0, 1.0)]
    [DataRow(1.0, 0.0, 1.0, 1.0)]
    [DataRow(0.55, 0.55, 0.0, 1.0)]
    [DataRow(0.25, 0.1, 0.25, 1.0)]
    [DataRow(0.42, 0.0, 0.58, 1.0)]
    [DataRow(0.1, 0.9, 0.2, 1.4)]
    public void TheEvaluatorGivesBackThePointOfTheCurveAtEveryTime(double x1, double y1, double x2, double y2)
    {
        var curve = new CubicBezier(x1, y1, x2, y2);
        for (double s = 0.001; s < 1; s += 0.001)
        {
            double x = CubicBezier.Axis(x1, x2, s);
            double y = CubicBezier.Axis(y1, y2, s);
            Assert.AreEqual(y, curve.Progress(x), 1e-9, "At s = " + s);
        }
    }

    [TestMethod]
    public void TheEvaluatorMatchesTheClosedFormsItCanBeCheckedAgainst()
    {
        // cubic-bezier(0, 0, 0, 1): x = s^3, y = 3s^2 - 2s^3, so y = 3 t^(2/3) - 2t.
        // cubic-bezier(1/3, 1/3, 2/3, 2/3) is the straight line: x = y = s.
        var line = new CubicBezier(1 / 3.0, 1 / 3.0, 2 / 3.0, 2 / 3.0);
        for (double t = 0.005; t < 1; t += 0.005)
        {
            Assert.AreEqual((3 * Math.Pow(t, 2.0 / 3.0)) - (2 * t), FluentMotion.Enter.Progress(t), 1e-9, "Enter at " + t);
            Assert.AreEqual(t, line.Progress(t), 1e-9, "Line at " + t);
            Assert.AreEqual(t, FluentMotion.Linear.Progress(t), 1e-9, "Linear at " + t);
        }

        foreach (CubicBezier curve in new[] { FluentMotion.Enter, FluentMotion.Exit, FluentMotion.PointToPoint, FluentMotion.Linear })
        {
            Assert.AreEqual(0.0, curve.Progress(0));
            Assert.AreEqual(1.0, curve.Progress(1));
            Assert.AreEqual(0.0, curve.Progress(-3));
            Assert.AreEqual(1.0, curve.Progress(7));
        }
    }

    [TestMethod]
    public void ThePointToPointCurveStartsLikeALineAndSettlesSlowly()
    {
        CubicBezier p = FluentMotion.PointToPoint;
        double previous = 0;
        for (double t = 0.01; t <= 1; t += 0.01)
        {
            double v = p.Progress(t);
            Assert.IsGreaterThanOrEqualTo(previous, v, "Only rises, at " + t);
            previous = v;
        }

        Assert.IsGreaterThan(0.9, p.Progress(0.5), "Most of the way by half time.");
        Assert.IsLessThan(1.0, p.Progress(0.9), "Still settling near the end.");
    }

    [TestMethod]
    public void TheTimingsAreMicrosoftsThree()
    {
        Assert.AreEqual(Ms(83), FluentMotion.Fast);
        Assert.AreEqual(Ms(167), FluentMotion.Normal);
        Assert.AreEqual(Ms(250), FluentMotion.Slow);
        Assert.AreEqual(new CubicBezier(0, 0, 0, 1), FluentMotion.Enter);
        Assert.AreEqual(new CubicBezier(1, 0, 1, 1), FluentMotion.Exit);
        Assert.AreEqual(new CubicBezier(0.55, 0.55, 0, 1), FluentMotion.PointToPoint);
        Assert.AreEqual(FluentMotion.Normal, FluentMotion.ToggleDuration);
        Assert.AreEqual(FluentMotion.Enter, FluentMotion.ToggleCurve);
        Assert.AreEqual(FluentMotion.Fast, FluentMotion.HoverDuration);
    }

    // ---- One value on the driver

    private static (FakeVBlankClock Clock, FrameDriver Driver) Display(double hz, bool animations = true)
    {
        var clock = new FakeVBlankClock(hz);
        clock.Blank();
        clock.Blank();
        return (clock, new FrameDriver(clock, () => animations));
    }

    // Runs value from 0 to 1 and records each applied value with the blank's time.
    private static List<(TimeSpan At, double Value)> Run(FakeVBlankClock clock, FrameDriver driver, TimeSpan duration, CubicBezier curve)
    {
        var value = new AnimatedValue(0);
        var applied = new List<(TimeSpan, double)>();
        value.AnimateTo(1, driver, duration, curve, v => applied.Add((clock.Now, v)));
        clock.RunUntilIdle();
        return applied;
    }

    private static void AssertOnCurve(List<(TimeSpan At, double Value)> applied, TimeSpan start, TimeSpan duration, CubicBezier curve, int frames)
    {
        Assert.HasCount(frames, applied, "One value per blank.");
        foreach ((TimeSpan at, double value) in applied)
        {
            double t = Math.Min(1, (at - start) / duration);
            Assert.AreEqual(curve.Progress(t), value, 1e-12, "At " + (at - start).TotalMilliseconds + " ms.");
        }

        Assert.AreEqual(1.0, applied[^1].Value, "Exactly at the end.");
    }

    [TestMethod]
    [DataRow(60.0, 11)]
    [DataRow(300.0, 51)]
    public void AToggleKnobSlidesOnTheEntranceCurveOverOneHundredAndSixtySevenMilliseconds(double hz, int frames)
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(hz);
        TimeSpan start = clock.Now;
        List<(TimeSpan, double)> applied = Run(clock, driver, FluentMotion.ToggleDuration, FluentMotion.ToggleCurve);
        AssertOnCurve(applied, start, FluentMotion.ToggleDuration, FluentMotion.Enter, frames);
        Assert.AreEqual(0, clock.Subscriptions);
    }

    [TestMethod]
    [DataRow(60.0, 5)]
    [DataRow(300.0, 25)]
    public void AHoverFillFadesLinearlyOverEightyThreeMilliseconds(double hz, int frames)
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(hz);
        TimeSpan start = clock.Now;
        List<(TimeSpan, double)> applied = Run(clock, driver, FluentMotion.HoverDuration, FluentMotion.Linear);
        AssertOnCurve(applied, start, FluentMotion.HoverDuration, FluentMotion.Linear, frames);
        Assert.AreEqual(0, clock.Subscriptions);
    }

    [TestMethod]
    public void AValueSentBackWhileMovingTurnsFromWhereItIs()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(300);
        var value = new AnimatedValue(0);
        double shown = 0;
        value.AnimateTo(1, driver, FluentMotion.ToggleDuration, FluentMotion.ToggleCurve, v => shown = v);
        clock.RunUntil(clock.Now + Ms(30));
        double then = shown;
        Assert.IsTrue(then is > 0 and < 1);

        value.AnimateTo(0, driver, FluentMotion.ToggleDuration, FluentMotion.ToggleCurve, v => shown = v);
        Assert.AreEqual(then, value.ValueAt(clock.Now), 1e-12, "No jump at the turn.");
        Assert.AreEqual(1, clock.Subscriptions);
        double previous = then;
        while (clock.Subscriptions > 0)
        {
            clock.Blank();
            Assert.IsLessThanOrEqualTo(previous, shown);
            previous = shown;
        }

        Assert.AreEqual(0.0, shown);
    }

    [TestMethod]
    public void WithAnimationEffectsOffAValueIsPutThereInOneStepAndNoFrameIsAskedFor()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(300, animations: false);
        var value = new AnimatedValue(0);
        var applied = new List<double>();
        value.AnimateTo(1, driver, FluentMotion.ToggleDuration, FluentMotion.ToggleCurve, applied.Add);
        Assert.HasCount(1, applied);
        Assert.AreEqual(1.0, applied[0]);
        Assert.AreEqual(0, clock.SubscribeCalls);
    }

    [TestMethod]
    public void TheDriverHoldsOneSubscriptionForAnyNumberOfMotionsAndLetsGoAfterTheLast()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(60);
        int afterFrames = 0;
        driver.AfterFrame += () => afterFrames++;
        var a = new AnimatedValue(0);
        var b = new AnimatedValue(0);
        a.AnimateTo(1, driver, FluentMotion.Fast, FluentMotion.Linear, _ => { });
        b.AnimateTo(1, driver, FluentMotion.Slow, FluentMotion.Linear, _ => { });
        Assert.AreEqual(1, clock.Subscriptions);
        Assert.AreEqual(1, clock.SubscribeCalls);

        int frames = clock.RunUntilIdle();
        Assert.AreEqual(15, frames, "As long as the longer one.");
        Assert.AreEqual(15, afterFrames, "One repaint per frame for both.");
        Assert.AreEqual(0, clock.Subscriptions);
        Assert.IsFalse(clock.Blank(), "Nothing at rest.");
    }

    [TestMethod]
    public void AValueAlreadyWhereItIsSentAsksForNothing()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(300);
        var value = new AnimatedValue(1);
        int applied = 0;
        value.AnimateTo(1, driver, FluentMotion.Fast, FluentMotion.Linear, _ => applied++);
        Assert.AreEqual(0, applied);
        Assert.AreEqual(0, clock.SubscribeCalls);
    }

    // ---- Pages

    [TestMethod]
    public void ANewPageSlidesInFromTheRightGoingDeeperAndTheLeftComingBackAndFadesInOverEightyThreeMilliseconds()
    {
        Assert.AreEqual((40, 0.0), PageMotion.At(TimeSpan.Zero, 40, 1));
        Assert.AreEqual((-40, 0.0), PageMotion.At(TimeSpan.Zero, 40, -1));
        (int offset, double opacity) = PageMotion.At(Ms(31.25), 40, 1);
        Assert.AreEqual(20, offset, "Half the travel after an eighth of 250 ms, on the entrance curve.");
        Assert.AreEqual(31.25 / 83, opacity, 1e-9);
        Assert.AreEqual(1.0, PageMotion.At(Ms(83), 40, 1).Opacity);
        Assert.IsGreaterThan(0, PageMotion.At(Ms(83), 40, 1).Offset, "Still sliding after the fade.");
        Assert.AreEqual((0, 1.0), PageMotion.At(Ms(250), 40, 1));
        Assert.AreEqual((0, 1.0), PageMotion.At(Ms(900), 40, -1));
    }

    // ---- Fills

    [TestMethod]
    public void AFillMixesFromNothingToHoverToPressed()
    {
        Color hover = Color.FromArgb(10, 0, 0, 0);
        Color pressed = Color.FromArgb(6, 0, 0, 0);
        Assert.AreEqual(0, WidgetCard.FillColour(hover, pressed, 0).A);
        Assert.AreEqual(5, WidgetCard.FillColour(hover, pressed, 0.5).A);
        Assert.AreEqual(hover, WidgetCard.FillColour(hover, pressed, 1));
        Assert.AreEqual(8, WidgetCard.FillColour(hover, pressed, 1.5).A);
        Assert.AreEqual(pressed, WidgetCard.FillColour(hover, pressed, 2));
    }

    // ---- The gauge's ring

    private static GaugeContent Reading(int percent) => new(GaugeMode.Reading, percent, false, false, "");

    [TestMethod]
    [DataRow(60.0, 15)]
    [DataRow(300.0, 75)]
    public void TheRingMovesPointToPointToANewValueWhileTheNumberIsTheNewValueAtOnce(double hz, int frames)
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(hz);
        var motion = new GaugeMotion { Driver = driver };
        var rings = new List<(TimeSpan At, double Ring)>();
        motion.Changed += () => rings.Add((clock.Now, motion.Ring));
        motion.Show(Reading(40), onScreen: true);
        Assert.AreEqual(40.0, motion.Ring, "The first value is drawn at once.");
        Assert.AreEqual(0, clock.SubscribeCalls);

        GaugeContent next = Reading(80);
        TimeSpan start = clock.Now;
        motion.Show(next, onScreen: true);

        // Frame 0, the push made now: the number is the content's own, 80, while the ring is still where it was.
        Assert.AreEqual(80, next.Percent);
        Assert.AreEqual(40.0, motion.Ring);
        GaugePushKey key = GaugePushKey.Of(next, default!, default!, motion.Hover, "Segoe UI", Point.Empty, motion.Ring);
        Assert.AreEqual(80, key.Content.Percent, "What is pushed draws the number 80.");
        Assert.AreEqual(40.0, key.Ring, "And the ring at 40.");

        clock.RunUntilIdle();
        Assert.HasCount(frames, rings, "One push per blank over 250 ms.");
        foreach ((TimeSpan at, double ring) in rings)
        {
            double t = Math.Min(1, (at - start) / FluentMotion.RingDuration);
            Assert.AreEqual(40 + (40 * FluentMotion.PointToPoint.Progress(t)), ring, 1e-9);
        }

        Assert.AreEqual(80.0, motion.Ring, "Exactly at the new value.");
        Assert.AreEqual(0, clock.Subscriptions);

        motion.Show(next, onScreen: true);
        Assert.AreEqual(0, clock.Subscriptions, "The same value again asks for nothing.");
    }

    [TestMethod]
    public void TheRingLandsAtOnceWhenItAppearsGoesOffScreenOrAnimationEffectsAreOff()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(300);
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(new GaugeContent(GaugeMode.MarkOnly, null, false, false, ""), onScreen: true);
        motion.Show(Reading(60), onScreen: true);
        Assert.AreEqual(60.0, motion.Ring, "A first arc appears whole.");
        motion.Show(Reading(30), onScreen: false);
        Assert.AreEqual(30.0, motion.Ring, "A hidden gauge does not move.");
        Assert.AreEqual(0, clock.SubscribeCalls);

        (FakeVBlankClock still, FrameDriver off) = Display(300, animations: false);
        var quiet = new GaugeMotion { Driver = off };
        quiet.Show(Reading(30), onScreen: true);
        quiet.Show(Reading(90), onScreen: true);
        quiet.SetHover(true, onScreen: true);
        Assert.AreEqual(90.0, quiet.Ring);
        Assert.AreEqual(1.0, quiet.Hover);
        Assert.AreEqual(0, still.SubscribeCalls, "Nothing moves, nothing fades.");
    }

    [TestMethod]
    public void TheGaugesHoverFillFadesInOverEightyThreeMilliseconds()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(60);
        var motion = new GaugeMotion { Driver = driver };
        int pushes = 0;
        motion.Changed += () => pushes++;
        motion.SetHover(true, onScreen: true);
        Assert.AreEqual(0.0, motion.Hover);
        clock.RunUntilIdle();
        Assert.AreEqual(5, pushes);
        Assert.AreEqual(1.0, motion.Hover);
        Assert.AreEqual(0, clock.Subscriptions);
    }
}
