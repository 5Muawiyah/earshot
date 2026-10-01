using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.BroadcastFixtures;

namespace Earshot.Tests.Widget;

// The rule that tells one set of AirPods heard from two addresses (one per bud) from two sets, carried over from
// the rule the battery set-up used so it keeps its regression tests.
[TestClass]
public sealed class BroadcastSenderSetsTests
{
    private long _sequence;

    private SenderMessage Msg(uint tag, double seconds, ProximityMessage message, sbyte rssi = -60) =>
        new(++_sequence, tag, Start + TimeSpan.FromSeconds(seconds), rssi, message);

    private static List<BroadcastSet> Sets(params (uint Tag, List<SenderMessage> Messages)[] senders) =>
        BroadcastSenderSets.Compute(senders);

    [TestMethod]
    public void TwoBudsSpeakingTogetherAreOneSet()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)), Msg(1, 0.5, Bud(first: true)) };
        var two = new List<SenderMessage> { Msg(2, 0.2, Bud(first: false)), Msg(2, 0.7, Bud(first: false)) };

        List<BroadcastSet> sets = Sets((1, one), (2, two));

        Assert.HasCount(1, sets);
        CollectionAssert.AreEqual(new uint[] { 1, 2 }, sets[0].Tags.ToArray());
        Assert.HasCount(4, sets[0].Messages);
    }

    [TestMethod]
    public void BudsThatNeverSpeakWithinTwoSecondsOfEachOtherAreNotOneSet()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)), Msg(1, 4.002, Bud(first: true)) };
        var two = new List<SenderMessage> { Msg(2, 2.001, Bud(first: false)), Msg(2, 6.003, Bud(first: false)) };

        List<BroadcastSet> sets = Sets((1, one), (2, two));

        Assert.HasCount(2, sets);
    }

    [TestMethod]
    public void BudsTwoSecondsApartAreOneSet()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)) };
        var two = new List<SenderMessage> { Msg(2, 2.0, Bud(first: false)) };

        Assert.HasCount(1, Sets((1, one), (2, two)));
    }

    // A sender joins a set through any one member of it: the first and third never speak within two seconds of
    // each other, but each does with the second.
    [TestMethod]
    public void TheMergeIsTransitive()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)) };
        var two = new List<SenderMessage> { Msg(2, 1.5, Bud(first: false)) };
        var three = new List<SenderMessage> { Msg(3, 3.0, Bud(first: true)) };

        List<BroadcastSet> sets = Sets((1, one), (2, two), (3, three));

        Assert.HasCount(1, sets);
        Assert.HasCount(3, sets[0].Tags);
    }

    // The same bud levels heard together but another colour, another model, another case level or other bud
    // levels is another set.
    [TestMethod]
    public void ADifferentColourModelCaseLevelOrBudLevelsIsNotTheSameSet()
    {
        (string Name, ProximityMessage Other)[] cases =
        [
            ("colour", Bud(first: false, colour: WidgetFixtures.StrangerColour)),
            ("model", Bud(first: false, modelLow: WidgetFixtures.StrangerModelLow)),
            ("case level", Bud(first: false, caseNibble: 0x8)),
            ("bud levels", Bud(first: false, pairHigh: 0x7, pairLow: 0x3)),
        ];

        foreach ((string name, ProximityMessage other) in cases)
        {
            var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)) };
            var two = new List<SenderMessage> { Msg(2, 0.1, other) };

            Assert.HasCount(2, Sets((1, one), (2, two)), "A different " + name + " is a different set.");
        }
    }

    [TestMethod]
    public void OneBudAloneIsASet()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true)), Msg(1, 1, Bud(first: true)), Msg(1, 2, Bud(first: true)) };

        List<BroadcastSet> sets = Sets((1, one));

        Assert.HasCount(1, sets);
        Assert.HasCount(1, sets[0].Tags);
    }

    [TestMethod]
    public void TheMedianCoversBothBudsAndNeedsThreeMessages()
    {
        var one = new List<SenderMessage> { Msg(1, 0, Bud(first: true), -50), Msg(1, 1, Bud(first: true), -52) };
        var two = new List<SenderMessage> { Msg(2, 0.1, Bud(first: false), -70), Msg(2, 1.1, Bud(first: false), -72) };

        BroadcastSet set = Sets((1, one), (2, two)).Single();

        Assert.AreEqual(-61.0, set.MedianRssi, "The mean of the middle two of four.");

        var lone = new List<SenderMessage> { Msg(1, 0, Bud(first: true), -50), Msg(1, 1, Bud(first: true), -52) };
        Assert.IsNull(Sets((1, lone)).Single().MedianRssi, "Two messages are a passer-by, not a median.");
    }

    [TestMethod]
    public void TheMedianOfAnOddNumberIsTheMiddleOne()
    {
        var one = new List<SenderMessage>
        {
            Msg(1, 0, Bud(first: true), -80), Msg(1, 1, Bud(first: true), -50), Msg(1, 2, Bud(first: true), -60),
        };

        Assert.AreEqual(-60.0, Sets((1, one)).Single().MedianRssi);
    }
}
