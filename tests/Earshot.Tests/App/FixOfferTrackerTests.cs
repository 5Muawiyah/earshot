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
}
