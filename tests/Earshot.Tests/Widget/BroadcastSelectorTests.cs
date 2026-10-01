using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.BroadcastFixtures;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class BroadcastSelectorTests
{
    private const uint SetA = 1;
    private const uint SetAOtherBud = 2;
    private const uint SetB = 3;
    private const uint SetANewAddress = 4;
    private const uint Stranger = 5;

    // Both sets send every half second for the given time, A then B at the same instant.
    private static void RunBoth(SelectorDriver d, sbyte rssiA, sbyte rssiB, double seconds, ProximityMessage? a = null, ProximityMessage? b = null)
    {
        int steps = (int)Math.Round(seconds / 0.5);
        for (int i = 0; i <= steps; i++)
        {
            d.Send(SetA, a ?? Bud(first: true), rssiA);
            d.Send(SetB, b ?? OtherSet(), rssiB);
            if (i < steps)
            {
                d.Tick(0.5);
            }
        }
    }

    [TestMethod]
    public void WithNoPairedModelNothingIsSelected()
    {
        var d = new SelectorDriver(model: null);

        SelectionObservation seen = d.Run(SetA, Bud(first: true), -50, 10);

        Assert.AreEqual(BroadcastClass.NoPairedModel, seen.Class);
        Assert.IsFalse(d.Selector.HasChosen);
        Assert.AreEqual(BroadcastSelectionState.NoPairedModel, d.Selector.StateAt(d.Now));
    }

    [TestMethod]
    public void AnotherModelIsIgnoredAndNeverChosen()
    {
        var d = new SelectorDriver();

        SelectionObservation seen = d.Run(SetB, Bud(first: true, modelLow: WidgetFixtures.StrangerModelLow), -40, 10);

        Assert.AreEqual(BroadcastClass.ModelMismatch, seen.Class);
        Assert.IsFalse(d.Selector.HasChosen);
        Assert.AreEqual(BroadcastSelectionState.Listening, d.Selector.StateAt(d.Now));
    }

    [TestMethod]
    public void TheFirstChoiceWaitsForTwoSecondsAndThreeMessages()
    {
        var d = new SelectorDriver();

        SelectionObservation first = d.Send(SetA, Bud(first: true), -50);
        d.Tick(0.5);
        d.Send(SetA, Bud(first: true), -50);
        d.Tick(0.5);
        SelectionObservation third = d.Send(SetA, Bud(first: true), -50);

        Assert.AreEqual(BroadcastClass.Choosing, first.Class);
        Assert.AreEqual(BroadcastClass.Choosing, third.Class, "Three messages in one second: still inside the two seconds.");
        Assert.IsFalse(d.Selector.HasChosen);

        d.Tick(1.1);
        SelectionObservation chosen = d.Send(SetA, Bud(first: true), -50);

        Assert.AreEqual(BroadcastClass.Chosen, chosen.Class);
        Assert.IsTrue(chosen.NewChoice);
        Assert.IsTrue(d.Selector.HasChosen);
        Assert.AreEqual(BroadcastSelectionState.Chosen, d.Selector.StateAt(d.Now));
    }

    [TestMethod]
    public void TwoSetsInRangeTenDecibelsApartTheNearerIsChosenAndItsColourHeld()
    {
        var d = new SelectorDriver();

        RunBoth(d, rssiA: -50, rssiB: -60, seconds: 5, a: Bud(first: true, colour: 0x11), b: OtherSet(colour: 0x22));

        Assert.IsTrue(d.Selector.HasChosen);
        SelectionObservation a = d.Send(SetA, Bud(first: true, colour: 0x11), -50);
        SelectionObservation b = d.Send(SetB, OtherSet(colour: 0x22), -60);
        Assert.AreEqual(BroadcastClass.Chosen, a.Class);
        Assert.AreEqual(BroadcastClass.ColourMismatch, b.Class, "Once the colour is held a message of another colour is ignored.");
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
    }

    [TestMethod]
    public void ANearerSetAmongTwoWithTheSameColourIsChosenOverTheOtherAndTheOtherCountsAsAnotherSet()
    {
        var d = new SelectorDriver();

        RunBoth(d, rssiA: -62, rssiB: -52, seconds: 5);

        SelectionObservation a = d.Send(SetA, Bud(first: true), -62);
        SelectionObservation b = d.Send(SetB, OtherSet(), -52);
        Assert.AreEqual(BroadcastClass.Chosen, b.Class);
        Assert.AreEqual(BroadcastClass.OtherSet, a.Class);
        Assert.AreEqual(2, b.SetsInRange);
    }

    [TestMethod]
    public void OneSetFromTwoAddressesIsOneSetAndBothAreChosen()
    {
        var d = new SelectorDriver();

        for (int i = 0; i < 12; i++)
        {
            d.Send(SetA, Bud(first: true), -58);
            d.Tick(0.2);
            d.Send(SetAOtherBud, Bud(first: false), -56);
            d.Tick(0.3);
        }

        SelectionObservation first = d.Send(SetA, Bud(first: true), -58);
        SelectionObservation second = d.Send(SetAOtherBud, Bud(first: false), -56);

        Assert.AreEqual(BroadcastClass.Chosen, first.Class);
        Assert.AreEqual(BroadcastClass.Chosen, second.Class);
        Assert.AreEqual(1, second.SetsInRange);
    }

    // A challenger nearer than the chosen set by less than the margin never takes over, however long it stays so.
    [TestMethod]
    public void AChallengerSixDecibelsNearerForAMinuteDoesNotSwitch()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        for (int i = 0; i <= 120; i++)
        {
            d.Send(SetA, Bud(first: true), -60);
            d.Send(SetB, OtherSet(), -54);
            d.Tick(0.5);
        }

        SelectionObservation a = d.Send(SetA, Bud(first: true), -60);
        Assert.AreEqual(BroadcastClass.Chosen, a.Class);
        Assert.AreEqual(2, a.SetsInRange, "Sanity: both sets are in range.");
    }

    [TestMethod]
    public void AChallengerNineDecibelsNearerForTwentyNineSecondsDoesNotSwitch()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        bool switched = false;
        for (int i = 0; i <= 58; i++)
        {
            switched |= d.Send(SetA, Bud(first: true), -60).Switched;
            switched |= d.Send(SetB, OtherSet(), -51).Switched;
            if (i < 58)
            {
                d.Tick(0.5);
            }
        }

        Assert.IsFalse(switched);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, Bud(first: true), -60).Class);
    }

    [TestMethod]
    public void AChallengerNineDecibelsNearerForThirtyOneSecondsSwitchesAndKeepsTheColour()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true, colour: 0x11), -60, 3);

        bool switched = false;
        bool newChoice = false;
        for (int i = 0; i <= 62; i++)
        {
            SelectionObservation fromA = d.Send(SetA, Bud(first: true, colour: 0x11), -60);
            SelectionObservation fromB = d.Send(SetB, OtherSet(colour: 0x11), -51);
            switched |= fromA.Switched || fromB.Switched;
            newChoice |= fromA.NewChoice || fromB.NewChoice;
            if (i < 62)
            {
                d.Tick(0.5);
            }
        }

        Assert.IsTrue(switched, "Nine decibels nearer for thirty seconds.");
        Assert.IsTrue(newChoice, "A switch drops the old set's values.");
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetB, OtherSet(colour: 0x11), -51).Class);
        Assert.AreEqual(BroadcastClass.OtherSet, d.Send(SetA, Bud(first: true, colour: 0x11), -60).Class);
    }

    [TestMethod]
    public void AGapInTheChallengersMessagesResetsTheThirtySeconds()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        bool switched = false;
        void Both(int seconds)
        {
            for (int i = 0; i <= seconds * 2; i++)
            {
                switched |= d.Send(SetA, Bud(first: true), -60).Switched;
                switched |= d.Send(SetB, OtherSet(), -51).Switched;
                d.Tick(0.5);
            }
        }

        Both(20);
        for (int i = 0; i < 24; i++)
        {
            d.Send(SetA, Bud(first: true), -60); // eleven and more seconds with nothing from the challenger
            d.Tick(0.5);
        }

        Both(20);

        Assert.IsFalse(switched, "Twenty seconds, a gap, twenty seconds is never thirty without a break.");
    }

    // A chosen set that sends too seldom to have a median in the window (it sends once every eight seconds, so the
    // window holds one or two of its messages) is compared with its last median.
    [TestMethod]
    public void WhileTheChosenSetIsTooSeldomHeardForAMedianItsLastMedianIsHeldForTheComparison()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        d.Tick(0.5);

        bool nearSwitched = false;
        for (int i = 0; i < 80; i++)
        {
            if (i % 16 == 8)
            {
                d.Send(SetA, Bud(first: true), -60);
            }

            nearSwitched |= d.Send(SetB, OtherSet(), -55).Switched; // 5 dB over the held median: under the margin
            d.Tick(0.5);
        }

        Assert.IsFalse(nearSwitched);

        bool switched = false;
        for (int i = 0; i < 80; i++)
        {
            if (i % 16 == 8)
            {
                d.Send(SetA, Bud(first: true), -60);
            }

            switched |= d.Send(SetB, OtherSet(), -50).Switched; // 10 dB over the held median, for forty seconds
            d.Tick(0.5);
        }

        Assert.IsTrue(switched);
    }

    // While any chosen sender has sent inside the window a different set is never adopted as the chosen set's
    // continuation, however equal its fields: only the margin and the hold can make it the chosen one.
    [TestMethod]
    public void EqualFieldsFromANewAddressWhileTheChosenSetSentInsideTheWindowAreNotItsContinuation()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        d.Tick(3.0);

        SelectionObservation seen = d.Send(SetANewAddress, Bud(first: true), -60);

        Assert.AreEqual(BroadcastClass.OtherSet, seen.Class);
        Assert.IsFalse(seen.NewChoice);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, Bud(first: true), -60).Class, "The chosen set still sends and is still chosen.");
    }

    [TestMethod]
    public void ANewAddressWithDifferentFieldsIsANewSetHeldToTheStickinessRule()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        d.Tick(3.0);

        SelectionObservation seen = d.Send(SetANewAddress, Bud(first: true, pairHigh: 0x6, pairLow: 0x4), -60);

        Assert.AreEqual(BroadcastClass.OtherSet, seen.Class);
        Assert.IsFalse(seen.NewChoice);
    }

    // A sender that said exactly what the chosen set said, once, far away, and then said something else is not part
    // of the chosen set for good: each evaluation the chosen tags are those of the set the chosen sender is in.
    [TestMethod]
    public void AFarStrangerThatMatchedTheChosenSetOnceAndThenDiffersIsAnotherSet()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -50, 3);
        d.Tick(0.5);
        d.Send(Stranger, Bud(first: true), -85);

        SelectionObservation owner = default;
        SelectionObservation stranger = default;
        for (int i = 0; i < 30; i++)
        {
            d.Tick(0.5);
            owner = d.Send(SetA, Bud(first: true), -50);
            stranger = d.Send(Stranger, OtherSet(), -85);
        }

        Assert.AreEqual(BroadcastClass.Chosen, owner.Class);
        Assert.AreEqual(BroadcastClass.OtherSet, stranger.Class, "Fifteen seconds on, the stranger's own fields make it another set.");
        Assert.AreEqual(2, stranger.SetsInRange);
        Assert.IsFalse(stranger.NewChoice);
    }

    [TestMethod]
    public void AStrangerThatSplitsOffIsHeldToTheMarginAndTheHoldLikeAnyOtherSet()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -70, 3);
        d.Tick(0.5);
        d.Send(Stranger, Bud(first: true), -50);

        bool switched = false;
        for (int i = 0; i < 40; i++)
        {
            d.Tick(0.5);
            switched |= d.Send(SetA, Bud(first: true), -70).Switched;
            switched |= d.Send(Stranger, OtherSet(), -50).Switched;
        }

        Assert.IsFalse(switched, "Twenty seconds is not the thirty it takes, split off or not.");
        for (int i = 0; i < 70; i++)
        {
            d.Tick(0.5);
            switched |= d.Send(SetA, Bud(first: true), -70).Switched;
            switched |= d.Send(Stranger, OtherSet(), -50).Switched;
        }

        Assert.IsTrue(switched, "Twenty decibels nearer for thirty seconds after it split off takes over.");
    }

    // The case closed, or the addresses rotated, and more than the window passed: the selection is made again by
    // the first-choice rule, however long the new address has been going.
    [TestMethod]
    public void ANewAddressMoreThanTheWindowAfterTheChosenSetWentQuietIsChosenAgainAfterTwoSecondsAndThreeMessages()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        d.Tick(11);

        SelectionObservation first = d.Send(SetANewAddress, Bud(first: true), -60);
        d.Tick(0.5);
        SelectionObservation second = d.Send(SetANewAddress, Bud(first: true), -60);
        d.Tick(0.5);
        SelectionObservation third = d.Send(SetANewAddress, Bud(first: true), -60);

        Assert.AreEqual(BroadcastClass.Choosing, first.Class);
        Assert.AreEqual(BroadcastClass.Choosing, second.Class);
        Assert.AreEqual(BroadcastClass.Choosing, third.Class, "A second in: the two seconds have not passed.");

        d.Tick(1.1);
        SelectionObservation chosen = d.Send(SetANewAddress, Bud(first: true), -60);

        Assert.AreEqual(BroadcastClass.Chosen, chosen.Class);
        Assert.IsTrue(d.Selector.HasChosen);
    }

    [TestMethod]
    public void ACaseClosedForFiveMinutesAndOpenedUnderANewAddressIsNotShutOutForAnHour()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true, colour: 0x11), -60, 3);
        d.Tick(300);

        BroadcastClass last = BroadcastClass.OtherSet;
        for (int i = 0; i < 20; i++)
        {
            last = d.Send(SetANewAddress, Bud(first: true, colour: 0x11, pairHigh: 0x6, pairLow: 0x4), -60).Class;
            d.Tick(0.5);
        }

        Assert.AreEqual(BroadcastClass.Chosen, last, "Ten seconds after it opened, the new address is the chosen set.");
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
        Assert.AreEqual(BroadcastClass.ColourMismatch, d.Send(SetB, OtherSet(colour: 0x22), -30).Class, "The colour stays held across the gap.");
    }

    [TestMethod]
    public void WhenTheSelectionIsMadeAgainTheNearestSetIsChosenAndTheValuesAreNotMarkedAsANewPair()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        d.Tick(11);

        SelectionObservation seen = default;
        for (int i = 0; i < 8; i++)
        {
            d.Send(SetANewAddress, Bud(first: true), -72);
            seen = d.Send(SetB, OtherSet(), -48);
            d.Tick(0.5);
        }

        Assert.AreEqual(BroadcastClass.Chosen, seen.Class, "The nearer of the two is chosen.");
        Assert.IsFalse(seen.NewChoice, "The values on show are kept, greyed as they age, until the new set's own replace them.");
        Assert.AreEqual(BroadcastClass.OtherSet, d.Send(SetANewAddress, Bud(first: true), -72).Class);
    }

    [TestMethod]
    public void TheTwoSecondsRestartWhenTheModelComesBackAfterEveryoneWentQuiet()
    {
        var d = new SelectorDriver();
        d.Send(SetA, Bud(first: true), -60);
        d.Tick(60);

        SelectionObservation first = d.Send(SetA, Bud(first: true), -60);
        d.Tick(0.4);
        d.Send(SetA, Bud(first: true), -60);
        d.Tick(0.4);
        SelectionObservation third = d.Send(SetA, Bud(first: true), -60);

        Assert.AreEqual(BroadcastClass.Choosing, first.Class);
        Assert.AreEqual(BroadcastClass.Choosing, third.Class, "Heard for less than two seconds since it came back, whatever it did a minute ago.");
        Assert.IsFalse(d.Selector.HasChosen);
    }

    // Three messages six seconds apart are a median over the ten second window; five seconds would hold two of
    // them, and twenty would hold the third message twelve seconds apart.
    [TestMethod]
    public void TheWindowIsTenSecondsAndNeitherLongerNorShorterGivesTheSameFirstChoice()
    {
        var spread = new SelectorDriver();
        spread.Send(SetA, Bud(first: true), -60);
        spread.Tick(3);
        spread.Send(SetA, Bud(first: true), -60);
        spread.Tick(3);
        SelectionObservation inside = spread.Send(SetA, Bud(first: true), -60);
        Assert.AreEqual(BroadcastClass.Chosen, inside.Class, "Three messages inside six seconds are a median.");

        var wide = new SelectorDriver();
        wide.Send(SetA, Bud(first: true), -60);
        wide.Tick(6);
        wide.Send(SetA, Bud(first: true), -60);
        wide.Tick(6);
        SelectionObservation outside = wide.Send(SetA, Bud(first: true), -60);
        Assert.AreEqual(BroadcastClass.Choosing, outside.Class, "The first of three messages twelve seconds apart is out of the window.");
    }

    // The challenger's own median is -52.5 (messages alternate between -52 and -53), 7.5 dB over the chosen set.
    [TestMethod]
    public void AChallengerSevenAndAHalfDecibelsNearerForAMinuteDoesNotSwitch()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        bool switched = false;
        for (int i = 0; i <= 120; i++)
        {
            switched |= d.Send(SetA, Bud(first: true), -60).Switched;
            switched |= d.Send(SetB, OtherSet(), (sbyte)(i % 2 == 0 ? -52 : -53)).Switched;
            d.Tick(0.5);
        }

        Assert.IsFalse(switched, "Seven and a half decibels is under the eight it takes.");
    }

    [TestMethod]
    public void AChallengerExactlyEightDecibelsNearerForThirtySecondsSwitches()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        bool switched = false;
        for (int i = 0; i <= 70; i++)
        {
            switched |= d.Send(SetA, Bud(first: true), -60).Switched;
            switched |= d.Send(SetB, OtherSet(), -52).Switched;
            d.Tick(0.5);
        }

        Assert.IsTrue(switched, "Eight decibels is the margin: at least that, for thirty seconds.");
    }

    [TestMethod]
    public void TheChosenSetAndTheHeldColourAreReleasedAfterAnHourOfSilence()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true, colour: 0x11), -60, 3);
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
        DateTimeOffset lastHeard = d.Now;

        d.Tick(3601);
        Assert.AreEqual(BroadcastSelectionState.Listening, d.Selector.StateAt(d.Now), "An hour and a second on, nothing is chosen.");
        Assert.IsTrue(d.Now - lastHeard > TimeSpan.FromHours(1));

        // Another colour is heard now: nothing locks the first pair's colour in.
        SelectionObservation first = d.Send(SetB, OtherSet(colour: 0x22), -50);
        Assert.AreEqual(BroadcastClass.Choosing, first.Class);
        d.Run(SetB, OtherSet(colour: 0x22), -50, 3);
        Assert.AreEqual((byte)0x22, d.Selector.HeldColour);
        Assert.IsTrue(d.Selector.HasChosen);
    }

    [TestMethod]
    public void TheChosenSetIsKeptWhileItIsHeardWithinTheHour()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);

        d.Tick(3599);

        Assert.AreEqual(BroadcastSelectionState.Chosen, d.Selector.StateAt(d.Now));
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, Bud(first: true), -60).Class);
    }

    [TestMethod]
    public void ANewPairedModelStartsTheSelectionOver()
    {
        var d = new SelectorDriver();
        d.Run(SetA, Bud(first: true), -60, 3);
        Assert.IsTrue(d.Selector.HasChosen);

        Assert.IsFalse(d.Selector.SetPairedModel(BroadcastFixtures.PairedModel), "The same model changes nothing.");
        Assert.IsTrue(d.Selector.HasChosen);
        Assert.IsTrue(d.Selector.SetPairedModel(0x1234));

        Assert.IsFalse(d.Selector.HasChosen);
        Assert.IsNull(d.Selector.HeldColour);
    }

    [TestMethod]
    public void TheOtherBudsLatestMessageIsAvailableForTheBudOrderCheck()
    {
        var d = new SelectorDriver();
        for (int i = 0; i < 12; i++)
        {
            d.Send(SetA, Bud(first: true), -58);
            d.Tick(0.2);
            d.Send(SetAOtherBud, Bud(first: false), -56);
            d.Tick(0.3);
        }

        d.Send(SetA, Bud(first: true), -58);

        IReadOnlyList<SenderMessage> others = d.Selector.OtherChosenSenders(SetA, d.Now, TimeSpan.FromSeconds(2));

        Assert.HasCount(1, others);
        Assert.AreEqual(SetAOtherBud, others[0].Tag);
        Assert.IsEmpty(d.Selector.OtherChosenSenders(SetA, d.Now + TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)));
    }
}
