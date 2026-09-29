using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class OwnershipRuleTests
{
    // A table whose only proved part is the case nibble, so the case takes part in the battery check.
    private static readonly ProximityDecodeTable CaseProved = ProximityDecodeTable.Unproved with { CaseNibbleProved = true };

    private static readonly DateTimeOffset ClaimedAt = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);

    private static ProximityMessage Message(
        byte modelHigh = WidgetFixtures.ModelHigh, byte modelLow = WidgetFixtures.ModelLow, byte colour = WidgetFixtures.Colour,
        byte status = 0x00, byte batteryA = 0x00, byte batteryB = 0x00, byte lid = 0x00) =>
        new(modelHigh, modelLow, status, batteryA, batteryB, lid, colour, Reserved: 0x00);

    private static ProximityParse Ok(ProximityMessage m) => new(ProximityParseStatus.Ok, m, null, null, 1, Array.Empty<byte>());

    private static WidgetClaim Claim(sbyte threshold = -70, OwnedBattery? last = null, bool nibblesAreNamedOrder = false) =>
        SetupRecordFixtures.Claim(last ?? new OwnedBattery(null, null, null, ClaimedAt), nibblesAreNamedOrder, threshold);

    private static OwnershipInput Input(
        ProximityParse parse, WidgetClaim? claim, ProximityDecodeTable? table = null, sbyte rssi = -50) =>
        new(parse, rssi, claim, table ?? ProximityDecodeTable.Unproved, Now);

    [TestMethod]
    public void WithoutAClaimNothingIsOwnedAndTheCountSaysNoClaim()
    {
        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(Message()), claim: null));

        Assert.AreEqual(OwnershipVerdict.NoClaim, result.Verdict);
        Assert.IsNull(result.UpdatedLast);
    }

    [TestMethod]
    public void AMatchingMessageAtTheThresholdWithConsistentBatteryIsOwned()
    {
        WidgetClaim claim = Claim(threshold: -70, last: new OwnedBattery(0, 0, 0, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x00);

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -70));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
        Assert.IsNotNull(result.UpdatedLast);
    }

    [TestMethod]
    public void ASameModelStrangerAtWeakerSignalIsNotOwned()
    {
        WidgetClaim claim = Claim(threshold: -70);
        ProximityMessage m = Message();

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -80));

        Assert.AreEqual(OwnershipVerdict.SignalBelowThreshold, result.Verdict);
    }

    [TestMethod]
    public void ASameModelStrangerAtStrongSignalWithInconsistentBatteryIsNotOwned()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 2, ClaimedAt));
        ProximityMessage m = Message(batteryB: 0x09); // case jumps from 2 to 9, not charging

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, CaseProved, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, result.Verdict);
    }

    [TestMethod]
    public void ADifferentColourAtStrongSignalIsNotOwned()
    {
        WidgetClaim claim = Claim();
        ProximityMessage m = Message(colour: WidgetFixtures.StrangerColour);

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.ModelOrColourMismatch, result.Verdict);
    }

    [TestMethod]
    public void ADifferentModelIsNotOwned()
    {
        WidgetClaim claim = Claim();
        ProximityMessage m = Message(modelLow: WidgetFixtures.StrangerModelLow);

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.ModelOrColourMismatch, result.Verdict);
    }

    [TestMethod]
    [DataRow((byte)5, (byte)5)] // equal
    [DataRow((byte)5, (byte)4)] // lower
    [DataRow((byte)5, (byte)6)] // one step higher
    public void EqualLowerOrOneStepHigherIsConsistent(byte lastCase, byte newCase)
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, lastCase, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: newCase);

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
    }

    [TestMethod]
    public void HigherByMoreThanOneStepFailsWithTheChargingBitUnproved()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x07); // case 5 -> 7, no charging bit proved

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, CaseProved, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, result.Verdict);
    }

    [TestMethod]
    public void HigherByMoreThanOneStepPassesOnlyWhileThatPartChargesWithTheBitProved()
    {
        var table = CaseProved with { CaseChargingBit = 4 };
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0b0001_0111); // case nibble 7, bit 4 (charging) set

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, table: table, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
    }

    [TestMethod]
    public void TheUnknownValueNeitherConfirmsNorDeniesAndNeverOverwritesTheLastReading()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x0F); // case nibble 0xF: unknown

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
        Assert.AreEqual(5, result.UpdatedLast!.Case);
    }

    [TestMethod]
    public void AValueAboveTenIsUnknownToo()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x0B); // case nibble 11: out of range, unknown

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
        Assert.AreEqual(5, result.UpdatedLast!.Case);
    }

    // The name once distinguished this from a live-connection waiver; owner decision 2026-09-27 ("same
    // checks always") removed that concept from OwnershipInput entirely, so there is no other case left to
    // tell it apart from.
    [TestMethod]
    public void AMessageWithEveryNibbleUnknownIsNotOwned()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0xFF, batteryB: 0x0F); // both bud nibbles and the case nibble unknown

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryUnreadable, result.Verdict);
        Assert.IsNull(result.UpdatedLast);
    }

    // Owner decision 2026-09-27 ("same checks always"): a live connection used to waive this exact jump.
    // There is no live concept left in OwnershipInput at all; the same message now simply fails the
    // ordinary consistency check, like any other reading would.
    [TestMethod]
    public void ABatteryJumpTheOldLiveWaiverWouldHaveAcceptedIsNowInconsistent()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x09); // case 5 -> 9, no charging bit proved

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, CaseProved, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, result.Verdict);
        Assert.IsNull(result.UpdatedLast);
    }

    // Owner decision 2026-09-27: candidate counting went with the waiver, so there is nothing left to make
    // "two candidates" ambiguous. A consistent reading is Owned by the ordinary checks alone.
    [TestMethod]
    public void AConsistentReadingIsOwnedWithNoCandidateCountToConsult()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(); // case 0, consistent (lower than last 5)

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
    }

    [TestMethod]
    public void ATruncatedPayloadIsNotEvaluated()
    {
        var parse = new ProximityParse(ProximityParseStatus.Truncated, null, 0x01, 10, 1, Array.Empty<byte>());

        OwnershipResult result = OwnershipRule.Evaluate(Input(parse, Claim(), rssi: -50));

        Assert.AreEqual(OwnershipVerdict.NotEvaluated, result.Verdict);
    }

    [TestMethod]
    public void AWrongMessageTypeIsNotEvaluated()
    {
        var parse = new ProximityParse(ProximityParseStatus.WrongType, null, null, null, 0, Array.Empty<byte>());

        OwnershipResult result = OwnershipRule.Evaluate(Input(parse, Claim(), rssi: -50));

        Assert.AreEqual(OwnershipVerdict.NotEvaluated, result.Verdict);
    }

    [TestMethod]
    public void AnUnknownFormIsNotEvaluated()
    {
        var parse = new ProximityParse(ProximityParseStatus.UnknownForm, null, 0x06, 17, 1, Array.Empty<byte>());

        OwnershipResult result = OwnershipRule.Evaluate(Input(parse, Claim(), rssi: -50));

        Assert.AreEqual(OwnershipVerdict.NotEvaluated, result.Verdict);
    }

    [TestMethod]
    public void WithTheOrderUnprovedTheBudPairIsComparedUnordered()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(8, 6, 0, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x68, batteryB: 0x00); // wire high 6, wire low 8: the set {8,6} again

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
    }

    // The test above only ever exercised the case where the unordered check must return true (the same
    // set, rotated); it could not have failed had that check always returned true regardless of input. This
    // pins the other side: a pair that matches neither last value, in either position, even unordered.
    [TestMethod]
    public void WithTheOrderUnprovedAnInconsistentPairFailsEvenUnordered()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(5, 3, 0, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x91, batteryB: 0x00); // wire high 9, wire low 1: matches neither 5 nor 3, unordered or not

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, result.Verdict);
    }

    [TestMethod]
    public void WithTheOrderProvedTheBudsAreComparedByName()
    {
        var table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        WidgetClaim claim = Claim(last: new OwnedBattery(NibbleHigh: 6, NibbleLow: 8, Case: 0, ClaimedAt), nibblesAreNamedOrder: true); // Right 6, Left 8
        ProximityMessage m = Message(batteryA: 0x86, batteryB: 0x00); // wire high (Right) 8, wire low (Left) 6

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, table: table, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, result.Verdict);
    }

    // The claim did not used to record which convention wrote its stored nibbles. A claim made while the
    // order was unproved (wire order) compared against a table that has since had the order proved (named)
    // would otherwise silently compare the wrong things; it must fail closed instead.
    [TestMethod]
    public void ANamedOrderTableAgainstAWireOrderClaimIsNotOwned()
    {
        var table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        WidgetClaim claim = Claim(last: new OwnedBattery(NibbleHigh: 6, NibbleLow: 8, Case: 0, ClaimedAt), nibblesAreNamedOrder: false);
        ProximityMessage m = Message(batteryA: 0x68, batteryB: 0x00); // would be Owned if compared unordered

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, table: table, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.NibbleOrderMismatch, result.Verdict);
        Assert.IsNull(result.UpdatedLast);
    }

    // The reverse: a claim made once the order was proved (named), read back against a table that has
    // reverted to unproved (a decode table change, or a claim moved to another build), must also fail closed
    // rather than treat the named values as wire-position ones.
    [TestMethod]
    public void AWireOrderTableAgainstANamedOrderClaimIsNotOwned()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(NibbleHigh: 6, NibbleLow: 8, Case: 0, ClaimedAt), nibblesAreNamedOrder: true);
        ProximityMessage m = Message(batteryA: 0x68, batteryB: 0x00);

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, table: ProximityDecodeTable.Unproved, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.NibbleOrderMismatch, result.Verdict);
    }

    [TestMethod]
    public void AnOwnedReadingUpdatesOnlyTheKnownParts()
    {
        WidgetClaim claim = Claim(last: new OwnedBattery(0, 0, 5, ClaimedAt));
        ProximityMessage m = Message(batteryA: 0x00, batteryB: 0x1F); // case unknown (0xF), buds known and equal to last

        OwnershipResult result = OwnershipRule.Evaluate(Input(Ok(m), claim, rssi: -50));

        Assert.AreEqual(OwnershipVerdict.Owned, result.Verdict);
        Assert.AreEqual(5, result.UpdatedLast!.Case);
        Assert.AreEqual(0, result.UpdatedLast!.NibbleHigh);
        Assert.AreEqual(0, result.UpdatedLast!.NibbleLow);
    }

    // A case that is not proved decodes to nothing, so it is never compared with the claim's: a case that jumped is
    // not a reason to refuse a reading whose buds are consistent. Once it is proved the jump is refused.
    [TestMethod]
    public void AnUnprovedCaseIsNotCompared()
    {
        var last = new OwnedBattery(5, 6, 2, ClaimedAt);
        ProximityMessage jumped = Message(batteryA: 0x65, batteryB: 0x09);   // the case nibble went 2 -> 9

        OwnershipResult proved = OwnershipRule.Evaluate(Input(Ok(jumped), Claim(last: last), ProximityDecodeTable.Unproved with { CaseNibbleProved = true }));
        OwnershipResult unproved = OwnershipRule.Evaluate(Input(Ok(jumped), Claim(last: last)));

        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, proved.Verdict, "Sanity: with the case proved the jump is refused.");
        Assert.AreEqual(OwnershipVerdict.Owned, unproved.Verdict, "With the case unproved it is not compared.");
        Assert.AreEqual(2, unproved.UpdatedLast!.Case, "The last known case value is kept, not replaced by a value nobody has proved.");
    }
}
