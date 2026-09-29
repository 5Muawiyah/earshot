using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Widget.SetupRecordFixtures;

namespace Earshot.Tests.Widget;

// DecodeProof.Evaluate is pure: records in, a table and a status per field out. Every rule needs its evidence
// twice over before it proves anything, and a later record that disagrees withdraws what an earlier pair proved.
[TestClass]
public sealed class DecodeProofTests
{
    // One record where the high nibble is the right bud (right 80, left 40).
    private static BatterySetupRecord HighIsRight(int minutes = 0, byte status = 0x00) =>
        Record(Message(high: 8, low: 4, status: status), Picks(left: 40, right: 80), minutes);

    private static BatterySetupRecord HighIsLeft(int minutes = 0, byte status = 0x00) =>
        Record(Message(high: 4, low: 8, status: status), Picks(left: 40, right: 80), minutes);

    private static FieldProofStatus Status(DecodeProofResult result, DecodeField field) => result.Fields[field].Status;

    [TestMethod]
    public void NoRecordsProveNothing()
    {
        DecodeProofResult result = DecodeProof.Evaluate([]);

        Assert.AreEqual(ProximityDecodeTable.Unproved, result.Table, "The table is exactly the unproved one.");
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.FlipBit));
        Assert.AreEqual(FieldProofStatus.Documented, Status(result, DecodeField.CaseNibble), "The case is documented and shown from the first claim.");
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.CaseChargingBit));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.RightChargingBit));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.LeftChargingBit));
        Assert.AreEqual(FieldProofStatus.NotProvableBySetup, Status(result, DecodeField.LeftInEarBit));
        Assert.AreEqual(FieldProofStatus.NotProvableBySetup, Status(result, DecodeField.RightInEarBit));
        Assert.AreEqual(FieldProofStatus.NotProvableBySetup, Status(result, DecodeField.Lid));
        Assert.IsEmpty(result.Notes);
    }

    [TestMethod]
    public void OneDiscriminatingRecordLeavesTheOrderUnproved()
    {
        DecodeProofResult result = DecodeProof.Evaluate([HighIsRight()]);

        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.HighNibbleIsRight),
            "One entry could still be the owner entering the two buds the wrong way round.");
        Assert.IsNull(result.Table.HighNibbleIsRight);
        Assert.AreEqual(1, result.Fields[DecodeField.HighNibbleIsRight].Agree);
    }

    [TestMethod]
    public void TwoAgreeingRecordsProveHighNibbleIsRight()
    {
        DecodeProofResult result = DecodeProof.Evaluate([HighIsRight(0), Record(Message(high: 6, low: 9), Picks(left: 90, right: 60), 1)]);

        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(true, result.Table.HighNibbleIsRight);
        Assert.AreEqual(2, result.Fields[DecodeField.HighNibbleIsRight].Agree);
        Assert.AreEqual(0, result.Fields[DecodeField.HighNibbleIsRight].Disagree);
        Assert.IsNull(result.Table.FlipBit);
    }

    [TestMethod]
    public void TwoAgreeingRecordsProveHighNibbleIsLeft()
    {
        DecodeProofResult result = DecodeProof.Evaluate([HighIsLeft(0), Record(Message(high: 9, low: 6), Picks(left: 90, right: 60), 1)]);

        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(false, result.Table.HighNibbleIsRight);
    }

    [TestMethod]
    public void BudsReadingTheSameAreNotDiscriminating()
    {
        BatterySetupRecord same = Record(Message(high: 6, low: 6), Picks(left: 60, right: 60));

        Assert.AreEqual("both buds read the same", DecodeProof.Discriminate(same).WhyNot);
        DecodeProofResult result = DecodeProof.Evaluate([same, same with { EndedAtUtc = same.EndedAtUtc.AddMinutes(1) }]);

        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.HighNibbleIsRight), "Two records that say nothing prove nothing.");
        Assert.HasCount(2, result.Notes);
        Assert.IsTrue(result.Notes[0].Contains("both buds read the same", StringComparison.Ordinal), result.Notes[0]);
        Assert.IsTrue(result.Notes[0].Contains(same.FileName, StringComparison.Ordinal), "The note names the record.");
    }

    [TestMethod]
    public void ANibbleMatchingBothPicksIsNotDiscriminating()
    {
        BatterySetupRecord ambiguous = Record(Message(high: 8, low: 7), Picks(left: 80, right: 70));

        BudOrderEvidence evidence = DecodeProof.Discriminate(ambiguous);

        Assert.IsNull(evidence.HighNibbleIsRight);
        Assert.AreEqual("a nibble matches both picks", evidence.WhyNot, "80 is within a step of 80 and of 70; two steps would have proved nothing.");
    }

    [TestMethod]
    public void AnUnknownNibbleAndAPickNothingMatchesAreNotDiscriminating()
    {
        Assert.AreEqual("a bud nibble is unknown", DecodeProof.Discriminate(Record(Message(high: 15, low: 4), Picks(40, 80))).WhyNot);
        Assert.AreEqual("no pick matches", DecodeProof.Discriminate(Record(Message(high: 2, low: 4), Picks(90, 90))).WhyNot);
        Assert.AreEqual("both nibbles match the same bud", DecodeProof.Discriminate(Record(Message(high: 8, low: 7), Picks(left: 0, right: 80))).WhyNot);
    }

    // The tolerance is one nibble step either way: pick 80 is matched by nibbles 7, 8 and 9, and by neither 6 nor 10.
    [TestMethod]
    public void TheToleranceIsOneStepEitherWay()
    {
        foreach (int nibble in new[] { 7, 8, 9 })
        {
            BudOrderEvidence evidence = DecodeProof.Discriminate(Record(Message(high: nibble, low: 2), Picks(left: 20, right: 80)));
            Assert.AreEqual(true, evidence.HighNibbleIsRight, "Nibble " + nibble + " matches a pick of 80.");
        }

        foreach (int nibble in new[] { 6, 10 })
        {
            BudOrderEvidence evidence = DecodeProof.Discriminate(Record(Message(high: nibble, low: 2), Picks(left: 20, right: 80)));
            Assert.IsNull(evidence.HighNibbleIsRight, "Nibble " + nibble + " is two steps from a pick of 80.");
            Assert.AreEqual("no pick matches", evidence.WhyNot);
        }
    }

    [TestMethod]
    public void ADisagreeingThirdRecordWithdrawsTheOrder()
    {
        DecodeProofResult proved = DecodeProof.Evaluate([HighIsRight(0), HighIsRight(1)]);
        Assert.AreEqual(FieldProofStatus.Proved, Status(proved, DecodeField.HighNibbleIsRight));

        DecodeProofResult withdrawn = DecodeProof.Evaluate([HighIsRight(0), HighIsRight(1), HighIsLeft(2)]);

        Assert.AreEqual(FieldProofStatus.Withdrawn, Status(withdrawn, DecodeField.HighNibbleIsRight));
        Assert.IsNull(withdrawn.Table.HighNibbleIsRight, "The buds stop decoding at once.");
        Assert.AreEqual(2, withdrawn.Fields[DecodeField.HighNibbleIsRight].Agree);
        Assert.AreEqual(1, withdrawn.Fields[DecodeField.HighNibbleIsRight].Disagree);
        Assert.AreEqual(FieldProofStatus.Unproved, Status(withdrawn, DecodeField.FlipBit), "Three records are too few to look for a flip.");
    }

    // Four discriminating records that split two and two on one status bit prove the flip: the order follows the bit.
    [TestMethod]
    public void FourRecordsSplitOnOneStatusBitProveTheFlip()
    {
        BatterySetupRecord[] records =
        [
            HighIsRight(0, status: 0x00),
            Record(Message(high: 6, low: 9, status: 0x00), Picks(left: 90, right: 60), 1),
            HighIsLeft(2, status: 0x08),
            Record(Message(high: 9, low: 6, status: 0x08), Picks(left: 90, right: 60), 3),
        ];

        DecodeProofResult result = DecodeProof.Evaluate(records);

        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.FlipBit));
        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(3, result.Table.FlipBit);
        Assert.AreEqual(true, result.Table.FlipWhenSet, "Bit 3 reading 1 swaps the two nibbles' sides.");
        Assert.AreEqual(true, result.Table.HighNibbleIsRight);

        // The table decodes every record the way its owner said it read.
        foreach (BatterySetupRecord record in records)
        {
            DecodedReading reading = ProximityDecoder.Decode(record.Candidate!.LastOkMessage!.Value, result.Table, record.EndedAtUtc);
            Assert.AreEqual(record.Picks.Left, reading.Left.Percent, record.FileName);
            Assert.AreEqual(record.Picks.Right, reading.Right.Percent, record.FileName);
        }
    }

    [TestMethod]
    public void AFlipTwoBitsExplainIsNotProved()
    {
        BatterySetupRecord[] records =
        [
            HighIsRight(0, status: 0x00),
            Record(Message(high: 6, low: 9, status: 0x00), Picks(left: 90, right: 60), 1),
            HighIsLeft(2, status: 0x28),   // bits 3 and 5 both set together
            Record(Message(high: 9, low: 6, status: 0x28), Picks(left: 90, right: 60), 3),
        ];

        DecodeProofResult result = DecodeProof.Evaluate(records);

        Assert.AreEqual(FieldProofStatus.Withdrawn, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.FlipBit));
        Assert.IsNull(result.Table.FlipBit);
        Assert.IsTrue(result.Notes.Any(n => n.Contains("2 status bits", StringComparison.Ordinal)), "The note says how many bits fit.");
    }

    [TestMethod]
    public void AFlipWithOneRecordPerBitValueIsNotProved()
    {
        BatterySetupRecord[] records =
        [
            HighIsRight(0, status: 0x00),
            HighIsRight(1, status: 0x00),
            HighIsLeft(2, status: 0x08),   // the only record with the bit set
            HighIsLeft(3, status: 0x00),
        ];

        DecodeProofResult result = DecodeProof.Evaluate(records);

        Assert.AreEqual(FieldProofStatus.Withdrawn, Status(result, DecodeField.HighNibbleIsRight));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.FlipBit), "One record with the bit set is too few for a fit to mean anything.");
        Assert.IsNull(result.Table.HighNibbleIsRight);
    }

    [TestMethod]
    public void TheCaseIsDocumentedUntilTwoDisagreementsOutweighAgreements()
    {
        // The case nibble reads 5 (50%): a pick of 50 agrees, a pick of 90 disagrees.
        BatterySetupRecord agrees(int m) => Record(Message(8, 4, caseNibble: 5), Picks(40, 80, box: 50), m);
        BatterySetupRecord disagrees(int m) => Record(Message(8, 4, caseNibble: 5), Picks(40, 80, box: 90), m);

        Assert.AreEqual(FieldProofStatus.Documented, Status(DecodeProof.Evaluate([agrees(0), agrees(1)]), DecodeField.CaseNibble));
        Assert.AreEqual(FieldProofStatus.Documented, Status(DecodeProof.Evaluate([agrees(0), agrees(1), disagrees(2), disagrees(3)]), DecodeField.CaseNibble),
            "Equal for and against: not outweighed.");

        DecodeProofResult doubted = DecodeProof.Evaluate([agrees(0), disagrees(1), disagrees(2)]);
        Assert.AreEqual(FieldProofStatus.Doubted, Status(doubted, DecodeField.CaseNibble));
        Assert.IsTrue(doubted.Table.CaseNibbleDoubted);
        Assert.AreEqual(1, doubted.Fields[DecodeField.CaseNibble].Agree);
        Assert.AreEqual(2, doubted.Fields[DecodeField.CaseNibble].Disagree);
    }

    [TestMethod]
    public void OneCaseDisagreementDoesNotDoubtIt()
    {
        DecodeProofResult result = DecodeProof.Evaluate([Record(Message(8, 4, caseNibble: 5), Picks(40, 80, box: 90))]);

        Assert.AreEqual(FieldProofStatus.Documented, Status(result, DecodeField.CaseNibble), "One disagreement is a mis-pick or a stale iPhone value.");
        Assert.IsFalse(result.Table.CaseNibbleDoubted);
    }

    // A bit that is always 1 "explains" a part that is always charging, and a flag that never varies proves
    // nothing about any bit: both sides need to vary.
    [TestMethod]
    public void AChargingBitNeedsVariationOnBothSides()
    {
        // The flags never vary.
        DecodeProofResult flat = DecodeProof.Evaluate(
        [
            Record(Message(8, 4, chargingBits: 0b0010), Picks(40, 80, rightCharging: true), 0),
            Record(Message(6, 9, chargingBits: 0b0010), Picks(90, 60, rightCharging: true), 1),
        ]);
        Assert.AreEqual(FieldProofStatus.Unproved, Status(flat, DecodeField.RightChargingBit), "A part that is always charging cannot prove a bit.");

        // The bit never varies.
        DecodeProofResult constantBit = DecodeProof.Evaluate(
        [
            Record(Message(8, 4, chargingBits: 0b0000), Picks(40, 80, rightCharging: true), 0),
            Record(Message(6, 9, chargingBits: 0b0000), Picks(90, 60, rightCharging: false), 1),
        ]);
        Assert.AreEqual(FieldProofStatus.Unproved, Status(constantBit, DecodeField.RightChargingBit));
        Assert.IsNull(constantBit.Table.RightChargingBit);
    }

    [TestMethod]
    public void AChargingBitTwoPartsExplainIsNotProved()
    {
        // The left and right flags always agree, so the same bit explains both equally well.
        DecodeProofResult result = DecodeProof.Evaluate(
        [
            Record(Message(8, 4, chargingBits: 0b0010), Picks(40, 80, leftCharging: true, rightCharging: true), 0),
            Record(Message(6, 9, chargingBits: 0b0000), Picks(90, 60, leftCharging: false, rightCharging: false), 1),
        ]);

        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.LeftChargingBit));
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.RightChargingBit));
    }

    [TestMethod]
    public void AChargingBitTwoBitsExplainIsNotProved()
    {
        // Bits 5 and 6 always read the same, so neither can be told from the other.
        DecodeProofResult result = DecodeProof.Evaluate(
        [
            Record(Message(8, 4, chargingBits: 0b0110), Picks(40, 80, rightCharging: true), 0),
            Record(Message(6, 9, chargingBits: 0b0000), Picks(90, 60, rightCharging: false), 1),
            Record(Message(7, 3, chargingBits: 0b0000), Picks(30, 70, leftCharging: false), 2),
        ]);

        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.RightChargingBit));
    }

    [TestMethod]
    public void AChargingBitWithVariationAndUniquenessIsProved()
    {
        // Left charges in the second record, right in the first, the case in the third; each has its own bit.
        BatterySetupRecord[] records =
        [
            Record(Message(8, 4, chargingBits: 0b0010), Picks(40, 80, rightCharging: true), 0),
            Record(Message(6, 9, chargingBits: 0b0001), Picks(90, 60, leftCharging: true), 1),
            Record(Message(7, 3, chargingBits: 0b0100), Picks(30, 70, caseCharging: true), 2),
        ];

        DecodeProofResult result = DecodeProof.Evaluate(records);

        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.LeftChargingBit));
        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.RightChargingBit));
        Assert.AreEqual(FieldProofStatus.Proved, Status(result, DecodeField.CaseChargingBit));
        Assert.AreEqual(4, result.Table.LeftChargingBit);
        Assert.AreEqual(5, result.Table.RightChargingBit);
        Assert.AreEqual(6, result.Table.CaseChargingBit);

        DecodedReading reading = ProximityDecoder.Decode(records[1].Candidate!.LastOkMessage!.Value, result.Table, Start);
        Assert.AreEqual(true, reading.Left.Charging, "The second record's left bud was charging.");
        Assert.AreEqual(false, reading.Right.Charging);
        Assert.AreEqual(false, reading.Case.Charging);
    }

    [TestMethod]
    public void InEarAndLidAreNeverProved()
    {
        BatterySetupRecord[] many = Enumerable.Range(0, 8).Select(i => Record(Message(8, 4, status: (byte)(i * 17)), Picks(40, 80), i)).ToArray();

        DecodeProofResult result = DecodeProof.Evaluate(many);

        foreach (DecodeField field in new[] { DecodeField.LeftInEarBit, DecodeField.RightInEarBit, DecodeField.Lid })
        {
            Assert.AreEqual(FieldProofStatus.NotProvableBySetup, Status(result, field), field + " cannot be proved by three percentage pickers.");
        }

        Assert.IsNull(result.Table.LeftInEarBit);
        Assert.IsNull(result.Table.RightInEarBit);
        Assert.IsNull(result.Table.LidOpenBit);
        Assert.IsNull(result.Table.LidCounterMask);
    }

    [TestMethod]
    public void RecordsWithoutAnOkMessageAreIgnored()
    {
        BatterySetupRecord noCandidate = HighIsRight() with { Candidate = null };

        DecodeProofResult result = DecodeProof.Evaluate([ShortFormRecord(Picks(40, 80), 0), noCandidate, HighIsRight(2)]);

        Assert.AreEqual(1, result.Fields[DecodeField.HighNibbleIsRight].Agree, "Only the one usable record took part.");
        Assert.AreEqual(FieldProofStatus.Unproved, Status(result, DecodeField.HighNibbleIsRight));
        Assert.IsEmpty(result.Notes, "A record with nothing to decode is ignored, not noted as failing.");
    }

    // The picks are evidence only: a proved table decodes what the advertisement says, whatever the owner picked.
    [TestMethod]
    public void TheProvedTableDecodesTheAdvertisementNotThePicks()
    {
        DecodeProofResult result = DecodeProof.Evaluate([HighIsRight(0), HighIsRight(1)]);

        DecodedReading reading = ProximityDecoder.Decode(Message(high: 3, low: 7, caseNibble: 2), result.Table, Start);

        Assert.AreEqual(30, reading.Right.Percent);
        Assert.AreEqual(70, reading.Left.Percent);
        Assert.AreEqual(20, reading.Case.Percent);
    }
}
