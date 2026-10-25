using Earshot.App;
using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

[TestClass]
public sealed class FixOfferTrackerTests
{
    private static bool Note(FixOfferTracker t, FailureClass c, bool success = false, bool cancelled = false) => t.NoteConnect(success, c, cancelled);

    [TestMethod]
    public void PcSideDeviceSidePcSideRaisesTheOffer()
    {
        var t = new FixOfferTracker();

        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.IsFalse(Note(t, FailureClass.DeviceSide));
        Assert.IsTrue(Note(t, FailureClass.PcSide), "A failure on the AirPods' side neither counts nor resets.");
    }

    [TestMethod]
    public void UnknownFailuresNeverRaiseTheOfferHoweverMany()
    {
        // The owner's rule: the offer follows failures on this PC's side only. Unknown may be either side, so it is not counted.
        var t = new FixOfferTracker();

        for (int i = 0; i < 5; i++)
        {
            Assert.IsFalse(Note(t, FailureClass.Unknown));
        }

        Assert.AreEqual(0, t.Consecutive);
    }

    [TestMethod]
    public void TwoPcSideStillRaiseItAtTwoAndAnUnknownBetweenThemDoesNotDelayIt()
    {
        var t = new FixOfferTracker();

        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.IsTrue(Note(t, FailureClass.PcSide));

        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.IsFalse(Note(t, FailureClass.Unknown));
        Assert.IsTrue(Note(t, FailureClass.PcSide), "An Unknown between two PC-side failures neither counts nor resets.");
    }

    [TestMethod]
    public void UnknownAndTheAirPodsSideNeitherCountNorResetThePcSideCount()
    {
        var t = new FixOfferTracker();
        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.IsFalse(Note(t, FailureClass.Unknown));
        Assert.IsFalse(Note(t, FailureClass.DeviceSide));
        Assert.IsFalse(Note(t, FailureClass.Unknown));
        Assert.AreEqual(1, t.Consecutive);

        Assert.IsTrue(Note(t, FailureClass.PcSide));
    }

    [TestMethod]
    public void PcSideSuccessPcSideGivesNone()
    {
        var t = new FixOfferTracker();

        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.IsFalse(Note(t, FailureClass.None, success: true));
        Assert.IsFalse(Note(t, FailureClass.PcSide));
        Assert.AreEqual(1, t.Consecutive);
    }

    [TestMethod]
    public void TheOfferIsRaisedOnceAndTheCountStartsAgain()
    {
        var t = new FixOfferTracker();
        Note(t, FailureClass.PcSide);
        Assert.IsTrue(Note(t, FailureClass.PcSide));

        Assert.AreEqual(0, t.Consecutive);
        Assert.IsFalse(Note(t, FailureClass.PcSide), "The third in a row is the first of the next two.");
        Assert.IsTrue(Note(t, FailureClass.PcSide));
    }

    [TestMethod]
    public void ACancelledConnectNeitherCountsNorResets()
    {
        var t = new FixOfferTracker();
        Note(t, FailureClass.PcSide);

        Assert.IsFalse(Note(t, FailureClass.PcSide, cancelled: true));
        Assert.AreEqual(1, t.Consecutive);
        Assert.IsTrue(Note(t, FailureClass.PcSide));
    }

    [TestMethod]
    public void ReplayRestoresTheCountWithoutRaisingTheOffer()
    {
        DateTimeOffset now = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
        ConnectHistoryEntry Entry(string outcome, string cls) => new(now, ConnectHistoryActions.Connect, "unknown", "none", [], false, outcome, cls, 100);

        FixOfferTracker afterOne = FixOfferTracker.Replay([Entry(ConnectHistoryOutcomes.Connected, "None"), Entry(ConnectHistoryOutcomes.Failed, "PcSide")]);
        FixOfferTracker afterTwo = FixOfferTracker.Replay([Entry(ConnectHistoryOutcomes.Failed, "PcSide"), Entry(ConnectHistoryOutcomes.Failed, "PcSide")]);

        Assert.AreEqual(1, afterOne.Consecutive);
        Assert.AreEqual(0, afterTwo.Consecutive, "Two already offered once, and the count started again.");
    }

    // A pairing entry is not a connect: a run of failed pairings, even ones classed PcSide, never raises the Fix offer, and a
    // pairing between two failed connects neither adds to the count nor resets it. This pins the rule; it passed before the
    // pair action existed because the replay already counted only connect entries.
    [TestMethod]
    public void PairEntriesNeverMoveTheOfferCount()
    {
        DateTimeOffset now = new(2026, 10, 25, 20, 0, 0, TimeSpan.Zero);
        ConnectHistoryEntry Entry(string action, string outcome, string cls) => new(now, action, "none", "custom-pairing", [], false, outcome, cls, 100);
        ConnectHistoryEntry Failed(string action) => Entry(action, ConnectHistoryOutcomes.Failed, "PcSide");

        FixOfferTracker onlyPairs = FixOfferTracker.Replay([Failed(ConnectHistoryActions.Pair), Failed(ConnectHistoryActions.Pair), Failed(ConnectHistoryActions.Pair)]);
        FixOfferTracker between = FixOfferTracker.Replay(
            [Failed(ConnectHistoryActions.Connect), Entry(ConnectHistoryActions.Pair, ConnectHistoryOutcomes.Paired, "None"), Failed(ConnectHistoryActions.Pair)]);

        Assert.AreEqual(0, onlyPairs.Consecutive);
        Assert.AreEqual(1, between.Consecutive, "The one failed connect still counts; the pair entries around it do not.");
    }
}
