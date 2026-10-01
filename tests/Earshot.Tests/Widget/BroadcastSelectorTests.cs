using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.BroadcastFixtures;

namespace Earshot.Tests.Widget;

// The owner's set is linked when the owner opens the case next to the PC: a message of the paired model that carries a
// case level (a bud in the case, the lid open), the nearest such set, above the near-PC level, sustained for several
// messages. Nothing else makes a set the owner's. After that the link follows the set across address changes and is
// dropped when the set has been lost for too long.
[TestClass]
public sealed class BroadcastSelectorTests
{
    private const uint SetA = 1;
    private const uint SetAOtherBud = 2;
    private const uint SetB = 3;
    private const uint SetBOtherBud = 6;
    private const uint SetANewAddress = 4;
    private const uint SetANewOtherBud = 7;
    private const uint Stranger = 5;

    // A bud's message with the case level unknown: the buds are out of the case, the way a pair is when it is in use.
    private static ProximityMessage InUse(bool first, byte colour = WidgetFixtures.Colour, int pairHigh = 0x7, int pairLow = 0x4) =>
        Bud(first, colour, caseNibble: 0xF, pairHigh, pairLow);

    // The owner opens the case: both buds send about four times a second between them, with the case level known.
    // Returns the last observation.
    private static SelectionObservation Burst(
        SelectorDriver d, sbyte rssi, double seconds, uint first = SetA, uint second = SetAOtherBud,
        byte colour = WidgetFixtures.Colour, byte caseNibble = 0x9, int pairHigh = 0x7, int pairLow = 0x4)
    {
        SelectionObservation last = default;
        int steps = (int)Math.Round(seconds / 0.25);
        for (int i = 0; i <= steps; i++)
        {
            bool one = i % 2 == 0;
            last = d.Send(one ? first : second, Bud(one, colour, caseNibble, pairHigh, pairLow), rssi);
            if (i < steps)
            {
                d.Tick(0.25);
            }
        }

        return last;
    }

    // A set of the paired model that is not the owner's, with other levels, opening its case.
    private static SelectionObservation StrangerBurst(SelectorDriver d, sbyte rssi, double seconds, byte colour = WidgetFixtures.Colour) =>
        Burst(d, rssi, seconds, SetB, SetBOtherBud, colour, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1);

    // The owner's pair, linked by a three second burst at a strong signal.
    private static SelectorDriver Linked(byte colour = WidgetFixtures.Colour, sbyte rssi = -55)
    {
        var d = new SelectorDriver();
        Burst(d, rssi, 3, colour: colour);
        Assert.IsTrue(d.Selector.IsLinked, "Sanity: the burst linked the pair.");
        d.Tick(0.25);
        return d;
    }

    // The linked pair in use: both buds out of the case, a message from each every second for the given time.
    private static SelectionObservation InUseRun(
        SelectorDriver d, double seconds, uint first = SetA, uint second = SetAOtherBud, sbyte rssi = -55, byte colour = WidgetFixtures.Colour)
    {
        SelectionObservation last = default;
        int steps = (int)Math.Round(seconds);
        for (int i = 0; i < steps; i++)
        {
            last = d.Send(first, InUse(true, colour), rssi);
            d.Tick(0.5);
            last = d.Send(second, InUse(false, colour), rssi);
            d.Tick(0.5);
        }

        return last;
    }

    // ---- The paired model

    [TestMethod]
    public void WithNoPairedModelNothingIsLinked()
    {
        var d = new SelectorDriver(model: null);

        SelectionObservation seen = Burst(d, -50, 5);

        Assert.AreEqual(BroadcastClass.NoPairedModel, seen.Class);
        Assert.IsFalse(d.Selector.IsLinked);
        Assert.AreEqual(BroadcastSelectionState.NoPairedModel, d.Selector.StateAt(d.Now));
    }

    [TestMethod]
    public void AnotherModelOpeningItsCaseIsIgnoredAndNeverLinked()
    {
        var d = new SelectorDriver();
        SelectionObservation seen = default;
        for (int i = 0; i < 40; i++)
        {
            seen = d.Send(SetB, Bud(first: true, modelLow: WidgetFixtures.StrangerModelLow), -40);
            d.Tick(0.25);
        }

        Assert.AreEqual(BroadcastClass.ModelMismatch, seen.Class);
        Assert.IsFalse(d.Selector.IsLinked);
        Assert.AreEqual(BroadcastSelectionState.Listening, d.Selector.StateAt(d.Now));
    }

    // ---- Linking on a case open

    // The scene that was shown as the owner's: the owner's pair silent in its closed case, a stranger's pair of the same
    // model worn nearby, its case level unknown, at -64 to -76.
    [TestMethod]
    public void AStrangersPairWornNearbyWithTheCaseUnknownIsNeverLinkedHoweverLongAndHoweverNear()
    {
        var d = new SelectorDriver();
        SelectionObservation seen = default;
        for (int i = 0; i < 600; i++)
        {
            sbyte rssi = (sbyte)(-64 - ((i * 7) % 13)); // -64 to -76
            seen = d.Send(i % 2 == 0 ? SetB : SetBOtherBud, InUse(i % 2 == 0, pairHigh: 0x7, pairLow: 0x7), rssi);
            d.Tick(0.5);
        }

        Assert.AreEqual(BroadcastClass.NotLinked, seen.Class, "Heard, and never the owner's.");
        Assert.IsFalse(d.Selector.IsLinked, "Five minutes of a worn pair nearby: nothing is linked.");
        Assert.AreEqual(BroadcastSelectionState.Listening, d.Selector.StateAt(d.Now));

        // Even a stronger signal does not make it the owner's.
        for (int i = 0; i < 100; i++)
        {
            seen = d.Send(SetB, InUse(true, pairHigh: 0x7, pairLow: 0x7), -40);
            d.Tick(0.25);
        }

        Assert.IsFalse(d.Selector.IsLinked);
    }

    [TestMethod]
    public void ACaseOpenBurstOfThePairedModelLinksAfterTwoSecondsAndFiveMessagesAndHoldsItsColour()
    {
        var d = new SelectorDriver();

        SelectionObservation early = Burst(d, -55, 1.75, colour: 0x11);
        Assert.AreEqual(BroadcastClass.NotLinked, early.Class, "Eight messages in a second and three quarters: not yet sustained.");
        Assert.IsFalse(d.Selector.IsLinked);

        d.Tick(0.25);
        SelectionObservation linked = d.Send(SetA, Bud(first: true, colour: 0x11), -55);

        Assert.IsTrue(linked.NewChoice, "A link was made: whatever was held before belonged to another pair.");
        Assert.IsTrue(d.Selector.IsLinked);
        Assert.AreEqual(BroadcastSelectionState.Linked, d.Selector.StateAt(d.Now));
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
        Assert.AreEqual(BroadcastClass.Chosen, linked.Class);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetAOtherBud, Bud(first: false, colour: 0x11), -55).Class, "Both buds are the linked set.");
    }

    [TestMethod]
    public void ABurstWhoseMedianIsBelowTheNearPcLevelNeverLinksAndOneAtItDoes()
    {
        var far = new SelectorDriver();
        Burst(far, -71, 20);
        Assert.IsFalse(far.Selector.IsLinked, "-71 dBm is not next to the PC.");

        var near = new SelectorDriver();
        Burst(near, -70, 3);
        Assert.IsTrue(near.Selector.IsLinked, "-70 dBm is.");
    }

    [TestMethod]
    public void TheNearPcLevelIsTheMedianNotAnySingleMessage()
    {
        var d = new SelectorDriver();
        for (int i = 0; i <= 24; i++)
        {
            // Strong now and then, weak otherwise: the median is -80.
            sbyte rssi = (sbyte)(i % 4 == 0 ? -40 : -80);
            d.Send(i % 2 == 0 ? SetA : SetAOtherBud, Bud(i % 2 == 0), rssi);
            d.Tick(0.25);
        }

        Assert.IsFalse(d.Selector.IsLinked);
    }

    [TestMethod]
    public void MessagesTooSeldomToBeFiveInFiveSecondsNeverLink()
    {
        var d = new SelectorDriver();
        for (int i = 0; i < 40; i++)
        {
            d.Send(i % 2 == 0 ? SetA : SetAOtherBud, Bud(i % 2 == 0), -50);
            d.Tick(1.3); // four in five seconds
        }

        Assert.IsFalse(d.Selector.IsLinked, "A case-known message every second and a third is not a burst.");
    }

    [TestMethod]
    public void AMessageWithTheCaseLevelUnknownOrOutOfRangeNeverCountsTowardsALink()
    {
        var d = new SelectorDriver();
        for (int i = 0; i < 40; i++)
        {
            byte nibble = (byte)(i % 2 == 0 ? 0xF : 0xC); // unknown, and a value neither source describes
            d.Send(SetA, Bud(first: true, caseNibble: nibble), -45);
            d.Tick(0.25);
        }

        Assert.IsFalse(d.Selector.IsLinked);
    }

    [TestMethod]
    public void WhenTwoSetsOpenTheirCasesTheStrongestIsLinkedWhicheverItIs()
    {
        var owner = new SelectorDriver();
        for (int i = 0; i <= 12; i++)
        {
            owner.Send(SetA, Bud(first: true), -52);
            owner.Send(SetAOtherBud, Bud(first: false), -52);
            owner.Send(SetB, OtherSet(), -62);
            owner.Send(SetBOtherBud, Bud(first: false, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1), -62);
            owner.Tick(0.25);
        }

        Assert.AreEqual(BroadcastClass.Chosen, owner.Send(SetA, Bud(first: true), -52).Class);
        Assert.AreEqual(BroadcastClass.OtherSet, owner.Send(SetB, OtherSet(), -62).Class, "The other pair, ten decibels weaker, is another set.");

        // The residual risk, stated as it behaves: a same-model pair that opens its case more strongly than the owner's
        // is the one linked.
        var stranger = new SelectorDriver();
        for (int i = 0; i <= 12; i++)
        {
            stranger.Send(SetA, Bud(first: true), -62);
            stranger.Send(SetAOtherBud, Bud(first: false), -62);
            stranger.Send(SetB, OtherSet(), -52);
            stranger.Send(SetBOtherBud, Bud(first: false, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1), -52);
            stranger.Tick(0.25);
        }

        Assert.AreEqual(BroadcastClass.Chosen, stranger.Send(SetB, OtherSet(), -52).Class);
    }

    [TestMethod]
    public void AColourOtherThanTheLinkedSetsIsIgnoredWhileLinked()
    {
        SelectorDriver d = Linked(colour: 0x11);

        SelectionObservation seen = StrangerBurst(d, -40, 5, colour: 0x22);

        Assert.AreEqual(BroadcastClass.ColourMismatch, seen.Class);
        Assert.AreEqual((byte)0x11, d.Selector.HeldColour);
    }

    // ---- Following the linked set

    // Addresses rotate. The linked pair goes quiet under its old addresses and its in-use messages come from new ones,
    // with the fields it last said: the set is followed, and what was shown is not another pair's.
    [TestMethod]
    public void TheLinkedSetRotatingItsAddressesIsFollowedAndItsValuesAreKept()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 10);

        d.Tick(1.0);
        bool followed = false;
        bool newChoice = false;
        BroadcastClass last = BroadcastClass.OtherSet;
        for (int i = 0; i < 40; i++)
        {
            SelectionObservation a = d.Send(SetANewAddress, InUse(true), -55);
            d.Tick(0.5);
            SelectionObservation b = d.Send(SetANewOtherBud, InUse(false), -55);
            d.Tick(0.5);
            followed |= a.Followed || b.Followed;
            newChoice |= a.NewChoice || b.NewChoice;
            last = b.Class;
        }

        Assert.IsTrue(followed, "Fields that continue the linked set's last: it is the same set under new addresses.");
        Assert.IsFalse(newChoice, "A continuation is not another pair: the values stay.");
        Assert.AreEqual(BroadcastClass.Chosen, last);
        Assert.IsTrue(d.Selector.IsLinked);
        Assert.AreEqual(BroadcastSelectionState.Linked, d.Selector.StateAt(d.Now));
    }

    [TestMethod]
    public void AStrangerWithDifferentFieldsDuringARotationIsNeverAdopted()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 5);

        // The owner's pair is quiet for twenty seconds. A stranger of the same model, much nearer and with other levels,
        // is heard the whole time.
        bool adopted = false;
        for (int i = 0; i < 40; i++)
        {
            SelectionObservation seen = d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -35);
            adopted |= seen.Class == BroadcastClass.Chosen || seen.NewChoice || seen.Followed || seen.Switched;
            d.Tick(0.5);
        }

        Assert.IsFalse(adopted, "Different fields continue nothing.");

        // The owner's pair comes back under new addresses with the fields it had, and is the one followed.
        bool followed = false;
        for (int i = 0; i < 12; i++)
        {
            followed |= d.Send(SetANewAddress, InUse(true), -55).Followed;
            d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -35);
            d.Tick(0.5);
        }

        Assert.IsTrue(followed);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetANewAddress, InUse(true), -55).Class);
        Assert.AreEqual(BroadcastClass.OtherSet, d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -35).Class);
    }

    [TestMethod]
    public void AStrangerWithDifferentFieldsAfterTheOwnersPairWasLostIsNeverAdoptedWhateverTheSignal()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 5);
        d.Tick(11);

        bool adopted = false;
        for (int i = 0; i < 120; i++)
        {
            SelectionObservation seen = d.Send(Stranger, InUse(false, pairHigh: 0x5, pairLow: 0x7), -30);
            adopted |= seen.Class == BroadcastClass.Chosen;
            d.Tick(0.5);
        }

        Assert.IsFalse(adopted, "A minute of a much nearer same-model pair whose fields are not the linked set's.");
    }

    [TestMethod]
    public void AnAddressChangeThatArrivesLaterThanThirtySecondsAfterTheLastMessageIsNotFollowed()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 5);
        d.Tick(31);

        bool followed = false;
        for (int i = 0; i < 20; i++)
        {
            followed |= d.Send(SetANewAddress, InUse(true), -55).Followed;
            d.Tick(0.5);
        }

        Assert.IsFalse(followed, "Thirty seconds is the time in which the old address's last value is still current.");
        Assert.AreEqual(BroadcastSelectionState.Linked, d.Selector.StateAt(d.Now), "It is lost, but not yet dropped.");
    }

    [TestMethod]
    public void AnAddressChangeThatIsFollowedWithinTheThirtySecondsIsFollowedEvenWhenItIsHeardLate()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 5);
        d.Tick(26.5);

        bool followed = false;
        for (int i = 0; i < 6; i++)
        {
            followed |= d.Send(SetANewAddress, InUse(true), -55).Followed;
            d.Tick(0.5);
        }

        Assert.IsTrue(followed, "Twenty nine seconds after the old address's last message is inside the thirty.");
    }

    [TestMethod]
    public void EqualFieldsFromANewAddressWhileTheLinkedSetStillSendsAreNotItsContinuation()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 3);
        d.Tick(3.0);

        SelectionObservation seen = d.Send(SetANewAddress, InUse(true), -55);

        Assert.AreEqual(BroadcastClass.OtherSet, seen.Class);
        Assert.IsFalse(seen.NewChoice);
        Assert.IsFalse(seen.Followed);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, InUse(true), -55).Class, "The linked set still sends and is still the linked set.");
    }

    // A pair that closes its case for a while and opens it again may come back with other levels (the case charged the
    // buds) and new addresses. The open is a case open: it links.
    [TestMethod]
    public void TheOwnerReopeningTheCaseWithOtherLevelsAfterALossLinksAgain()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 3);
        d.Tick(60);

        SelectionObservation last = Burst(d, -55, 3, SetANewAddress, SetANewOtherBud, pairHigh: 0x9, pairLow: 0x9);

        Assert.IsTrue(d.Selector.IsLinked);
        Assert.AreEqual(BroadcastClass.Chosen, last.Class);
        Assert.IsTrue(d.Send(SetANewAddress, Bud(first: true, pairHigh: 0x9, pairLow: 0x9), -55).Class == BroadcastClass.Chosen);
    }

    // ---- Dropping the link

    [TestMethod]
    public void TheLinkIsDroppedWhenTheSetHasBeenLostForLongerThanTwoMinutesAndNotBefore()
    {
        SelectorDriver d = Linked(colour: 0x11);
        InUseRun(d, 4, colour: 0x11);

        d.Tick(119);
        Assert.AreEqual(BroadcastSelectionState.Linked, d.Selector.StateAt(d.Now), "A minute and fifty nine seconds is within the limit.");
        Assert.IsFalse(d.Selector.Expire(d.Now));
        Assert.IsTrue(d.Selector.IsLinked);

        d.Tick(2);
        Assert.AreEqual(BroadcastSelectionState.Listening, d.Selector.StateAt(d.Now), "Two minutes and a second: nothing is linked.");
        Assert.IsTrue(d.Selector.Expire(d.Now), "The drop is reported once, so what was kept can be cleared.");
        Assert.IsFalse(d.Selector.Expire(d.Now));
        Assert.IsFalse(d.Selector.IsLinked);
        Assert.IsNull(d.Selector.HeldColour, "The colour goes with the link.");
    }

    [TestMethod]
    public void AMessageThatArrivesAfterTheLimitReportsTheDropAndIsNotTheLinkedSets()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 4);
        d.Tick(125);

        SelectionObservation seen = d.Send(SetA, InUse(true), -55);

        Assert.IsTrue(seen.Dropped);
        Assert.AreEqual(BroadcastClass.NotLinked, seen.Class, "The same address is not linked again by being heard.");
        Assert.IsFalse(d.Selector.IsLinked);
    }

    [TestMethod]
    public void ADroppedLinkWaitsForTheNextCaseOpenWhatEverElseIsHeard()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 4);
        d.Tick(125);
        d.Selector.Expire(d.Now);

        // The owner's own pair in use, with the fields it had, for ten minutes: no case open, no link.
        for (int i = 0; i < 1200; i++)
        {
            d.Send(SetA, InUse(true), -45);
            d.Tick(0.5);
        }

        Assert.IsFalse(d.Selector.IsLinked);

        Burst(d, -55, 3);
        Assert.IsTrue(d.Selector.IsLinked, "The next case open links again.");
    }

    [TestMethod]
    public void TheLinkIsKeptWhileTheSetIsHeardWithinTheLimit()
    {
        SelectorDriver d = Linked();
        for (int round = 0; round < 10; round++)
        {
            InUseRun(d, 2);
            d.Tick(90); // a minute and a half of silence between each word, always under the limit
            Assert.AreEqual(BroadcastSelectionState.Linked, d.Selector.StateAt(d.Now), "Round " + round);
        }

        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, InUse(true), -55).Class);
    }

    [TestMethod]
    public void ANewPairedModelStartsTheSelectionOver()
    {
        SelectorDriver d = Linked();

        Assert.IsFalse(d.Selector.SetPairedModel(BroadcastFixtures.PairedModel), "The same model changes nothing.");
        Assert.IsTrue(d.Selector.IsLinked);
        Assert.IsTrue(d.Selector.SetPairedModel(0x1234));

        Assert.IsFalse(d.Selector.IsLinked);
        Assert.IsNull(d.Selector.HeldColour);
    }

    // ---- Another set opening its case while one is linked

    [TestMethod]
    public void AnotherPairOpeningItsCaseTenDecibelsNearerTakesTheLinkAndOneFiveDecibelsNearerDoesNot()
    {
        SelectorDriver closer = Linked(rssi: -60);
        bool switched = false;
        bool newChoice = false;
        for (int i = 0; i <= 16; i++)
        {
            closer.Send(SetA, InUse(true), -60);
            closer.Send(SetAOtherBud, InUse(false), -60);
            SelectionObservation a = closer.Send(SetB, OtherSet(), -50);
            SelectionObservation b = closer.Send(SetBOtherBud, Bud(first: false, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1), -50);
            switched |= a.Switched || b.Switched;
            newChoice |= a.NewChoice || b.NewChoice;
            closer.Tick(0.25);
        }

        Assert.IsTrue(switched, "Ten decibels nearer, opening its case: the owner's way of correcting a wrong link.");
        Assert.IsTrue(newChoice, "A switch drops the old pair's values.");

        SelectorDriver slightly = Linked(rssi: -60);
        switched = false;
        for (int i = 0; i <= 40; i++)
        {
            slightly.Send(SetA, InUse(true), -60);
            slightly.Send(SetAOtherBud, InUse(false), -60);
            SelectionObservation a = slightly.Send(SetB, OtherSet(), -55);
            SelectionObservation b = slightly.Send(SetBOtherBud, Bud(first: false, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1), -55);
            switched |= a.Switched || b.Switched;
            slightly.Tick(0.25);
        }

        Assert.IsFalse(switched, "Five decibels is under the eight it takes.");
    }

    // The margin is exact: seven decibels nearer never takes the link and eight does.
    [TestMethod]
    public void TheMarginForTakingTheLinkIsEightDecibelsExactly()
    {
        foreach ((sbyte rssi, bool expected) in new[] { ((sbyte)-53, false), ((sbyte)-52, true) })
        {
            SelectorDriver d = Linked(rssi: -60);
            bool switched = false;
            for (int i = 0; i <= 16; i++)
            {
                d.Send(SetA, InUse(true), -60);
                d.Send(SetAOtherBud, InUse(false), -60);
                switched |= d.Send(SetB, OtherSet(), rssi).Switched;
                switched |= d.Send(SetBOtherBud, Bud(first: false, caseNibble: 0x2, pairHigh: 0x3, pairLow: 0x1), rssi).Switched;
                d.Tick(0.25);
            }

            Assert.AreEqual(expected, switched, rssi + " dBm against -60.");
        }
    }

    [TestMethod]
    public void AnotherPairNearerStillButNotOpeningItsCaseNeverTakesTheLink()
    {
        SelectorDriver d = Linked(rssi: -70);
        bool switched = false;
        for (int i = 0; i < 400; i++)
        {
            d.Send(SetA, InUse(true), -70);
            switched |= d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -30).Switched;
            d.Tick(0.5);
        }

        Assert.IsFalse(switched, "A worn pair, however near, does not open a case.");
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(SetA, InUse(true), -70).Class);
    }

    // ---- Anchors: what the selection stands on

    // A stranger said exactly what the linked set said, once, so it merged into the set for a moment. It was never linked
    // or switched to, so it is not what the selection stands on: while it merged, and while the owner is quiet for less
    // than the window, none of its different messages is the linked set's.
    [TestMethod]
    public void AStrangerHeardOnceWithEqualFieldsDoesNotTakeOverWhenTheOwnerGoesQuiet()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 3);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(Stranger, InUse(true), -85).Class, "Sanity: the equal message merged for the moment.");

        int chosen = 0;
        bool changed = false;
        for (int i = 0; i < 17; i++)
        {
            d.Tick(0.5);
            SelectionObservation seen = d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -85);
            chosen += seen.Class == BroadcastClass.Chosen ? 1 : 0;
            changed |= seen.NewChoice || seen.Switched || seen.Followed;
        }

        Assert.AreEqual(0, chosen, "Eight and a half seconds with the owner quiet: the stranger's own messages are not the linked set's.");
        Assert.IsFalse(changed);

        d.Tick(0.5);
        SelectionObservation owner = d.Send(SetA, InUse(true), -55);
        Assert.AreEqual(BroadcastClass.Chosen, owner.Class, "The owner is back and is still the linked set.");
        Assert.IsFalse(owner.NewChoice || owner.Switched || owner.Followed);
    }

    [TestMethod]
    public void TheDifferentMessagesOfAStrangerThatMergedOnceAreNeverTheLinkedSets()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 3);
        d.Send(Stranger, InUse(true), -85);

        int chosen = 0;
        for (int i = 0; i < 20; i++)
        {
            d.Tick(0.5);
            d.Send(SetA, InUse(true), -55);
            chosen += d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -85).Class == BroadcastClass.Chosen ? 1 : 0;
        }

        Assert.AreEqual(0, chosen, "Twenty different messages of a stranger that matched once.");
    }

    [TestMethod]
    public void AMessageOfATagThatIsNotAnAnchorIsChosenOnlyWhenItMatchesAnAnchorsMessageNearInTime()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 2);
        d.Send(SetA, InUse(true), -55);
        d.Tick(0.5);

        Assert.AreEqual(BroadcastClass.Chosen, d.Send(Stranger, InUse(true), -85).Class);
        d.Tick(0.5);
        Assert.AreEqual(BroadcastClass.OtherSet, d.Send(Stranger, InUse(true, pairHigh: 0x6, pairLow: 0x6), -85).Class);
        d.Tick(0.5);
        Assert.AreEqual(BroadcastClass.Chosen, d.Send(Stranger, InUse(true), -85).Class, "Back to what the anchor says, inside two seconds of it.");
    }

    // A second sender of the set (the other bud) that joins after the link and matches for long enough is an anchor
    // too, so the set stays linked through a quiet of the first sender longer than the window.
    [TestMethod]
    public void ASecondBudThatMatchesForThreeSecondsBecomesAnAnchorAndHoldsTheSetWhenTheFirstGoesQuiet()
    {
        var d = new SelectorDriver();
        for (int i = 0; i <= 12; i++)
        {
            d.Send(SetA, Bud(first: true), -58); // one bud alone opens the link
            d.Tick(0.25);
        }

        Assert.IsTrue(d.Selector.IsLinked);
        for (int i = 0; i <= 7; i++)
        {
            d.Send(SetA, InUse(true), -58);
            d.Send(SetAOtherBud, InUse(false), -56);
            d.Tick(0.5);
        }

        d.Tick(3.0);
        bool followed = false;
        BroadcastClass last = BroadcastClass.OtherSet;
        for (int i = 0; i < 40; i++)
        {
            SelectionObservation seen = d.Send(SetAOtherBud, InUse(false), -56);
            followed |= seen.Followed || seen.NewChoice;
            last = seen.Class;
            d.Tick(0.5);
        }

        Assert.IsFalse(followed, "Twenty seconds with only the second bud: it was an anchor, so nothing was followed or linked again.");
        Assert.AreEqual(BroadcastClass.Chosen, last);
    }

    [TestMethod]
    public void ASecondSenderThatMatchedForOnlyTwoSecondsIsNotAnAnchorSoTheSetIsFollowedWhenTheFirstGoesQuiet()
    {
        var d = new SelectorDriver();
        for (int i = 0; i <= 12; i++)
        {
            d.Send(SetA, Bud(first: true), -58);
            d.Tick(0.25);
        }

        d.Tick(0.5);
        for (int i = 0; i <= 2; i++)
        {
            d.Send(SetA, InUse(true), -58);
            d.Send(SetAOtherBud, InUse(false), -56);
            d.Tick(1);
        }

        d.Tick(2.5);
        bool followed = false;
        for (int i = 0; i < 40; i++)
        {
            followed |= d.Send(SetAOtherBud, InUse(false), -56).Followed;
            d.Tick(0.5);
        }

        Assert.IsTrue(followed, "Not an anchor: with the first sender quiet for longer than the window its fields continue, so the set is followed.");
    }

    // The residual risk, stated as it behaves: a same-model pair whose fields equal the linked set's last, heard for two
    // seconds and three messages within thirty seconds of it going quiet, is followed as if it were the owner's.
    [TestMethod]
    public void AStrangerWithTheLinkedSetsExactFieldsWithinThirtySecondsOfItGoingQuietIsFollowed()
    {
        SelectorDriver d = Linked();
        InUseRun(d, 3);

        SelectionObservation seen = default;
        double firstFollowedAt = -1;
        double elapsed = 0;
        for (int i = 0; i < 60; i++)
        {
            d.Tick(0.5);
            elapsed += 0.5;
            seen = d.Send(Stranger, InUse(true), -85);
            if (seen.Class == BroadcastClass.Chosen && seen.Followed && firstFollowedAt < 0)
            {
                firstFollowedAt = elapsed;
            }
        }

        Assert.IsGreaterThan(9.0, firstFollowedAt, "Not while the owner's last message was inside the window.");
        Assert.IsLessThan(16.0, firstFollowedAt, "Within two seconds and three messages of the window running out.");
    }

    [TestMethod]
    public void TheOtherBudsLatestMessageIsAvailableForTheBudOrderCheck()
    {
        SelectorDriver d = Linked();
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
