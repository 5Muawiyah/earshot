using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.CaseOpenFixtures;

namespace Earshot.Tests.Widget;

// CaseOpenTracker on its own: pure, the time handed in. CaseOpenRuleTests holds the burst rule and the replays of the saved
// records; WidgetStatusServiceTests and CaseOpenReplayTests cover it through the real selection code.
//
// These tests were written for the first rule (any case-known message after silence is an open, and so is any move of the
// lid byte). Each one that pinned that rule is rewritten, with a note of the record that overturned it.
[TestClass]
public sealed class CaseOpenTrackerTests
{
    private static CaseOpenTracker OpenedByLink()
    {
        var tracker = new CaseOpenTracker();
        Assert.AreEqual(CaseOpenStep.Opened, Feed(tracker, SenderA, 0, linkMade: true));
        return tracker;
    }

    // Was: "the first case-known message is an open". Overturned by the seven second shows in the evidence table, each from
    // one or two case-known messages of the linked set's own address. A link is still an open at once.
    [TestMethod]
    public void TheMessageThatMakesTheLinkIsTheFirstOpenAndOneCaseKnownMessageAloneIsNot()
    {
        OpenedByLink();

        var alone = new CaseOpenTracker();
        Assert.AreEqual(CaseOpenStep.None, Feed(alone, SenderA, 0));
        Assert.IsFalse(alone.IsOpen);
    }

    // Unchanged by the records: a pair worn (case level unknown) never opens anything, whatever the lid byte says.
    [TestMethod]
    public void WornBudsNeverOpenAnything()
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < 40; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, i % 2 == 0 ? SenderA : SenderB, i * 0.5, caseKnown: false, lid: (byte)i));
        }

        Assert.IsFalse(tracker.IsOpen);
        Assert.IsNull(tracker.CloseDueAt);
    }

    // Was: "the lid counter wrapping 255 to 0 is a change like any: a new open". Overturned by the 30 September capture:
    // both senders moved the lid byte 0x51, 0x59, 0x52, 0x5A inside one unbroken run, so a change of it is not a reopen.
    [TestMethod]
    public void TheLidByteWrappingOrMovingIsNotAnOpen()
    {
        var tracker = OpenedByLink();
        Feed(tracker, SenderA, 1, lid: 0xFF);
        Feed(tracker, SenderA, 5, lid: 0xFF);

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 6, lid: 0x00), "255 to 0.");
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 7, lid: 0x01));
        Assert.IsTrue(tracker.IsOpen);
    }

    // Was: "a counter change inside the same open window is not a second open". The answer is the same, and now holds for
    // every change at any time, not only inside two seconds.
    [TestMethod]
    public void ALidByteChangeNextToTheOpenIsNotASecondOpen()
    {
        var tracker = OpenedByLink();

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 1, lid: 0x53));
    }

    // Was: "a late second bud's counter change after a reopen is the same open, not a third". Overturned by the same capture:
    // there is no reopen to count, from the first bud or the late one. Both buds' changes, 4 s apart, open nothing.
    [TestMethod]
    public void BothBudsMovingTheLidByteAFewSecondsApartOpenNothing()
    {
        var tracker = OpenedByLink();
        Feed(tracker, SenderB, 0.25, lid: 0x51);
        Feed(tracker, SenderA, 5, lid: 0x51);
        Feed(tracker, SenderB, 5.25, lid: 0x51);

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 7, lid: 0x59));
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderB, 11, lid: 0x59), "Kept open by its own messages.");
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 15, lid: 0x52));
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderB, 18, lid: 0x52));
        Assert.IsTrue(tracker.IsOpen);
    }

    // Was: "a second bud's first counter after an open is only a baseline". There are no baselines any more; what is left to
    // say is that a bud first heard some seconds into an open changes nothing.
    [TestMethod]
    public void ASecondBudFirstHeardSecondsIntoAnOpenChangesNothing()
    {
        var tracker = OpenedByLink();
        Feed(tracker, SenderA, 5);

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderB, 7, lid: 0x77));
        Assert.AreEqual(At(7) + CaseOpenTracker.CloseAfter, tracker.CloseDueAt);
    }

    // Was: "after the close, the next case-known message opens again". Overturned by the evidence table: that message is the
    // lone pair a minute later, and it showed a card nobody asked for. A burst opens again.
    [TestMethod]
    public void SilenceForCloseAfterClosesItAndOnlyABurstOpensAgain()
    {
        var tracker = OpenedByLink();
        DateTimeOffset due = tracker.CloseDueAt!.Value;
        Assert.AreEqual(At(0) + CaseOpenTracker.CloseAfter, due);

        Assert.IsFalse(tracker.Expire(due - TimeSpan.FromMilliseconds(1)));
        Assert.IsTrue(tracker.IsOpen);
        Assert.IsTrue(tracker.Expire(due));
        Assert.IsFalse(tracker.IsOpen);
        Assert.IsFalse(tracker.Expire(due + TimeSpan.FromSeconds(1)), "Closing is reported once.");

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 10));

        CaseOpenStep last = CaseOpenStep.None;
        for (double t = 20; t <= 22.0; t += 0.25)
        {
            last = Feed(tracker, SenderA, t);
        }

        Assert.AreEqual(CaseOpenStep.Opened, last, "The last of nine messages in two seconds.");
    }

    // Was: "a message after the close time opens even when no timer ran". Same overturn: a late clock still closes the case
    // first, and a lone message after it is not an open.
    [TestMethod]
    public void AMessageAfterTheCloseTimeClosesTheCaseEvenWhenNoTimerRanAndIsNotAnOpen()
    {
        var tracker = OpenedByLink();

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 30));
        Assert.IsFalse(tracker.IsOpen, "The case closed before this message, and one message does not reopen it.");
    }

    // Was: "after a reset the next message is an open and the counter after it is inside the open window". Overturned the
    // same way: nothing opens from one message, and the reset forgets the burst under way.
    [TestMethod]
    public void ResetForgetsTheOpenAndTheBurstUnderWay()
    {
        var tracker = OpenedByLink();

        Assert.IsTrue(tracker.Reset());
        Assert.IsFalse(tracker.IsOpen);
        Assert.IsFalse(tracker.Reset(), "Nothing was open the second time.");
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 1));
        Assert.AreEqual(CaseOpenStep.Opened, Feed(tracker, SenderA, 2, linkMade: true), "A new link is an open again.");
    }
}
