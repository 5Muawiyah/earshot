using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class ProximityDecoderTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static ProximityMessage Message(
        byte status = 0x00, byte batteryA = 0x00, byte batteryB = 0x00, byte lid = 0x00) =>
        new(ModelHigh: 0xEE, ModelLow: 0xEE, Status: status, BatteryA: batteryA, BatteryB: batteryB, Lid: lid, Colour: 0xEE, Reserved: 0x00);

    private static DecodedReading Decode(ProximityMessage m) => ProximityDecoder.Decode(m, ProximityDecodeTable.Documented, At);

    [TestMethod]
    public void TheHighNibbleIsTheRightBudAndTheLowTheLeftWhenTheFlipBitIsClear()
    {
        DecodedReading reading = Decode(Message(status: 0x00, batteryA: 0x82)); // high 8 (80%), low 2 (20%)

        Assert.AreEqual(80, reading.Right.Percent);
        Assert.AreEqual(20, reading.Left.Percent);
    }

    [TestMethod]
    public void StatusBitFiveSwapsTheSides()
    {
        DecodedReading reading = Decode(Message(status: 0x20, batteryA: 0x82));

        Assert.AreEqual(20, reading.Right.Percent);
        Assert.AreEqual(80, reading.Left.Percent);
    }

    // The other bits of the status byte are not read: they only ever matter through the flip.
    [TestMethod]
    public void NoOtherStatusBitMovesTheSides()
    {
        foreach (int bit in new[] { 0, 1, 2, 3, 4, 6, 7 })
        {
            DecodedReading reading = Decode(Message(status: (byte)(1 << bit), batteryA: 0x82));

            Assert.AreEqual(80, reading.Right.Percent, "Status bit " + bit);
            Assert.AreEqual(20, reading.Left.Percent, "Status bit " + bit);
        }
    }

    [TestMethod]
    public void TheCaseLevelIsTheLowNibbleOfTheSecondBatteryByteAndNeedsNoProof()
    {
        DecodedReading reading = Decode(Message(batteryB: 0x39)); // low nibble 9 = 90%

        Assert.AreEqual(90, reading.Case.Percent);
        Assert.AreEqual(At, reading.Case.ReadAt);
    }

    [TestMethod]
    public void ChargingBitsFollowTheirNibblesAndNotTheSides()
    {
        // Bit 6 the case, bit 5 the bud with the high nibble, bit 4 the bud with the low nibble.
        DecodedReading plain = Decode(Message(status: 0x00, batteryA: 0x82, batteryB: 0x45)); // case charging, nothing else
        Assert.AreEqual(true, plain.Case.Charging);
        Assert.AreEqual(false, plain.Right.Charging);
        Assert.AreEqual(false, plain.Left.Charging);

        DecodedReading highCharging = Decode(Message(status: 0x00, batteryA: 0x82, batteryB: 0x25));
        Assert.AreEqual(true, highCharging.Right.Charging, "Unflipped, the high nibble is the right bud and bit 5 goes with it.");
        Assert.AreEqual(false, highCharging.Left.Charging);

        DecodedReading flipped = Decode(Message(status: 0x20, batteryA: 0x82, batteryB: 0x25));
        Assert.AreEqual(true, flipped.Left.Charging, "Flipped, the high nibble is the left bud and bit 5 goes with it.");
        Assert.AreEqual(false, flipped.Right.Charging);

        DecodedReading lowCharging = Decode(Message(status: 0x20, batteryA: 0x82, batteryB: 0x15));
        Assert.AreEqual(true, lowCharging.Right.Charging);
        Assert.AreEqual(false, lowCharging.Left.Charging);
    }

    // Two senders of one set say the same thing with the nibbles swapped and bit 5 differing: both decode to the
    // same pair, so the card never alternates.
    [TestMethod]
    public void TheTwoBudsOfOneSetDecodeToTheSamePair()
    {
        // Left 60, right 50, both buds charging, case 100, case not charging.
        DecodedReading firstBud = Decode(Message(status: 0x00, batteryA: 0x56, batteryB: 0x3A));
        DecodedReading secondBud = Decode(Message(status: 0x20, batteryA: 0x65, batteryB: 0x3A));

        Assert.AreEqual(60, firstBud.Left.Percent);
        Assert.AreEqual(50, firstBud.Right.Percent);
        Assert.AreEqual(firstBud.Left.Percent, secondBud.Left.Percent);
        Assert.AreEqual(firstBud.Right.Percent, secondBud.Right.Percent);
        Assert.AreEqual(100, firstBud.Case.Percent);
        Assert.AreEqual(true, firstBud.Left.Charging);
        Assert.AreEqual(true, firstBud.Right.Charging);
        Assert.AreEqual(false, firstBud.Case.Charging);
        Assert.AreEqual(secondBud.Left.Charging, firstBud.Left.Charging);
        Assert.AreEqual(secondBud.Right.Charging, firstBud.Right.Charging);
    }

    // 0xF is not a level: no value, no read time, and no charging flag for that part.
    [TestMethod]
    public void AnUnknownNibbleGivesNoValueNoReadTimeAndNoChargingFlag()
    {
        DecodedReading reading = Decode(Message(batteryA: 0xF3, batteryB: 0x7F)); // right 0xF, case 0xF, all charging bits set

        Assert.IsNull(reading.Right.Percent);
        Assert.IsNull(reading.Right.ReadAt);
        Assert.IsNull(reading.Right.Charging, "A charging bit beside an unknown level is not read.");
        Assert.AreEqual(30, reading.Left.Percent);
        Assert.AreEqual(true, reading.Left.Charging);
        Assert.IsNull(reading.Case.Percent);
        Assert.IsNull(reading.Case.ReadAt);
        Assert.IsNull(reading.Case.Charging);
    }

    [TestMethod]
    [DataRow(0xB)]
    [DataRow(0xC)]
    [DataRow(0xD)]
    [DataRow(0xE)]
    public void ElevenToFourteenAreUnknownToo(int nibble)
    {
        DecodedReading reading = Decode(Message(batteryA: (byte)((nibble << 4) | nibble), batteryB: (byte)nibble));

        Assert.IsNull(reading.Left.Percent);
        Assert.IsNull(reading.Right.Percent);
        Assert.IsNull(reading.Case.Percent);
    }

    [TestMethod]
    public void ReadAtIsSetOnlyForAKnownPercent()
    {
        DecodedReading reading = Decode(Message(batteryA: 0xF3, batteryB: 0x07)); // right unknown, left and case known

        Assert.IsNull(reading.Right.ReadAt);
        Assert.AreEqual(At, reading.Left.ReadAt);
        Assert.AreEqual(At, reading.Case.ReadAt);
    }

    // Pins the field order of the furiousMAC proximity pairing notes (messages/proximity_pairing.md): prefix, model (2 bytes),
    // status, pods battery, charging and case, lid open counter, colour, suffix. Every byte of the 9-byte value is distinct,
    // so a shifted offset anywhere moves a field and fails here: the lid open counter is byte 6 of the value.
    [TestMethod]
    public void TheLidOpenCounterIsByteSixOfTheValueInTheFuriousMacFieldOrder()
    {
        byte[] value = new byte[ProximityParser.DocumentedLength];
        value[0] = ProximityParser.DocumentedPrefix;
        for (int i = 1; i < 9; i++)
        {
            value[i] = (byte)(i * 17); // eight different invented bytes
        }

        ProximityMessage m = ProximityParser.Parse(
            ProximityParser.AppleCompanyId, [ProximityParser.ProximityType, (byte)value.Length, .. value]).Message!.Value;

        Assert.AreEqual(value[1], m.ModelHigh);
        Assert.AreEqual(value[2], m.ModelLow);
        Assert.AreEqual(value[3], m.Status);
        Assert.AreEqual(value[4], m.BatteryA, "Pods battery nibbles: byte 4.");
        Assert.AreEqual(value[5], m.BatteryB, "Charging flags and the case level: byte 5.");
        Assert.AreEqual(value[6], m.Lid, "Lid open counter: byte 6.");
        Assert.AreEqual(value[7], m.Colour);
        Assert.AreEqual(value[8], m.Reserved);
        Assert.AreEqual(value[6], (byte)Decode(m).LidCounter!.Value);
    }

    [TestMethod]
    public void InEarAndALidBitAreNotDecodedByTheDocumentedTableButTheLidOpenCounterIs()
    {
        DecodedReading reading = Decode(Message(status: 0xFF, lid: 0xA7));

        Assert.IsNull(reading.Left.InEar);
        Assert.IsNull(reading.Right.InEar);
        Assert.IsNull(reading.Case.InEar);
        Assert.IsNull(reading.LidOpen, "No source documents a bit that says the lid is open.");
        Assert.AreEqual(0xA7, reading.LidCounter, "The whole byte after the charging and case byte is the lid open counter.");
    }

    [TestMethod]
    public void ATableWithInEarBitsDecodesThem()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Documented with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };

        DecodedReading reading = ProximityDecoder.Decode(Message(status: 0b0000_0001), table, At);

        Assert.AreEqual(true, reading.Left.InEar);
        Assert.AreEqual(false, reading.Right.InEar);
    }

    [TestMethod]
    public void ATableWithNoOrderDecodesNoBud()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Documented with { HighNibbleIsRight = null };

        DecodedReading reading = ProximityDecoder.Decode(Message(batteryA: 0x53, batteryB: 0x07), table, At);

        Assert.IsNull(reading.Left.Percent);
        Assert.IsNull(reading.Right.Percent);
        Assert.AreEqual(70, reading.Case.Percent);
    }

    [TestMethod]
    public void TheDocumentedTableIsTheOneConstantForTheSideRule()
    {
        ProximityDecodeTable t = ProximityDecodeTable.Documented;

        Assert.AreEqual(true, t.HighNibbleIsRight);
        Assert.AreEqual(5, t.FlipBit);
        Assert.IsTrue(t.FlipWhenSet);
        Assert.AreEqual(6, t.CaseChargingBit);
        Assert.AreEqual(5, t.RightChargingBit);
        Assert.AreEqual(4, t.LeftChargingBit);
        Assert.IsNull(t.LeftInEarBit);
        Assert.IsNull(t.RightInEarBit);
        Assert.IsNull(t.LidOpenBit);
        Assert.AreEqual((byte)0xFF, t.LidCounterMask);
    }
}
