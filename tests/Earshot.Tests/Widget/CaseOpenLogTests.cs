using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.CaseOpenFixtures;

namespace Earshot.Tests.Widget;

// The lines the case-open rule writes: one per open, per burst that did not open, and per close; never one per message,
// never an address.
[TestClass]
public sealed class CaseOpenLogTests
{
    private static readonly Regex AddressShape = new(@"([0-9A-Fa-f]{2}[:-]){2,}[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant);

    [TestMethod]
    public void AnOpenByLinkWritesOneLineWithTheLidByte()
    {
        var tracker = new CaseOpenTracker();

        Feed(tracker, SenderA, 0, lid: 0x52, linkMade: true);

        Assert.AreEqual("Case: open by link: lid 0x52.", tracker.TakeLines().Single());
        Assert.AreEqual(0, tracker.TakeLines().Count, "Taken once.");
    }

    [TestMethod]
    public void AnOpenByBurstWritesTheCountTheSpanTheSendersTheMedianAndTheTimeSinceTheLastClose()
    {
        var tracker = new CaseOpenTracker();
        for (int i = 0; i <= 8; i++)
        {
            Feed(tracker, i % 2 == 0 ? SenderA : SenderB, i * 0.25, lid: 0x52, rssi: (sbyte)(i % 2 == 0 ? -58 : -62));
        }

        Assert.AreEqual(
            "Case: open by burst: 9 messages in 2000 ms from 2 senders, median -58 dBm, lid 0x52, no close before it.",
            tracker.TakeLines().Single());

        tracker.Expire(At(2.0) + CaseOpenTracker.CloseAfter);
        tracker.TakeLines();
        for (int i = 0; i <= 8; i++)
        {
            Feed(tracker, SenderA, 100.0 + i * 0.25);
        }

        Assert.AreEqual(
            "Case: open by burst: 9 messages in 2000 ms from 1 sender, median -60 dBm, lid 0x52, 90000 ms since the last close.",
            tracker.TakeLines().Single());
    }

    // The lone pair of the records: 2 messages 1.3 s apart, a minute after the close. Written when the window lapses, which
    // Expire (the timer) notices without another message.
    [TestMethod]
    public void ALonePairWritesNotAnOpenWhenTheWindowLapses()
    {
        var tracker = new CaseOpenTracker();
        Feed(tracker, SenderA, 0, linkMade: true);
        tracker.Expire(At(0) + CaseOpenTracker.CloseAfter);
        tracker.TakeLines();

        Feed(tracker, SenderA, 68.0, lid: 0x5A, rssi: -61);
        Feed(tracker, SenderB, 69.3, lid: 0x5A, rssi: -61);
        Assert.AreEqual(0, tracker.TakeLines().Count, "Nothing is written while the window is still open.");
        Assert.AreEqual(At(69.3) + CaseOpenTracker.OpenWindow, tracker.NextDueAt);

        Assert.IsFalse(tracker.Expire(At(74.2)));
        Assert.AreEqual(0, tracker.TakeLines().Count, "Not yet.");
        Assert.IsFalse(tracker.Expire(At(74.3)));

        Assert.AreEqual(
            "Case: not an open: 2 messages in 1300 ms from 2 senders, median -61 dBm, lid 0x5A, 60000 ms since the last close.",
            tracker.TakeLines().Single());
        Assert.IsNull(tracker.NextDueAt);
        Assert.AreEqual(0, tracker.TakeLines().Count, "Written once.");
    }

    // The next message finds the old burst lapsed too, if no timer ran.
    [TestMethod]
    public void ALapsedBurstIsWrittenByTheNextMessageWhenNoTimerRan()
    {
        var tracker = new CaseOpenTracker();
        Feed(tracker, SenderA, 10);
        Feed(tracker, SenderA, 11);

        Feed(tracker, SenderA, 500);

        Assert.AreEqual(
            "Case: not an open: 2 messages in 1000 ms from 1 sender, median -60 dBm, lid 0x52, no close before it.",
            tracker.TakeLines().Single());
    }

    [TestMethod]
    public void ACloseWritesHowLongItWasOpenHowManyMessagesAndTheLidByteFromFirstToLast()
    {
        var tracker = new CaseOpenTracker();
        Feed(tracker, SenderA, 0, lid: 0x51, linkMade: true);
        for (int i = 1; i <= 12; i++)
        {
            Feed(tracker, SenderA, i * 0.25, lid: i < 12 ? (byte)0x51 : (byte)0x5A);
        }

        tracker.TakeLines();
        Assert.IsTrue(tracker.Expire(At(3.0) + CaseOpenTracker.CloseAfter));

        Assert.AreEqual(
            "Case: closed after 8 s of silence: open for 3000 ms, 13 messages, lid 0x51 to 0x5A.",
            tracker.TakeLines().Single());
    }

    [TestMethod]
    public void NoLineHasAnAddressInIt()
    {
        var tracker = new CaseOpenTracker();
        var lines = new List<string>();
        foreach ((uint sender, double seconds, byte lid) in CaptureRun(startSeconds: 0))
        {
            Feed(tracker, sender, seconds, lid: lid);
            lines.AddRange(tracker.TakeLines());
        }

        tracker.Expire(At(100));
        lines.AddRange(tracker.TakeLines());

        Assert.AreEqual(2, lines.Count, "An open and a close for 110 messages: nothing per message.");
        foreach (string line in lines)
        {
            Assert.IsTrue(line.StartsWith("Case: ", StringComparison.Ordinal));
            Assert.IsFalse(AddressShape.IsMatch(line), line);
        }
    }

    [TestMethod]
    public void TheServiceWritesTheLinesForAShowAndALonePairInOrder()
    {
        using var harness = new CaseOpenHarness();
        harness.Link();
        harness.Steady(2.5, 3.0);
        harness.AdvanceTo(11.5);
        harness.SendAt(71.0);
        harness.SendAt(72.3);
        harness.AdvanceTo(80);

        string[] lines = harness.Log.Entries
            .Where(e => e.Level == LogLevel.Info && e.Message.StartsWith("Case: ", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToArray();

        Assert.AreEqual(3, lines.Length, string.Join(" | ", lines));
        Assert.AreEqual("Case: open by link: lid 0x52.", lines[0]);
        Assert.AreEqual("Case: closed after 8 s of silence: open for 1000 ms, 3 messages, lid 0x52 to 0x52.", lines[1]);
        Assert.AreEqual(
            "Case: not an open: 2 messages in 1300 ms from 1 sender, median -60 dBm, lid 0x52, 60000 ms since the last close.",
            lines[2]);
    }

    // A real open while linked, through the service: the link is made, the case closes, and the burst of the next open raises
    // CaseOpened at the message that makes the span two seconds, and CaseClosed after it.
    [TestMethod]
    public void ARealOpenWhileLinkedShowsAtTheBurstThroughTheService()
    {
        using var harness = new CaseOpenHarness();
        harness.Link();
        harness.AdvanceTo(12);
        Assert.AreEqual(1, harness.Closed.Count);

        for (int i = 0; i < 8; i++)
        {
            harness.SendAt(40.0 + i * 0.25);
        }

        Assert.AreEqual(1, harness.Opened.Count, "Eight messages over 1.75 s are not yet an open.");

        harness.SendAt(42.0);
        Assert.AreEqual(2, harness.Opened.Count);
        Assert.IsTrue(harness.LogHas("Case: open by burst: 9 messages in 2000 ms"));

        harness.AdvanceTo(60);
        Assert.AreEqual(2, harness.Closed.Count);
    }
}
