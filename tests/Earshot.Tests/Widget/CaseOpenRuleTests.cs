using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.CaseOpenFixtures;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// The case-open rule of CaseOpenTracker on its own, pure, the time handed in. An open is the message that makes a link, or
// a burst of case-known messages (five inside five seconds, the first and last two seconds apart), and nothing else. The
// saved records that overturned the old rule (any case-known message after silence, and any move of the lid byte) are the
// evidence table in scratch\one-card\evidence.md: seven links, each followed by a lone pair of one or two messages about a
// minute after the first close, and the 30 September capture, where both senders moved the lid byte three times inside one
// open.
[TestClass]
public sealed class CaseOpenRuleTests
{
    // A burst, the close, then the lone pair the records show: one or two messages from the linked set's own address,
    // about a minute after the close. Not an open.
    [TestMethod]
    public void LonePairAfterClose()
    {
        var tracker = new CaseOpenTracker();
        CaseOpenStep opens = CaseOpenStep.None;
        for (double t = 0; t <= 3.0; t += 0.25)
        {
            if (Feed(tracker, SenderA, t) == CaseOpenStep.Opened)
            {
                opens = CaseOpenStep.Opened;
            }
        }

        Assert.AreEqual(CaseOpenStep.Opened, opens, "The burst is an open.");
        Assert.IsTrue(tracker.Expire(At(3.0) + CaseOpenTracker.CloseAfter), "Eight seconds of silence close it.");

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 71.0), "The first of the pair.");
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 72.3), "The second, 1.3 s later (record 7).");
        Assert.IsFalse(tracker.IsOpen);
        Assert.IsNull(tracker.CloseDueAt);
    }

    // Every pair of the records is one message or two, however far apart, from either sender.
    [TestMethod]
    [DataRow(1, 0.0)]
    [DataRow(2, 0.2)]
    [DataRow(2, 1.3)]
    [DataRow(2, 4.9)]
    public void NoLonePairOpensAnythingWhenClosed(int messages, double apart)
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < messages; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, i % 2 == 0 ? SenderA : SenderB, 100.0 + i * apart));
        }

        Assert.IsFalse(tracker.IsOpen);
    }

    // The lid byte moved three times inside one open in the capture, from both senders. It opens nothing.
    [TestMethod]
    public void LidByteMoves()
    {
        var tracker = new CaseOpenTracker();
        int opens = 0;
        int messages = 0;

        foreach ((uint sender, double seconds, byte lid) in CaptureRun(startSeconds: 100.0))
        {
            messages++;
            if (Feed(tracker, sender, seconds, lid: lid) == CaseOpenStep.Opened)
            {
                opens++;
            }
        }

        Assert.AreEqual(110, messages);
        Assert.AreEqual(1, opens, "One open for one run of messages, whatever the lid byte did.");
        Assert.IsTrue(tracker.IsOpen);
    }

    // A real open while linked: the link is gone from the case, 30 s of silence, then the case-open rate (four a second).
    // The open comes at the message that makes the span two seconds, and only once.
    [TestMethod]
    public void RealOpenWhileLinked()
    {
        var tracker = new CaseOpenTracker();
        Assert.AreEqual(CaseOpenStep.Opened, Feed(tracker, SenderA, 0, linkMade: true));
        Assert.IsTrue(tracker.Expire(At(0) + CaseOpenTracker.CloseAfter));

        var opensAt = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            double t = 38.0 + i * 0.25;
            if (Feed(tracker, i % 2 == 0 ? SenderA : SenderB, t) == CaseOpenStep.Opened)
            {
                opensAt.Add(t);
            }
        }

        Assert.AreEqual(1, opensAt.Count);
        Assert.AreEqual(40.0, opensAt[0], 1e-9, "The ninth message, two seconds after the first.");
    }

    // Four messages and then silence: below the count, however close together.
    [TestMethod]
    public void FourThenSilence()
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, i * 0.7));
        }

        Assert.IsFalse(tracker.Expire(At(60)));
        Assert.IsFalse(tracker.IsOpen);
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 60), "And one more a minute later starts from nothing.");
    }

    // Many messages in one second are still not a burst of two seconds.
    [TestMethod]
    public void ManyMessagesInOneSecondAreTooShort()
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < 12; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, i * 0.08));
        }
    }

    // One message every 1.5 s never has five inside five seconds.
    [TestMethod]
    public void ASlowDripNeverOpens()
    {
        var tracker = new CaseOpenTracker();

        for (int i = 0; i < 60; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, i * 1.5));
        }
    }

    // The two buds of a pair are counted together.
    [TestMethod]
    public void TwoSendersMerged()
    {
        var tracker = new CaseOpenTracker();

        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 0));
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderB, 0.5));
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 1.0));
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderB, 1.5));
        Assert.AreEqual(CaseOpenStep.Opened, Feed(tracker, SenderA, 2.0), "Five messages over two seconds, from two senders.");
    }

    // A case-unknown message (buds in use) never counts, and does not break a burst either.
    [TestMethod]
    public void CaseUnknownNeverCounts()
    {
        var tracker = new CaseOpenTracker();
        for (int i = 0; i < 60; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, i % 2 == 0 ? SenderA : SenderB, i * 0.25, caseKnown: false));
        }

        Assert.IsFalse(tracker.IsOpen);

        var mixed = new CaseOpenTracker();
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(mixed, SenderA, i * 0.6));
            Assert.AreEqual(CaseOpenStep.None, Feed(mixed, SenderB, i * 0.6 + 0.3, caseKnown: false));
        }

        Assert.AreEqual(CaseOpenStep.Opened, Feed(mixed, SenderA, 2.4), "The fifth case-known message, 2.4 s after the first.");
    }

    [TestMethod]
    public void LinkMadeOpensAtOnce()
    {
        var tracker = new CaseOpenTracker();

        Assert.AreEqual(CaseOpenStep.Opened, Feed(tracker, SenderA, 0, linkMade: true));
        Assert.IsTrue(tracker.IsOpen);
        Assert.AreEqual(At(0) + CaseOpenTracker.CloseAfter, tracker.CloseDueAt);
    }

    // Messages of an open case keep it open, one a slow sender gives every 6 s included, and open nothing more.
    [TestMethod]
    public void AnOpenCaseStaysOpenOnItsOwnSlowMessagesAndNothingReopens()
    {
        var tracker = new CaseOpenTracker();
        Feed(tracker, SenderA, 0, linkMade: true);

        for (int i = 1; i <= 6; i++)
        {
            Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, i * 6.14));
        }

        Assert.IsTrue(tracker.IsOpen);
    }

    [TestMethod]
    public void ResetClears()
    {
        var tracker = new CaseOpenTracker();
        for (int i = 0; i < 4; i++)
        {
            Feed(tracker, SenderA, i * 0.5);
        }

        Assert.IsFalse(tracker.Reset(), "Nothing was open.");
        Assert.AreEqual(CaseOpenStep.None, Feed(tracker, SenderA, 2.0), "The four before the reset do not count towards the fifth.");

        var open = new CaseOpenTracker();
        Feed(open, SenderA, 0, linkMade: true);
        Assert.IsTrue(open.Reset());
        Assert.IsFalse(open.IsOpen);
        Assert.IsNull(open.CloseDueAt);
    }
}
