using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.CaseOpenFixtures;

namespace Earshot.Tests.Widget;

// The saved records of the case-open card, replayed through WidgetStatusService on a fake clock: a link, the messages of the
// first show, eight seconds of silence, then the lone pair about a minute after the close, then the link going at two
// minutes. The first rule raised CaseOpened and CaseClosed twice for each; one show is one of each.
//
// The rows are the evidence table of scratch\one-card\evidence.md (seven links, seven second shows). Each row gives when the
// last message of the first show came (the close is eight seconds after it; for the three rows whose first close was the
// close button the card's close time stands in for the case's, since the button says nothing about the messages), the gap
// between the two closes, and what the minute held: the linked set's own messages and others'.
[TestClass]
public sealed class CaseOpenReplayTests
{
    // The record 7 pair was two messages 1.3 s apart; the others say only "2", so the same gap is used for them (design choice).
    private const double PairGap = 1.3;

    // lastOfFirstShow, gapBetweenCloses, linkedSetMessages, otherSetMessages
    [TestMethod]
    [DataRow(10.220, 55.637, 1, 1, DisplayName = "ReplayEpisode1")]
    [DataRow(2.167, 60.000, 2, 0, DisplayName = "ReplayEpisode2")]
    [DataRow(2.000, 60.002, 2, 0, DisplayName = "ReplayEpisode3")]
    [DataRow(6.230, 64.082, 2, 0, DisplayName = "ReplayEpisode4")]
    [DataRow(6.530, 60.018, 2, 0, DisplayName = "ReplayEpisode5")]
    [DataRow(4.406, 66.248, 1, 1, DisplayName = "ReplayEpisode6")]
    [DataRow(38.048, 76.255, 2, 0, DisplayName = "ReplayEpisode7")]
    public void ReplayEpisode(double lastOfFirstShow, double gapBetweenCloses, int linkedMessages, int otherMessages)
    {
        using var harness = new CaseOpenHarness();
        harness.Link();
        Assert.AreEqual(1, harness.Opened.Count, "The link is the first show.");

        harness.Steady(2.5, Math.Max(lastOfFirstShow, 2.5));
        double firstClose = Math.Max(lastOfFirstShow, 2.5) + 8;
        harness.AdvanceTo(firstClose + 0.5);
        Assert.AreEqual(1, harness.Closed.Count, "Eight seconds of silence close the first show.");

        double pairLast = Math.Max(lastOfFirstShow, 2.5) + gapBetweenCloses;
        if (otherMessages > 0)
        {
            harness.SendStrangerAt(pairLast - 0.4);
        }

        if (linkedMessages > 1)
        {
            harness.SendAt(pairLast - PairGap);
        }

        harness.SendAt(pairLast);

        harness.AdvanceTo(pairLast + 15);
        harness.AdvanceTo(pairLast + 130);

        Assert.AreEqual(1, harness.Opened.Count, "The pair a minute later is not a second show.");
        Assert.AreEqual(1, harness.Closed.Count, "And so there is no second close.");
    }

    // The capture of the lid byte moving three times in one run, through the service: one open for one run.
    [TestMethod]
    public void LidByteMovesThroughTheService()
    {
        using var harness = new CaseOpenHarness();

        foreach ((uint sender, double seconds, byte lid) in CaptureRun(startSeconds: 0))
        {
            harness.SendAt(seconds, tag: sender, lid: lid);
        }

        Assert.AreEqual(1, harness.Opened.Count, "The link, and nothing the lid byte did after it.");
        Assert.AreEqual(0, harness.Closed.Count);

        harness.Advance(10);
        Assert.AreEqual(1, harness.Closed.Count);
    }

    // The rule only counts the linked set's own messages: a stranger's burst of case-known messages beside the linked
    // set's silence opens nothing.
    [TestMethod]
    public void AnotherSetsMessagesAfterTheCloseOpenNothing()
    {
        using var harness = new CaseOpenHarness();
        harness.Link();
        harness.AdvanceTo(30);
        Assert.AreEqual(1, harness.Closed.Count);

        for (int i = 0; i < 12; i++)
        {
            harness.SendStrangerAt(40 + i * 0.25);
        }

        Assert.AreEqual(1, harness.Opened.Count);
        Assert.AreEqual(1, harness.Closed.Count);
    }
}
