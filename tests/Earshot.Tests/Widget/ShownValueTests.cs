using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What a knob or an expander draws while it moves: the value the last frame was given, however that frame was stamped.
[TestClass]
public sealed class ShownValueTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(200);

    private static (FakeVBlankClock Clock, FrameDriver Driver) Rig(bool animations = true)
    {
        var clock = new FakeVBlankClock(60);
        return (clock, new FrameDriver(clock, () => animations));
    }

    [TestMethod]
    public void AValueRestsAtItsTargetWhenAFrameStampedAnHourAheadEndedItsMotion()
    {
        // The real clock's unpaced fallback stamps every frame an hour ahead: one frame ends the motion, and the repaint that
        // follows is at the real time, which is part-way through the duration.
        (FakeVBlankClock clock, FrameDriver driver) = Rig();
        clock.StampAhead = TimeSpan.FromHours(1);
        var value = new ShownValue(0);
        double lastFrame = double.NaN;

        value.AnimateTo(1, driver, Duration, FluentMotion.PointToPoint, v => lastFrame = v);
        clock.Blank();

        Assert.AreEqual(1.0, lastFrame, "The frame was given the end.");
        Assert.IsFalse(value.IsMoving(driver));
        Assert.AreEqual(1.0, value.Level(driver), "The repaint draws the end, not the value at the real time.");
    }

    [TestMethod]
    public void AValueMovingOnFramesStampedAheadIsDrawnAtTheFramesValue()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Rig();
        clock.StampAhead = TimeSpan.FromMilliseconds(30);
        var value = new ShownValue(0);
        var oracle = new AnimatedValue(0);
        oracle.MoveTo(1, TimeSpan.Zero, Duration, FluentMotion.PointToPoint);

        value.AnimateTo(1, driver, Duration, FluentMotion.PointToPoint, _ => { });
        clock.Blank();

        double expected = oracle.ValueAt(clock.Now + clock.StampAhead);
        Assert.IsTrue(value.IsMoving(driver));
        Assert.IsTrue(expected is > 0 and < 1);
        Assert.AreEqual(expected, value.Level(driver), 1e-12, "Drawn from the frame's own time.");
    }

    [TestMethod]
    public void AValueMovesFromWhereItWasToItsTargetOnAFakeDisplay()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Rig();
        var value = new ShownValue(0);
        value.AnimateTo(1, driver, Duration, FluentMotion.PointToPoint, _ => { });

        Assert.AreEqual(0.0, value.Level(driver), "Before the first frame it is where it was.");
        clock.Blank();
        double mid = value.Level(driver);
        Assert.IsTrue(mid is > 0 and < 1, "After one frame it is part-way.");

        clock.RunUntilIdle();
        Assert.AreEqual(1.0, value.Level(driver));
        Assert.IsFalse(driver.Subscribed, "At rest nothing is subscribed.");
    }

    [TestMethod]
    public void AValueIsAtItsTargetAtOnceWithAnimationEffectsOffAndTheCallbackIsCalledOnce()
    {
        (FakeVBlankClock clock, FrameDriver driver) = Rig(animations: false);
        var value = new ShownValue(0);
        var seen = new List<double>();

        value.AnimateTo(1, driver, Duration, FluentMotion.PointToPoint, seen.Add);

        Assert.HasCount(1, seen);
        Assert.AreEqual(1.0, seen[0]);
        Assert.AreEqual(1.0, value.Level(driver));
        Assert.AreEqual(0, clock.SubscribeCalls);
    }

    [TestMethod]
    public void AValueWithNoDriverIsAtItsTargetAtOnce()
    {
        var value = new ShownValue(0);

        value.AnimateTo(1, null, Duration, FluentMotion.PointToPoint, _ => { });

        Assert.AreEqual(1.0, value.Level(null));
    }
}
