using System.Drawing;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The animator on a fake clock: every frame it hands the window is the pure curve's frame at the time the clock
// shows, and with animation effects off there is no timer and no movement.
[TestClass]
public sealed class CardAnimatorTests
{
    private static readonly Rectangle Rest = new(100, 200, 360, 300);

    private sealed class RecordingSink : ICardWindowMotion
    {
        public List<(Rectangle At, int Offset, byte Alpha)> Frames { get; } = [];

        // The frame number (from 1) from which every frame fails, or 0 for none.
        public int FailFrom { get; set; }

        public bool Apply(Rectangle rest, int offsetPx, byte alpha)
        {
            Frames.Add((rest, offsetPx, alpha));
            return FailFrom == 0 || Frames.Count < FailFrom;
        }
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

    [TestMethod]
    public void AnEntranceAppliesTheCurvesFrameAtEachTimeTheClockReaches()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink();
        using var animator = new CardAnimator(time, sink, () => true, static a => a());

        animator.Enter(Rest, 48);
        Assert.AreEqual((Rest, 48, (byte)0), sink.Frames[^1], "The card is put at its starting offset, invisible, before the first tick.");
        Assert.IsTrue(animator.Running);

        CardMotionPlan plan = CardMotion.EnterFromBelow(48);
        // Each time is at least one frame (16 ms) after the last, so the fake clock fires the timer at every one.
        foreach (double ms in new[] { 16.0, 33, 83, 120, 250 })
        {
            time.Advance(Ms(ms) - (time.GetElapsedTime(0)));
            MotionFrame expected = CardMotion.FrameAt(plan, Ms(ms));
            (Rectangle rest, int offset, byte alpha) = sink.Frames[^1];
            Assert.AreEqual(Rest, rest);
            Assert.AreEqual(expected.OffsetPx, offset, "Offset at " + ms + " ms.");
            Assert.AreEqual(expected.Alpha, alpha, "Alpha at " + ms + " ms.");
        }

        Assert.IsFalse(animator.Running, "The timer stops when the motion is over.");
        Assert.AreEqual(0, time.LiveTimers);
        Assert.AreEqual((Rest, 0, (byte)255), sink.Frames[^1]);
    }

    // A frame the window could not take ends the motion at once: no timer is left running, and an exit goes on to hide the card
    // (the sink has already put the window at rest and made it opaque), so the card is never left half way.
    [TestMethod]
    public void AnExitWhoseFirstFrameFailsHidesTheCardAtOnceAndLeavesNoTimer()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink { FailFrom = 1 };
        using var animator = new CardAnimator(time, sink, () => true, static a => a());
        int hidden = 0;

        animator.Exit(Rest, 48, () => hidden++);

        Assert.AreEqual(1, hidden, "The exit is finished, not abandoned.");
        Assert.IsFalse(animator.Running);
        Assert.AreEqual(0, time.LiveTimers);
        time.Advance(Ms(500));
        Assert.AreEqual(1, hidden, "Once.");
        Assert.HasCount(1, sink.Frames, "No further frame is tried.");
    }

    [TestMethod]
    public void AnEntranceWhoseFirstFrameFailsLeavesNoTimerAndHidesNothing()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink { FailFrom = 1 };
        using var animator = new CardAnimator(time, sink, () => true, static a => a());

        animator.Enter(Rest, 48);

        Assert.IsFalse(animator.Running);
        Assert.AreEqual(0, time.LiveTimers);
        time.Advance(Ms(500));
        Assert.HasCount(1, sink.Frames, "No further frame is tried.");
    }

    [TestMethod]
    public void AnExitWhoseLaterFrameFailsStopsTheTimerAndHidesTheCard()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink { FailFrom = 3 };
        using var animator = new CardAnimator(time, sink, () => true, static a => a());
        int hidden = 0;

        animator.Exit(Rest, 48, () => hidden++);
        time.Advance(Ms(16));
        Assert.AreEqual(0, hidden);
        time.Advance(Ms(16));

        Assert.AreEqual(1, hidden, "The third frame failed, so the exit ended there.");
        Assert.IsFalse(animator.Running);
        Assert.AreEqual(0, time.LiveTimers);
    }

    [TestMethod]
    public void AnExitHidesTheCardOnlyOnceItIsGone()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink();
        using var animator = new CardAnimator(time, sink, () => true, static a => a());
        int hidden = 0;

        animator.Exit(Rest, 48, () => hidden++);
        time.Advance(Ms(100));
        Assert.AreEqual(0, hidden, "Still sliding at 100 ms.");
        time.Advance(Ms(67));
        Assert.AreEqual(1, hidden, "Hidden when the 167 ms are up.");
        Assert.AreEqual((Rest, 48, (byte)0), sink.Frames[^1]);
        time.Advance(Ms(500));
        Assert.AreEqual(1, hidden, "Only once.");
    }

    [TestMethod]
    public void WithAnimationEffectsOffTheCardIsShownAtRestInOneStepAndHidesAtOnce()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink();
        using var animator = new CardAnimator(time, sink, () => false, static a => a());

        animator.Enter(Rest, 48);
        Assert.AreEqual(1, sink.Frames.Count, "One Apply.");
        Assert.AreEqual((Rest, 0, (byte)255), sink.Frames[0], "At rest, opaque.");
        Assert.AreEqual(0, time.TimersCreated, "No timer is made when nothing moves.");

        int hidden = 0;
        animator.Exit(Rest, 48, () => hidden++);
        Assert.AreEqual(1, hidden, "Hidden at once.");
        Assert.AreEqual(2, sink.Frames.Count);
        Assert.AreEqual((Rest, 0, (byte)255), sink.Frames[1], "Left at rest for the next entrance.");
        Assert.AreEqual(0, time.TimersCreated);
    }

    [TestMethod]
    public void AnExitDuringAnEntranceStartsFromTheOffsetAndOpacityReached()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink();
        using var animator = new CardAnimator(time, sink, () => true, static a => a());
        int hidden = 0;

        animator.Enter(Rest, 48);
        time.Advance(Ms(125));
        (Rectangle _, int offsetAt125, byte alphaAt125) = sink.Frames[^1];
        Assert.IsTrue(offsetAt125 is > 0 and < 48);

        animator.Exit(Rest, 48, () => hidden++);
        (Rectangle _, int offset, byte alpha) = sink.Frames[^1];
        Assert.AreEqual(offsetAt125, offset, "The exit takes over at the offset the entrance had.");
        Assert.AreEqual(alphaAt125, alpha);
        Assert.AreEqual(1, time.LiveTimers, "One timer at a time.");

        time.Advance(Ms(167));
        Assert.AreEqual(1, hidden);
        Assert.AreEqual(0, time.LiveTimers);
    }

    [TestMethod]
    public void AnEntranceDuringAnExitComesBackFromWhereTheCardIsAndTheOldExitNeverHides()
    {
        var time = new TestTimeProvider();
        var sink = new RecordingSink();
        using var animator = new CardAnimator(time, sink, () => true, static a => a());
        int hidden = 0;

        animator.Exit(Rest, 48, () => hidden++);
        time.Advance(Ms(60));
        (Rectangle _, int offsetAt60, byte alphaAt60) = sink.Frames[^1];

        animator.Enter(Rest, 48);
        (Rectangle _, int offset, byte alpha) = sink.Frames[^1];
        Assert.AreEqual(offsetAt60, offset);
        Assert.AreEqual(alphaAt60, alpha);

        time.Advance(Ms(400));
        Assert.AreEqual(0, hidden, "The cancelled exit's hide never runs.");
        Assert.AreEqual((Rest, 0, (byte)255), sink.Frames[^1]);
    }

    [TestMethod]
    public void DisposingStopsTheTimer()
    {
        var time = new TestTimeProvider();
        var animator = new CardAnimator(time, new RecordingSink(), () => true, static a => a());
        animator.Enter(Rest, 48);
        Assert.AreEqual(1, time.LiveTimers);
        animator.Dispose();
        Assert.AreEqual(0, time.LiveTimers);
    }
}
