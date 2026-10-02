using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// CaseOpenTracker on its own: pure, the time handed in. WidgetStatusServiceTests covers it through the real selection code.
[TestClass]
public sealed class CaseOpenTrackerTests
{
    private const uint BudA = 1;
    private const uint BudB = 2;

    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void TheFirstCaseKnownMessageIsAnOpen()
    {
        var tracker = new CaseOpenTracker();

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, caseKnown: true, lidCounter: 7, At(0)));
        Assert.IsTrue(tracker.IsOpen);
        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudB, caseKnown: true, lidCounter: 7, At(0.25)), "The same open, seen from the other bud.");
    }

    // A pair worn (case level unknown) never opens anything, however many messages and whatever the counter says.
    [TestMethod]
    public void WornBudsNeverOpenAnything()
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < 40; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, tracker.Observe(i % 2 == 0 ? BudA : BudB, caseKnown: false, lidCounter: i, At(i * 0.5)));
        }

        Assert.IsFalse(tracker.IsOpen);
        Assert.IsNull(tracker.CloseDueAt);
    }

    [TestMethod]
    public void TheCounterWrappingFrom255ToZeroIsAChangeLikeAny()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 255, At(0));
        tracker.Observe(BudA, true, 255, At(1));
        tracker.Observe(BudA, true, 255, At(5));

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 0, At(6)), "255 to 0 is the lid opened again.");
    }

    [TestMethod]
    public void ACounterChangeInsideTheSameOpenWindowIsNotASecondOpen()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 10, At(0));

        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudA, true, 11, At(1)), "Inside CaseOpenTracker.SameOpenWithin of the open.");
    }

    // One reopen seen from both buds is one open, however late the second bud's message comes: a bud's own gap between
    // messages can be longer than the window that groups the two (the single-sender gap is 6.14 s in the saved records).
    [TestMethod]
    public void ALateSecondBudsCounterChangeAfterAReopenIsTheSameOpen()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 5, At(0));
        tracker.Observe(BudB, true, 5, At(0.25));
        tracker.Observe(BudA, true, 5, At(5));
        tracker.Observe(BudB, true, 5, At(5.25));

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 6, At(10)), "The lid was opened again.");
        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudB, true, 6, At(14)), "The other bud, 4 s later: the same reopen.");

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 7, At(20)), "A genuine third open still counts.");
        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudB, true, 7, At(23)));
    }

    // A bud first heard some seconds into an open carries a counter that is only its baseline.
    [TestMethod]
    public void ASecondBudsFirstCounterAfterAnOpenIsOnlyABaseline()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 5, At(0));
        tracker.Observe(BudA, true, 5, At(5));

        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudB, true, 9, At(7)), "Nothing to compare it with.");
        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudB, true, 9, At(7.5)));
    }

    [TestMethod]
    public void SilenceForCloseAfterClosesItAndTheNextCaseKnownMessageOpensAgain()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 5, At(0));
        DateTimeOffset due = tracker.CloseDueAt!.Value;
        Assert.AreEqual(At(0) + CaseOpenTracker.CloseAfter, due);

        Assert.IsFalse(tracker.Expire(due - TimeSpan.FromMilliseconds(1)));
        Assert.IsTrue(tracker.IsOpen);
        Assert.IsTrue(tracker.Expire(due));
        Assert.IsFalse(tracker.IsOpen);
        Assert.IsFalse(tracker.Expire(due + TimeSpan.FromSeconds(1)), "Closing is reported once.");

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 5, due + TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void AMessageAfterTheCloseTimeIsANewOpenEvenWhenNoTimerRan()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 5, At(0));

        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 5, At(30)), "The case closed before this message.");
    }

    [TestMethod]
    public void ResetForgetsTheOpenAndEveryBaseline()
    {
        var tracker = new CaseOpenTracker();
        tracker.Observe(BudA, true, 5, At(0));

        Assert.IsTrue(tracker.Reset());
        Assert.IsFalse(tracker.IsOpen);
        Assert.IsFalse(tracker.Reset(), "Nothing was open the second time.");
        Assert.AreEqual(CaseOpenStep.Opened, tracker.Observe(BudA, true, 6, At(1)));
        Assert.AreEqual(CaseOpenStep.None, tracker.Observe(BudA, true, 7, At(2)), "6 was the new baseline, and 7 is inside the open window.");
    }
}
