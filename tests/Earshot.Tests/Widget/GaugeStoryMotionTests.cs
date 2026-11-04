using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What moves on the gauge while the story is told, on a fake display: the sweep turns once a second off the frame clock, the
// number leaves and the tick or cross comes in over 83 ms, the arc goes point to point, frames are asked for only while
// something moves and none while the gauge is idle or holding a tick or a cross, and with animation effects off or no driver at
// all there is nothing but the still states.
[TestClass]
public sealed class GaugeStoryMotionTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static (FakeVBlankClock Clock, FrameDriver Driver) Display(bool animations = true)
    {
        var clock = new FakeVBlankClock(60);
        clock.Blank();
        clock.Blank();
        return (clock, new FrameDriver(clock, () => animations));
    }

    private static GaugeContent Reading(int percent, GaugeStoryKind story = GaugeStoryKind.None, bool connect = true) =>
        new GaugeContent(GaugeMode.Reading, percent, false, false, "") { Story = new GaugeStoryView(story, connect) };

    private static GaugeContent NotOnThisPc(GaugeStoryKind story) =>
        new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, "") { Story = new GaugeStoryView(story, true) };

    // Runs the display until its clock reads at least until, counting the frames the motion asked to draw.
    private static int Run(FakeVBlankClock clock, GaugeMotion motion, TimeSpan until)
    {
        int frames = 0;
        void Count() => frames++;
        motion.Changed += Count;
        clock.RunUntil(until);
        motion.Changed -= Count;
        return frames;
    }

    [TestMethod]
    public void AGaugeWithNoStoryDrawsNoFrameAndAsksForNone()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };

        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70), onScreen: true);
        clock.RunUntil(clock.Now + Ms(500));

        Assert.IsNull(motion.Frame, "No story: the gauge draws as it always did.");
        Assert.AreEqual(0, clock.SubscribeCalls);
        Assert.IsFalse(driver.Subscribed);
    }

    [TestMethod]
    public void TheSweepTurnsOnceAPerSecondClockwiseOffTheFrameClock()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        TimeSpan begin = clock.Now;

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);

        Assert.IsNotNull(motion.Frame);
        Assert.AreEqual(GaugeStoryKind.Spinning, motion.Frame!.Value.Phase);
        Assert.IsTrue(driver.Subscribed, "A spin runs on the frame clock.");
        foreach (double ms in new[] { 250.0, 500.0, 750.0, 1000.0, 1250.0 })
        {
            clock.RunUntil(begin + Ms(ms));
            double turns = (clock.Now - begin).TotalMilliseconds / 1000.0;
            double expected = -90 + (360 * (turns - Math.Floor(turns)));
            Assert.AreEqual(expected, motion.Frame!.Value.Start, 1e-6, "At " + ms + " ms, one turn per 1000 ms, linear.");
        }

        clock.RunUntil(begin + Ms(30_000));
        Assert.IsTrue(driver.Subscribed, "The spin has no end of its own: it runs until told.");
        Assert.AreEqual(GaugeStoryFrame.SweepDegrees, motion.Frame!.Value.Extent, 1e-9);
    }

    [TestMethod]
    public void TheNumberLeavesOver83MillisecondsAndTheArcBecomesTheSweepOver167()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        TimeSpan begin = clock.Now;
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);

        Assert.AreEqual(1.0, motion.Frame!.Value.NumberOpacity, "Frame 0 is what was on screen.");
        Assert.AreEqual(252.0, motion.Frame!.Value.Extent, 1e-9, "The arc starts from the reading's.");

        clock.RunUntil(begin + Ms(50));
        Assert.IsTrue(motion.Frame!.Value.NumberOpacity is > 0 and < 1, "Part way: " + motion.Frame!.Value.NumberOpacity);

        clock.RunUntil(begin + Ms(100));
        Assert.AreEqual(0.0, motion.Frame!.Value.NumberOpacity, 1e-9, "Gone by 83 ms.");
        Assert.IsTrue(motion.Frame!.Value.Extent is > 90 and < 252, "The arc is still shortening at 100 ms: " + motion.Frame!.Value.Extent);

        clock.RunUntil(begin + Ms(200));
        Assert.AreEqual(90.0, motion.Frame!.Value.Extent, 1e-9, "A 90 degree sweep by 167 ms.");
    }

    [TestMethod]
    public void ConnectedStopsTheSpinSweepsTheArcToTheLiveLevelAndBringsTheTickInThenAsksForNoMoreFrames()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(1300));
        TimeSpan begin = clock.Now;

        motion.Show(Reading(70, GaugeStoryKind.Tick), onScreen: true);
        Assert.AreEqual(GaugeSlotGlyph.Tick, motion.Frame!.Value.Glyph);
        Assert.AreEqual(0.0, motion.Frame!.Value.GlyphOpacity, "The tick crossfades in, from nothing.");

        clock.RunUntil(begin + Ms(100));
        Assert.AreEqual(1.0, motion.Frame!.Value.GlyphOpacity, 1e-9, "In by 83 ms.");
        Assert.AreEqual(0.0, motion.Frame!.Value.NumberOpacity, 1e-9);

        int frames = clock.RunUntilIdle();
        Assert.IsGreaterThan(0, frames);
        Assert.AreEqual(252.0, motion.Frame!.Value.Extent, 1e-9, "The arc is at the live level.");
        Assert.AreEqual(GaugeStoryFrame.TwelveOClock + 360, motion.Frame!.Value.Start, 1e-9, "Round to 12 o'clock.");
        Assert.IsFalse(driver.Subscribed, "The hold draws nothing, so nothing asks for frames.");

        int idle = Run(clock, motion, clock.Now + Ms(800));
        Assert.AreEqual(0, idle);
    }

    [TestMethod]
    public void TheArcSweepsToTheLiveLevelOver250MillisecondsPointToPoint()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(400));
        double from = motion.Frame!.Value.Extent;
        TimeSpan begin = clock.Now;

        motion.Show(Reading(70, GaugeStoryKind.Tick), onScreen: true);
        clock.RunUntil(begin + Ms(125));
        double t = (clock.Now - begin) / FluentMotion.RingDuration;
        double expected = from + ((252 - from) * FluentMotion.PointToPoint.Progress(t));
        Assert.AreEqual(expected, motion.Frame!.Value.Extent, 1e-6);

        clock.RunUntilIdle();
        Assert.AreEqual(252.0, motion.Frame!.Value.Extent, 1e-9);
    }

    [TestMethod]
    public void ConnectedWithNoLiveReadingEmptiesTheArcToTheTrackOnly()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(NotOnThisPc(GaugeStoryKind.None), onScreen: true);
        motion.Show(NotOnThisPc(GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(500));

        motion.Show(NotOnThisPc(GaugeStoryKind.Tick), onScreen: true);
        clock.RunUntilIdle();

        Assert.AreEqual(0.0, motion.Frame!.Value.Extent, 1e-9);
        Assert.AreEqual(GaugeSlotGlyph.Tick, motion.Frame!.Value.Glyph);
    }

    [TestMethod]
    public void FailedEmptiesTheArcToTheTrackOver167MillisecondsAndBringsTheCrossIn()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(600));
        TimeSpan begin = clock.Now;

        motion.Show(Reading(70, GaugeStoryKind.Cross), onScreen: true);
        Assert.AreEqual(GaugeSlotGlyph.Cross, motion.Frame!.Value.Glyph);

        clock.RunUntil(begin + Ms(100));
        Assert.IsTrue(motion.Frame!.Value.Extent is > 0 and < 90, "Part way to the track: " + motion.Frame!.Value.Extent);
        Assert.AreEqual(1.0, motion.Frame!.Value.GlyphOpacity, 1e-9);

        clock.RunUntilIdle();
        Assert.AreEqual(0.0, motion.Frame!.Value.Extent, 1e-9);
        Assert.IsFalse(driver.Subscribed, "The cross holds with no frames.");
    }

    [TestMethod]
    public void TheEndOfAHoldCrossfadesTheGlyphOutAndTheNumberBackOver83MillisecondsThenLetsTheFrameGo()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Tick), onScreen: true);
        clock.RunUntilIdle();
        clock.RunUntil(clock.Now + Ms(800));
        TimeSpan begin = clock.Now;

        motion.Show(Reading(70), onScreen: true);

        Assert.IsNotNull(motion.Frame, "The glyph is still leaving.");
        Assert.AreEqual(GaugeStoryKind.None, motion.Frame!.Value.Phase);
        clock.RunUntil(begin + Ms(45));
        Assert.IsTrue(motion.Frame!.Value.GlyphOpacity is > 0 and < 1);
        Assert.IsTrue(motion.Frame!.Value.NumberOpacity is > 0 and < 1);

        clock.RunUntilIdle();
        Assert.IsNull(motion.Frame, "Back to the gauge at rest.");
        Assert.IsFalse(driver.Subscribed);
        Assert.AreEqual(0, Run(clock, motion, clock.Now + Ms(500)), "And nothing asks for a frame at idle.");
    }

    [TestMethod]
    public void AToggleThatEndsWithNothingToSayLeavesWithoutAGlyph()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning, connect: false), onScreen: true);
        clock.RunUntil(clock.Now + Ms(400));

        motion.Show(NotOnThisPc(GaugeStoryKind.None), onScreen: true);

        Assert.AreEqual(GaugeSlotGlyph.None, motion.Frame!.Value.Glyph, "Straight to what is true: no mark.");
        clock.RunUntilIdle();
        Assert.IsNull(motion.Frame);
        Assert.IsFalse(driver.Subscribed);
    }

    [TestMethod]
    public void ANewToggleDuringAHoldTakesTheSweepUpAgainFromTheGlyph()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Cross), onScreen: true);
        clock.RunUntilIdle();

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(300));

        Assert.AreEqual(GaugeStoryKind.Spinning, motion.Frame!.Value.Phase);
        Assert.AreEqual(0.0, motion.Frame!.Value.GlyphOpacity, 1e-9, "The cross is gone.");
        Assert.AreEqual(90.0, motion.Frame!.Value.Extent, 1e-9);
        Assert.IsTrue(driver.Subscribed);
    }

    [TestMethod]
    public void EachFrameOfTheSweepIsRaisedAsAChangeSoTheGaugeIsPushed()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);

        int frames = Run(clock, motion, clock.Now + Ms(1000));

        Assert.IsTrue(frames is >= 59 and <= 61, "One change per refresh of a 60 Hz display for a second: " + frames);
    }

    [TestMethod]
    public void WithAnimationEffectsOffTheSweepIsAStillQuarterAtTwelveOClockAndNothingAsksForFrames()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display(animations: false);
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70), onScreen: true);

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);

        GaugeStoryFrame spin = motion.Frame!.Value;
        Assert.AreEqual(GaugeStoryFrame.TwelveOClock, spin.Start);
        Assert.AreEqual(90.0, spin.Extent);
        Assert.AreEqual(0.0, spin.NumberOpacity);
        Assert.AreEqual(0, Run(clock, motion, clock.Now + Ms(2000)), "Still: no turn.");
        Assert.AreEqual(0, clock.SubscribeCalls);

        motion.Show(Reading(70, GaugeStoryKind.Tick), onScreen: true);
        GaugeStoryFrame tick = motion.Frame!.Value;
        Assert.AreEqual(GaugeSlotGlyph.Tick, tick.Glyph);
        Assert.AreEqual(1.0, tick.GlyphOpacity, "The tick is there whole, with no fade.");
        Assert.AreEqual(0.0, tick.NumberOpacity);
        Assert.AreEqual(252.0, tick.Extent, 1e-9, "The arc is at the level at once.");

        motion.Show(Reading(70, GaugeStoryKind.Cross), onScreen: true);
        Assert.AreEqual(GaugeSlotGlyph.Cross, motion.Frame!.Value.Glyph);
        Assert.AreEqual(0.0, motion.Frame!.Value.Extent);

        motion.Show(Reading(70), onScreen: true);
        Assert.IsNull(motion.Frame, "It leaves with no fade.");
        Assert.AreEqual(0, clock.SubscribeCalls, "Nothing was ever asked of the display.");
    }

    [TestMethod]
    public void WithNoDriverTheStoryIsTheSameStillStates()
    {
        var motion = new GaugeMotion();

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        Assert.AreEqual(GaugeStoryFrame.TwelveOClock, motion.Frame!.Value.Start);

        motion.Show(Reading(70, GaugeStoryKind.Tick), onScreen: true);
        Assert.AreEqual(1.0, motion.Frame!.Value.GlyphOpacity);

        motion.Show(Reading(70), onScreen: true);
        Assert.IsNull(motion.Frame);
    }

    [TestMethod]
    public void AGaugeThatIsNotOnScreenDoesNotTurnButKeepsTheStory()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: false);

        Assert.IsNotNull(motion.Frame, "The story is kept.");
        Assert.AreEqual(0, clock.SubscribeCalls, "Nothing runs for a gauge nobody sees.");

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        Assert.IsTrue(driver.Subscribed, "Shown again, it turns.");
    }

    [TestMethod]
    public void HidingTheGaugeStopsTheSweepAtOnceAndShowingItAgainTurnsItAgain()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(300));
        Assert.IsTrue(driver.Subscribed);

        motion.Suspend();

        Assert.IsFalse(driver.Subscribed, "Hidden for a full-screen application: no frames for a window that draws nothing.");
        Assert.AreEqual(GaugeStoryKind.Spinning, motion.Frame!.Value.Phase, "The story is kept.");

        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        Assert.IsTrue(driver.Subscribed);
        clock.RunUntil(clock.Now + Ms(250));
        Assert.AreEqual(GaugeStoryFrame.SweepDegrees, motion.Frame!.Value.Extent, 1e-9);
    }

    [TestMethod]
    public void AStoryThatEndedWhileHiddenIsDrawnAsWhatIsTrueWhenItIsShown()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Display();
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        motion.Suspend();

        motion.Show(Reading(70), onScreen: false);

        Assert.IsNull(motion.Frame, "No tick or cross is replayed for a gauge nobody saw.");
        Assert.AreEqual(0, Run(clock, motion, clock.Now + Ms(500)));
    }

    [TestMethod]
    public void TurningAnimationEffectsOffMidSpinMakesItStill()
    {
        bool animations = true;
        var clock = new FakeVBlankClock(60);
        clock.Blank();
        var driver = new FrameDriver(clock, () => animations);
        var motion = new GaugeMotion { Driver = driver };
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);
        clock.RunUntil(clock.Now + Ms(300));

        animations = false;
        motion.Show(Reading(70, GaugeStoryKind.Spinning), onScreen: true);

        Assert.IsFalse(driver.Subscribed);
        Assert.AreEqual(GaugeStoryFrame.TwelveOClock, motion.Frame!.Value.Start);
    }
}
