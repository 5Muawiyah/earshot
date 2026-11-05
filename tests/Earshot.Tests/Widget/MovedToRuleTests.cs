using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The moved-to rule against messages at chosen times. Pure: it is handed the messages and the time, and says what it makes of a
// leave. The figures are the ones MovedToRule names as design choices; these tests pin where each one bites.
[TestClass]
public sealed class MovedToRuleTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T + TimeSpan.FromSeconds(seconds);

    private static MovedToRule Left(double beforeSeconds = 0)
    {
        var rule = new MovedToRule();
        rule.Left(At(beforeSeconds));
        return rule;
    }

    private static void Hear(MovedToRule rule, double seconds, sbyte rssi = -62, bool caseKnown = false) =>
        rule.Observe(At(seconds), rssi, caseKnown);

    // ---- Moved

    [TestMethod]
    public void ThreeCaseUnknownMessagesTwoSecondsApartAndNearAreMoved()
    {
        MovedToRule rule = Left();
        Hear(rule, 0.9);
        Hear(rule, 2.4);
        Assert.IsNull(rule.Check(At(2.4), backOnThisPc: false), "Two messages are not enough.");

        Hear(rule, 3.8);
        MovedVerdict? verdict = rule.Check(At(3.8), backOnThisPc: false);

        Assert.IsNotNull(verdict);
        Assert.AreEqual(MovedVerdictKind.Moved, verdict.Kind);
        Assert.AreEqual(3, verdict.Summary.Messages);
        Assert.AreEqual(TimeSpan.FromMilliseconds(900), verdict.Summary.First);
        Assert.AreEqual(TimeSpan.FromMilliseconds(3800), verdict.Summary.Last);
        Assert.AreEqual(-62, verdict.Summary.MedianDbm);
        Assert.AreEqual(0, verdict.Summary.CaseKnown);
        Assert.AreEqual(TimeSpan.FromMilliseconds(3800), verdict.After);
    }

    [TestMethod]
    public void TheVerdictIsGivenOnce()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 2);
        Hear(rule, 3.5);
        Assert.IsNotNull(rule.Check(At(3.5), false));

        Hear(rule, 4);

        Assert.IsNull(rule.Check(At(4), false));
        Assert.IsNull(rule.Check(At(10), false), "Nothing more is said at the bound once it was moved.");
    }

    // Three messages inside two seconds are a passer-by's burst, not a pair that stayed: the fourth, later, makes the span.
    [TestMethod]
    public void TheFirstAndLastMessageMustBeTwoSecondsApart()
    {
        MovedToRule rule = Left();
        Hear(rule, 1.0);
        Hear(rule, 1.5);
        Hear(rule, 2.9);
        Assert.IsNull(rule.Check(At(2.9), false), "1.9 s apart is under the span.");

        Hear(rule, 3.0);
        MovedVerdict? verdict = rule.Check(At(3.0), false);

        Assert.AreEqual(MovedVerdictKind.Moved, verdict?.Kind);
        Assert.AreEqual(4, verdict!.Summary.Messages);
    }

    [TestMethod]
    public void ExactlyTwoSecondsApartIsEnough()
    {
        MovedToRule rule = Left();
        Hear(rule, 1.0);
        Hear(rule, 2.0);
        Hear(rule, 3.0);

        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(3.0), false)?.Kind);
    }

    // The median, not the strongest: two near messages and one far one still leave the middle near.
    [TestMethod]
    public void TheMedianDecidesNotTheStrongest()
    {
        MovedToRule rule = Left();
        Hear(rule, 1, rssi: -60);
        Hear(rule, 2, rssi: -95);
        Hear(rule, 3.5, rssi: -70);

        MovedVerdict? verdict = rule.Check(At(3.5), false);

        Assert.AreEqual(MovedVerdictKind.Moved, verdict?.Kind);
        Assert.AreEqual(-70, verdict!.Summary.MedianDbm);
    }

    [TestMethod]
    public void AMedianExactlyAtTheThresholdIsNear()
    {
        MovedToRule rule = Left();
        Hear(rule, 1, rssi: -80);
        Hear(rule, 2, rssi: -80);
        Hear(rule, 3, rssi: -80);

        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(3), false)?.Kind);
    }

    [TestMethod]
    public void AWeakMedianIsNotHeardAtTheBound()
    {
        MovedToRule rule = Left();
        Hear(rule, 1, rssi: -85);
        Hear(rule, 2, rssi: -85);
        Hear(rule, 3, rssi: -85);
        Assert.IsNull(rule.Check(At(3), false), "A weak signal waits out the bound in case it strengthens.");

        MovedVerdict? verdict = rule.Check(At(10), false);

        Assert.AreEqual(MovedVerdictKind.NotHeard, verdict?.Kind);
        Assert.AreEqual(3, verdict!.Summary.Messages);
        Assert.AreEqual(-85, verdict.Summary.MedianDbm);
    }

    // A later message that lifts the median over the threshold within the bound is still moved.
    [TestMethod]
    public void AMedianThatRisesWithinTheBoundIsMoved()
    {
        MovedToRule rule = Left();
        Hear(rule, 1, rssi: -90);
        Hear(rule, 2, rssi: -90);
        Hear(rule, 3, rssi: -90);
        Hear(rule, 4, rssi: -60);
        Assert.IsNull(rule.Check(At(4), false), "The median is still -90.");
        Hear(rule, 5, rssi: -60);
        Hear(rule, 6, rssi: -60);

        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(6), false)?.Kind);
    }

    // ---- The bound

    [TestMethod]
    public void NothingHeardIsNotHeardAtTheBoundAndNotBefore()
    {
        MovedToRule rule = Left();

        Assert.IsNull(rule.Check(At(9.9), false));
        MovedVerdict? verdict = rule.Check(At(10), false);

        Assert.AreEqual(MovedVerdictKind.NotHeard, verdict?.Kind);
        Assert.AreEqual(0, verdict!.Summary.Messages);
        Assert.AreEqual(TimeSpan.FromSeconds(10), verdict.After);
    }

    [TestMethod]
    public void TwoMessagesAreTooFew()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 4);

        Assert.AreEqual(MovedVerdictKind.NotHeard, rule.Check(At(10), false)?.Kind);
    }

    [TestMethod]
    public void AMessageAtExactlyTheBoundCounts()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 5);
        Hear(rule, 10.0);

        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(10.0), false)?.Kind);
    }

    [TestMethod]
    public void AMessageAfterTheBoundDoesNotCount()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 5);
        Hear(rule, 10.001);

        MovedVerdict? verdict = rule.Check(At(10.001), false);

        Assert.AreEqual(MovedVerdictKind.NotHeard, verdict?.Kind);
        Assert.AreEqual(2, verdict!.Summary.Messages);
    }

    // The watch is for the messages after the leave: those from before it say nothing about where the pair went.
    [TestMethod]
    public void MessagesBeforeTheLeaveDoNotCount()
    {
        var rule = new MovedToRule();
        rule.Observe(At(-3), -60, caseKnown: false);
        rule.Observe(At(-2), -60, caseKnown: false);
        rule.Observe(At(-1), -60, caseKnown: false);
        rule.Left(At(0));
        Hear(rule, 1);

        Assert.IsNull(rule.Check(At(1), false));
        Assert.AreEqual(MovedVerdictKind.NotHeard, rule.Check(At(10), false)?.Kind);
    }

    // ---- Into the case

    [TestMethod]
    public void ACaseKnownMessageAfterTheLeaveIsIntoTheCase()
    {
        MovedToRule rule = Left();
        Hear(rule, 1.0);
        Hear(rule, 1.5, caseKnown: true);

        MovedVerdict? verdict = rule.Check(At(1.5), false);

        Assert.AreEqual(MovedVerdictKind.IntoCase, verdict?.Kind);
        Assert.AreEqual(1, verdict!.Summary.CaseKnown);
    }

    [TestMethod]
    public void ACaseKnownMessageJustBeforeTheLeaveIsIntoTheCaseAtOnce()
    {
        var rule = new MovedToRule();
        rule.Observe(At(-5), -60, caseKnown: true);
        rule.Left(At(0));

        MovedVerdict? verdict = rule.Check(At(0), false);

        Assert.AreEqual(MovedVerdictKind.IntoCase, verdict?.Kind);
        Assert.AreEqual(1, verdict!.Summary.CaseKnown);
    }

    [TestMethod]
    public void ACaseKnownMessageTenSecondsBeforeStillCountsAndEarlierDoesNot()
    {
        var onTheEdge = new MovedToRule();
        onTheEdge.Observe(At(-10), -60, caseKnown: true);
        onTheEdge.Left(At(0));
        Assert.AreEqual(MovedVerdictKind.IntoCase, onTheEdge.Check(At(0), false)?.Kind);

        var tooOld = new MovedToRule();
        tooOld.Observe(At(-10.001), -60, caseKnown: true);
        tooOld.Left(At(0));
        Assert.IsNull(tooOld.Check(At(0), false));
    }

    // A case-known message after the verdict is not a verdict: it is what clears the latch, in the service.
    [TestMethod]
    public void ACaseKnownMessageAfterTheVerdictIsNotAnotherVerdict()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 2);
        Hear(rule, 3.5);
        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(3.5), false)?.Kind);

        Hear(rule, 5, caseKnown: true);

        Assert.IsNull(rule.Check(At(5), false));
    }

    // ---- Back on this PC

    [TestMethod]
    public void ThisPcBeingActiveAgainFirstIsBackHere()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);

        MovedVerdict? verdict = rule.Check(At(2), backOnThisPc: true);

        Assert.AreEqual(MovedVerdictKind.BackHere, verdict?.Kind);
        Assert.AreEqual(1, verdict!.Summary.Messages);
    }

    [TestMethod]
    public void BackHereWinsOverMessagesThatWouldHaveMadeItMoved()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 2);
        Hear(rule, 3.5);

        Assert.AreEqual(MovedVerdictKind.BackHere, rule.Check(At(3.5), backOnThisPc: true)?.Kind);
    }

    // ---- Watching, the long line and what is held

    [TestMethod]
    public void NothingIsWatchedUntilALeave()
    {
        var rule = new MovedToRule();
        Hear(rule, 1);

        Assert.IsFalse(rule.Watching);
        Assert.IsNull(rule.NextDue);
        Assert.IsNull(rule.Check(At(20), false));
    }

    [TestMethod]
    public void TheNextDueTimeIsTheBoundThenTheLongLineThenNothing()
    {
        MovedToRule rule = Left();
        Assert.AreEqual(At(10), rule.NextDue);

        Assert.AreEqual(MovedVerdictKind.NotHeard, rule.Check(At(10), false)?.Kind);
        Assert.AreEqual(At(30), rule.NextDue);

        Assert.IsNull(rule.TakeLongSummary(At(29.9)));
        Assert.IsNotNull(rule.TakeLongSummary(At(30)));
        Assert.IsNull(rule.NextDue);
        Assert.IsNull(rule.TakeLongSummary(At(31)), "The long line is taken once.");
    }

    // What a longer bound would have said: every message in the thirty seconds, and the case-known ones among them.
    [TestMethod]
    public void TheLongSummaryCountsThirtySecondsOfMessages()
    {
        MovedToRule rule = Left();
        for (int i = 1; i <= 18; i++)
        {
            Hear(rule, i * 1.6, rssi: -60);
        }

        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(4.8), false)?.Kind);
        MovedSummary? summary = rule.TakeLongSummary(At(30));

        Assert.IsNotNull(summary);
        Assert.AreEqual(18, summary.Messages);
        Assert.AreEqual(-60, summary.MedianDbm);
        Assert.AreEqual(0, summary.CaseKnown);
    }

    [TestMethod]
    public void ANewLeaveStartsAFreshWatch()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);
        Hear(rule, 2);
        Hear(rule, 3.5);
        Assert.AreEqual(MovedVerdictKind.Moved, rule.Check(At(3.5), false)?.Kind);

        rule.Left(At(100));

        Assert.IsTrue(rule.Watching);
        Assert.AreEqual(At(110), rule.NextDue);
        Assert.AreEqual(MovedVerdictKind.NotHeard, rule.Check(At(110), false)?.Kind);
    }

    [TestMethod]
    public void ResetForgetsTheWatchAndTheMessages()
    {
        MovedToRule rule = Left();
        Hear(rule, 1);

        rule.Reset();

        Assert.IsFalse(rule.Watching);
        Assert.IsNull(rule.NextDue);
        Assert.AreEqual(0, rule.HeldForTest);
    }

    // ---- What is held is bounded

    [TestMethod]
    public void OldMessagesAreDroppedWhenNothingIsWatched()
    {
        var rule = new MovedToRule();
        for (int i = 0; i < 100; i++)
        {
            Hear(rule, i);
        }

        Assert.IsLessThanOrEqualTo(21, rule.HeldForTest, "Only the window and the bound are kept: about twenty seconds.");
    }

    [TestMethod]
    public void NoMoreThanTheCapIsHeldEvenInOneInstant()
    {
        var rule = new MovedToRule();
        for (int i = 0; i < 5000; i++)
        {
            rule.Observe(At(0), -60, caseKnown: false);
        }

        Assert.AreEqual(BroadcastRules.MaxRefindSamples, rule.HeldForTest);
    }

    // A watch keeps what the long line needs: the thirty seconds after the leave are not pruned away.
    [TestMethod]
    public void AWatchKeepsTheMessagesTheLongLineNeeds()
    {
        MovedToRule rule = Left();
        for (int i = 1; i <= 60; i++)
        {
            Hear(rule, i * 0.5, rssi: -60);
        }

        _ = rule.Check(At(10), false);
        MovedSummary? summary = rule.TakeLongSummary(At(30));

        Assert.AreEqual(60, summary?.Messages);
    }

    // ---- The figures are the ones named

    [TestMethod]
    public void TheFiguresAreTheNamedOnes()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), MovedToRule.MovedBound);
        int messages = MovedToRule.MovedMessages;
        Assert.AreEqual(3, messages);
        Assert.AreEqual(TimeSpan.FromSeconds(2), MovedToRule.MovedSpan);
        double near = MovedToRule.MovedNearDbm;
        Assert.AreEqual(-80.0, near);
        Assert.AreEqual(TimeSpan.FromSeconds(10), MovedToRule.CaseLookBack);
        Assert.AreEqual(TimeSpan.FromSeconds(30), MovedToRule.LongLineAfter);
    }

    // ---- The log line

    [TestMethod]
    public void TheVerdictLineNamesTheCountsAndNoAddress()
    {
        MovedToRule rule = Left();
        Hear(rule, 0.9, -62);
        Hear(rule, 2.4, -62);
        Hear(rule, 3.8, -62);
        MovedVerdict verdict = rule.Check(At(3.8), false)!;

        Assert.AreEqual("Move: 3 messages by +3800 ms, first +900 ms, median -62 dBm, case-known 0: moved.", verdict.Line());
        LiveTests.LiveTest25Lines.AssertMatches(verdict.Line(), LiveTests.LiveTest25Lines.MovedVerdict);
    }

    [TestMethod]
    public void TheLineHasTheSameShapeForEachVerdict()
    {
        MovedToRule none = Left();
        Assert.AreEqual("Move: 0 messages by +10000 ms, case-known 0: not heard.", none.Check(At(10), false)!.Line());

        MovedToRule inCase = Left();
        Hear(inCase, 1, caseKnown: true);
        Assert.AreEqual("Move: 0 messages by +1000 ms, case-known 1: into the case.", inCase.Check(At(1), false)!.Line());

        MovedToRule back = Left();
        Hear(back, 1);
        Assert.AreEqual("Move: 1 message by +2000 ms, first +1000 ms, median -62 dBm, case-known 0: back on this PC.", back.Check(At(2), true)!.Line());
    }

    [TestMethod]
    public void TheLongLineSaysWhatThirtySecondsHeld()
    {
        MovedToRule rule = Left();
        Hear(rule, 1, -60);
        Hear(rule, 2, -60);
        _ = rule.Check(At(10), false);

        MovedSummary summary = rule.TakeLongSummary(At(30))!;

        Assert.AreEqual("Move: at +30000 ms: 2 messages, median -60 dBm, case-known 0.", MovedVerdict.LongLine(summary));
    }
}
