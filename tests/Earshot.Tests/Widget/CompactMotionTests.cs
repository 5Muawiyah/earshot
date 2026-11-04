using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The pure parts of the compact card's motion: the crossfade's opacities and the hold that the pointer can stop.
[TestClass]
public sealed class CompactMotionTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [TestMethod]
    public void TheDurationsAreTheMotionTables()
    {
        Assert.AreEqual(Ms(250), CompactMotion.GrowDuration, "Compact to full");
        Assert.AreEqual(Ms(167), CompactMotion.FoldDuration, "Full to compact");
        Assert.AreEqual(Ms(83), CompactMotion.ReplaceDuration, "A row replaced in place");
        Assert.AreEqual(Ms(83), CompactMotion.ActionFadeDuration, "The button's glyph, ring and tick");
        Assert.AreEqual(Ms(800), CompactMotion.TickHold);
        Assert.AreEqual(Ms(1000), CompactMotion.TurnDuration, "One turn of the ring");
    }

    [TestMethod]
    public void AGrowFadesTheOldOutInTheFirst83MsAndTheNewInFrom167Ms()
    {
        (double oldAtStart, double newAtStart) = CompactMotion.Alphas(FormFadeKind.Sequential, 0);
        Assert.AreEqual(1.0, oldAtStart);
        Assert.AreEqual(0.0, newAtStart);

        (double oldGone, double newNot) = CompactMotion.Alphas(FormFadeKind.Sequential, 83.0 / 250);
        Assert.AreEqual(0.0, oldGone, 1e-9, "The old content is gone at 83 ms of 250");
        Assert.AreEqual(0.0, newNot, 1e-9);

        (double oldHalf, _) = CompactMotion.Alphas(FormFadeKind.Sequential, 41.5 / 250);
        Assert.AreEqual(0.5, oldHalf, 1e-9, "Halfway through its fade");

        (double oldNone, double newStarts) = CompactMotion.Alphas(FormFadeKind.Sequential, 167.0 / 250);
        Assert.AreEqual(0.0, oldNone);
        Assert.AreEqual(0.0, newStarts, 1e-9, "The new content starts at 167 ms of 250");

        (double _, double newHalf) = CompactMotion.Alphas(FormFadeKind.Sequential, 208.5 / 250);
        Assert.AreEqual(0.5, newHalf, 1e-9);

        (double oldEnd, double newEnd) = CompactMotion.Alphas(FormFadeKind.Sequential, 1);
        Assert.AreEqual(0.0, oldEnd);
        Assert.AreEqual(1.0, newEnd, 1e-9, "The new content is whole at the end");
    }

    [TestMethod]
    public void TheOldAndTheNewAreNeverBothWholeAndNeverBothSolidInTheGap()
    {
        for (double p = 0; p <= 1.0; p += 0.01)
        {
            (double o, double n) = CompactMotion.Alphas(FormFadeKind.Sequential, p);
            Assert.IsTrue(o + n <= 1.0 + 1e-9, "At " + p + " the two together are no more than one: " + (o + n));
        }
    }

    [TestMethod]
    public void AReplacementIsAStraightCrossfade()
    {
        (double o, double n) = CompactMotion.Alphas(FormFadeKind.Cross, 0.25);

        Assert.AreEqual(0.75, o, 1e-9);
        Assert.AreEqual(0.25, n, 1e-9);
    }

    [TestMethod]
    public void AProgressOutsideZeroToOneIsHeldToTheEnds()
    {
        Assert.AreEqual((1.0, 0.0), CompactMotion.Alphas(FormFadeKind.Cross, -3));
        Assert.AreEqual((0.0, 1.0), CompactMotion.Alphas(FormFadeKind.Cross, 7));
        Assert.AreEqual((0.0, 1.0), CompactMotion.Alphas(FormFadeKind.Cross, double.NaN));
    }

    [TestMethod]
    public void AHoldRunsOutWhenItsTimeHasPassed()
    {
        var hold = new CompactHold();
        hold.Start(Ms(6000), nowMs: 1000);

        Assert.IsTrue(hold.Active);
        Assert.IsFalse(hold.ElapsedAt(6999));
        Assert.AreEqual(Ms(1), hold.RemainingAt(6999));
        Assert.IsTrue(hold.ElapsedAt(7000));
        Assert.AreEqual(TimeSpan.Zero, hold.RemainingAt(90_000), "Never below zero");
    }

    [TestMethod]
    public void ThePointerOverStopsTheClockAndLeavingGoesOnWithWhatRemained()
    {
        var hold = new CompactHold();
        hold.Start(Ms(6000), nowMs: 0);

        hold.Pause(4000);
        Assert.IsTrue(hold.Paused);
        Assert.AreEqual(Ms(2000), hold.RemainingAt(4000));
        Assert.AreEqual(Ms(2000), hold.RemainingAt(500_000), "A paused hold keeps what it had");
        Assert.IsFalse(hold.ElapsedAt(500_000), "and never runs out");

        hold.Resume(500_000);
        Assert.IsFalse(hold.Paused);
        Assert.IsFalse(hold.ElapsedAt(501_999));
        Assert.IsTrue(hold.ElapsedAt(502_000), "Two seconds after it was let go, not six");
    }

    [TestMethod]
    public void ARestartTakesTheFullTimeAgain()
    {
        var hold = new CompactHold();
        hold.Start(Ms(6000), nowMs: 0);
        hold.Pause(5000);

        hold.Start(Ms(6000), nowMs: 9000);

        Assert.IsFalse(hold.Paused);
        Assert.AreEqual(Ms(6000), hold.RemainingAt(9000));
    }

    [TestMethod]
    public void ARowWithNoHoldStaysAndAStoppedHoldNeverRunsOut()
    {
        var hold = new CompactHold();
        hold.Start(null, nowMs: 0);

        Assert.IsFalse(hold.Active);
        Assert.IsNull(hold.RemainingAt(1_000_000));
        Assert.IsFalse(hold.ElapsedAt(1_000_000));

        hold.Start(Ms(100), 0);
        hold.Stop();
        Assert.IsFalse(hold.ElapsedAt(1_000));
    }
}
