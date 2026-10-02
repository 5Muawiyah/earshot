using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The animator on a fake display (FakeVBlankClock): one frame per vertical blank, each the pure curve's frame at that blank's
// time, the frame source subscribed only while something moves, and with animation effects off one frame and no subscription.
[TestClass]
public sealed class CardAnimatorTests
{
    private static readonly Rectangle Rest = new(100, 200, 360, 300);
    private const int Travel = 40;

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

    private sealed class Rig : IDisposable
    {
        public Rig(double hz, bool animations = true)
        {
            Clock = new FakeVBlankClock(hz);

            // The motion begins at some blank, not at time zero.
            for (int i = 0; i < 7; i++)
            {
                Clock.Blank();
            }

            Driver = new FrameDriver(Clock, () => animations);
            Animator = new CardAnimator(Driver, Sink);
        }

        public FakeVBlankClock Clock { get; }

        public FrameDriver Driver { get; }

        public RecordingSink Sink { get; } = new();

        public CardAnimator Animator { get; }

        public void Dispose()
        {
            Animator.Dispose();
            Driver.Dispose();
        }
    }

    // The frames a motion made at each blank: one per blank, each the plan's frame at that blank's time since the start.
    private static void AssertFollowsPlan(Rig rig, CardMotionPlan plan, TimeSpan start, int blanks, int framesBefore)
    {
        Assert.HasCount(framesBefore + blanks, rig.Sink.Frames, "One frame per blank.");
        for (int k = 1; k <= blanks; k++)
        {
            TimeSpan at = rig.Clock.TimeOfBlank(7 + k) - start;
            MotionFrame expected = CardMotion.FrameAt(plan, at);
            (Rectangle rest, int offset, byte alpha) = rig.Sink.Frames[framesBefore + k - 1];
            Assert.AreEqual(Rest, rest);
            Assert.AreEqual(expected.OffsetPx, offset, "Offset at blank " + k + " (" + at.TotalMilliseconds + " ms).");
            Assert.AreEqual(expected.Alpha, alpha, "Alpha at blank " + k + ".");
            Assert.AreEqual(k == blanks, expected.Done, "Only the last blank's frame is the end.");
        }
    }

    [TestMethod]
    [DataRow(60.0, 15)]
    [DataRow(300.0, 75)]
    public void AnEntranceIsOneFramePerBlankOnTheCurveAndEndsExactlyAtRest(double hz, int blanks)
    {
        using var rig = new Rig(hz);
        TimeSpan start = rig.Clock.Now;

        rig.Animator.Enter(Rest, Travel);
        Assert.AreEqual((Rest, Travel, (byte)0), rig.Sink.Frames[^1], "Put at its starting offset, invisible, before the first blank.");
        Assert.AreEqual(1, rig.Clock.Subscriptions);

        int frames = rig.Clock.RunUntilIdle();

        Assert.AreEqual(blanks, frames, "250 ms of blanks at " + hz + " Hz.");
        AssertFollowsPlan(rig, CardMotion.EnterFromBelow(Travel), start, blanks, framesBefore: 1);
        Assert.AreEqual((Rest, 0, (byte)255), rig.Sink.Frames[^1], "Exactly at rest and opaque.");
        Assert.IsFalse(rig.Animator.Running);
        Assert.AreEqual(0, rig.Clock.Subscriptions, "The frame source is let go.");
    }

    // 167 ms is ten blanks and a fraction at 60 Hz, fifty and a fraction at 300 Hz: the end lands on the first blank after it.
    [TestMethod]
    [DataRow(60.0, 11)]
    [DataRow(300.0, 51)]
    public void AnExitIsOneFramePerBlankOnTheCurveAndHidesOnceAtTheEnd(double hz, int blanks)
    {
        using var rig = new Rig(hz);
        TimeSpan start = rig.Clock.Now;
        int hidden = 0;

        rig.Animator.Exit(Rest, Travel, () => hidden++);
        Assert.AreEqual((Rest, 0, (byte)255), rig.Sink.Frames[^1]);

        int frames = rig.Clock.RunUntilIdle();

        Assert.AreEqual(blanks, frames);
        Assert.AreEqual((int)Math.Ceiling(167 / (1000 / hz)), blanks, "The first blank at or after 167 ms.");
        AssertFollowsPlan(rig, CardMotion.ExitFromRest(Travel), start, blanks, framesBefore: 1);
        Assert.AreEqual((Rest, Travel, (byte)0), rig.Sink.Frames[^1]);
        Assert.AreEqual(1, hidden);
        Assert.AreEqual(0, rig.Clock.Subscriptions);
    }

    [TestMethod]
    [DataRow(60.0)]
    [DataRow(300.0)]
    public void AReopenDuringTheExitTurnsBackFromWhereTheCardIsWithNoJump(double hz)
    {
        using var rig = new Rig(hz);
        int hidden = 0;
        rig.Animator.Exit(Rest, Travel, () => hidden++);
        rig.Clock.RunUntil(rig.Clock.Now + TimeSpan.FromMilliseconds(120));
        (Rectangle _, int offsetThen, byte alphaThen) = rig.Sink.Frames[^1];
        Assert.IsTrue(offsetThen is > 0 and < Travel, "Part way down.");
        int before = rig.Sink.Frames.Count;

        rig.Animator.Enter(Rest, Travel);

        Assert.AreEqual((Rest, offsetThen, alphaThen), rig.Sink.Frames[before], "The entrance starts exactly where the exit was.");
        Assert.AreEqual(1, rig.Clock.Subscriptions, "One subscription, not two.");
        rig.Clock.RunUntilIdle();

        // From there it only rises and only brightens, each step no larger than the whole remaining way.
        for (int i = before + 1; i < rig.Sink.Frames.Count; i++)
        {
            Assert.IsLessThanOrEqualTo(rig.Sink.Frames[i - 1].Offset, rig.Sink.Frames[i].Offset, "Never back down, frame " + i);
            Assert.IsGreaterThanOrEqualTo(rig.Sink.Frames[i - 1].Alpha, rig.Sink.Frames[i].Alpha, "Never dimmer, frame " + i);
        }

        Assert.AreEqual((Rest, 0, (byte)255), rig.Sink.Frames[^1]);
        Assert.AreEqual(0, hidden, "The cancelled exit never hides the card.");
        Assert.AreEqual(0, rig.Clock.Subscriptions);
    }

    [TestMethod]
    public void AnExitDuringTheEntranceTurnsBackFromWhereTheCardIs()
    {
        using var rig = new Rig(300);
        int hidden = 0;
        rig.Animator.Enter(Rest, Travel);
        rig.Clock.RunUntil(rig.Clock.Now + TimeSpan.FromMilliseconds(40));
        (Rectangle _, int offsetThen, byte alphaThen) = rig.Sink.Frames[^1];

        rig.Animator.Exit(Rest, Travel, () => hidden++);
        Assert.AreEqual((Rest, offsetThen, alphaThen), rig.Sink.Frames[^1]);
        rig.Clock.RunUntilIdle();
        Assert.AreEqual(1, hidden);
        Assert.AreEqual((Rest, Travel, (byte)0), rig.Sink.Frames[^1]);
    }

    [TestMethod]
    public void WithAnimationEffectsOffThereIsExactlyOneFrameAtRestAndNoFrameIsAskedFor()
    {
        using var rig = new Rig(300, animations: false);

        rig.Animator.Enter(Rest, Travel);
        Assert.HasCount(1, rig.Sink.Frames);
        Assert.AreEqual((Rest, 0, (byte)255), rig.Sink.Frames[0], "At rest, opaque.");

        int hidden = 0;
        rig.Animator.Exit(Rest, Travel, () => hidden++);
        Assert.AreEqual(1, hidden, "Hidden at once.");
        Assert.HasCount(2, rig.Sink.Frames);
        Assert.AreEqual((Rest, 0, (byte)255), rig.Sink.Frames[1], "Left at rest for the next entrance.");

        Assert.AreEqual(0, rig.Clock.SubscribeCalls, "No frame was ever asked for.");
        Assert.AreEqual(0, rig.Clock.RunUntilIdle());
    }

    [TestMethod]
    public void AtRestNothingIsSubscribedAndBlanksReachNoOne()
    {
        using var rig = new Rig(300);
        Assert.AreEqual(0, rig.Clock.Subscriptions);
        rig.Animator.Enter(Rest, Travel);
        rig.Clock.RunUntilIdle();
        int frames = rig.Sink.Frames.Count;

        for (int i = 0; i < 600; i++)
        {
            Assert.IsFalse(rig.Clock.Blank(), "A blank at rest wakes no one.");
        }

        Assert.HasCount(frames, rig.Sink.Frames, "And applies nothing.");
        Assert.IsFalse(rig.Driver.Subscribed);
    }

    // A frame the window could not take ends the motion at once: nothing is left subscribed, and an exit goes on to hide the
    // card (the sink has already put the window at rest and made it opaque), so the card is never left half way.
    [TestMethod]
    public void AnExitWhoseFirstFrameFailsHidesTheCardAtOnceAndLeavesNothingSubscribed()
    {
        using var rig = new Rig(60);
        rig.Sink.FailFrom = 1;
        int hidden = 0;

        rig.Animator.Exit(Rest, Travel, () => hidden++);

        Assert.AreEqual(1, hidden, "The exit is finished, not abandoned.");
        Assert.IsFalse(rig.Animator.Running);
        Assert.AreEqual(0, rig.Clock.SubscribeCalls);
        rig.Clock.RunUntil(rig.Clock.Now + TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(1, hidden, "Once.");
        Assert.HasCount(1, rig.Sink.Frames, "No further frame is tried.");
    }

    [TestMethod]
    public void AnEntranceWhoseFirstFrameFailsLeavesNothingSubscribed()
    {
        using var rig = new Rig(60);
        rig.Sink.FailFrom = 1;

        rig.Animator.Enter(Rest, Travel);

        Assert.IsFalse(rig.Animator.Running);
        Assert.AreEqual(0, rig.Clock.Subscriptions);
        rig.Clock.RunUntil(rig.Clock.Now + TimeSpan.FromMilliseconds(500));
        Assert.HasCount(1, rig.Sink.Frames);
    }

    [TestMethod]
    public void AnExitWhoseLaterFrameFailsStopsAndHidesTheCard()
    {
        using var rig = new Rig(60);
        rig.Sink.FailFrom = 3;
        int hidden = 0;

        rig.Animator.Exit(Rest, Travel, () => hidden++);
        rig.Clock.Blank();
        Assert.AreEqual(0, hidden);
        rig.Clock.Blank();

        Assert.AreEqual(1, hidden, "The third frame failed, so the exit ended there.");
        Assert.IsFalse(rig.Animator.Running);
        Assert.AreEqual(0, rig.Clock.Subscriptions);
    }

    [TestMethod]
    public void DisposingLetsTheFrameSourceGo()
    {
        var rig = new Rig(60);
        rig.Animator.Enter(Rest, Travel);
        Assert.AreEqual(1, rig.Clock.Subscriptions);
        rig.Dispose();
        Assert.AreEqual(0, rig.Clock.Subscriptions);
    }
}
