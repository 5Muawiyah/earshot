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

    [TestMethod]
    public void WithTheUnprovedTableOnlyTheCaseNibbleIsDecoded()
    {
        ProximityMessage m = Message(batteryA: 0x53, batteryB: 0x07, lid: 0x02);

        DecodedReading reading = ProximityDecoder.Decode(m, ProximityDecodeTable.Unproved, At);

        Assert.IsNull(reading.Left.Percent);
        Assert.IsNull(reading.Left.Charging);
        Assert.IsNull(reading.Left.InEar);
        Assert.IsNull(reading.Right.Percent);
        Assert.IsNull(reading.Right.Charging);
        Assert.IsNull(reading.Right.InEar);
        Assert.AreEqual(70, reading.Case.Percent);
        Assert.IsNull(reading.Case.Charging);
        Assert.IsNull(reading.LidOpen);
        Assert.IsNull(reading.LidCounter);
    }

    [TestMethod]
    public void TheCaseNibbleNeedsNoTable()
    {
        ProximityMessage m = Message(batteryB: 0x39); // low nibble 0x9 = 90%

        DecodedReading reading = ProximityDecoder.Decode(m, ProximityDecodeTable.Unproved, At);

        Assert.AreEqual(90, reading.Case.Percent);
        Assert.AreEqual(At, reading.Case.ReadAt);
    }

    [TestMethod]
    public void WithHighNibbleIsRightProvedTheNibblesLandOnTheNamedBuds()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        ProximityMessage m = Message(batteryA: 0x82); // high nibble 8 (80%), low nibble 2 (20%)

        DecodedReading reading = ProximityDecoder.Decode(m, table, At);

        Assert.AreEqual(80, reading.Right.Percent);
        Assert.AreEqual(20, reading.Left.Percent);
    }

    [TestMethod]
    public void WithAFlipBitProvedTheSidesSwapWhenItReads()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true, FlipBit = 0, FlipWhenSet = true };
        ProximityMessage notFlipped = Message(status: 0x00, batteryA: 0x82);
        ProximityMessage flipped = Message(status: 0x01, batteryA: 0x82);

        DecodedReading notFlippedReading = ProximityDecoder.Decode(notFlipped, table, At);
        DecodedReading flippedReading = ProximityDecoder.Decode(flipped, table, At);

        Assert.AreEqual(80, notFlippedReading.Right.Percent);
        Assert.AreEqual(20, notFlippedReading.Left.Percent);
        Assert.AreEqual(20, flippedReading.Right.Percent);
        Assert.AreEqual(80, flippedReading.Left.Percent);
    }

    [TestMethod]
    public void ChargingBitsFollowTheTable()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { CaseChargingBit = 0, RightChargingBit = 1, LeftChargingBit = 2 };
        ProximityMessage m = Message(batteryB: 0b0000_0101); // bits 0 and 2 set: case and left charging

        DecodedReading reading = ProximityDecoder.Decode(m, table, At);

        Assert.AreEqual(true, reading.Case.Charging);
        Assert.AreEqual(false, reading.Right.Charging);
        Assert.AreEqual(true, reading.Left.Charging);
    }

    [TestMethod]
    public void InEarBitsFollowTheTable()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { LeftInEarBit = 0, RightInEarBit = 1, InEarWhenSet = true };
        ProximityMessage m = Message(status: 0b0000_0001); // left bit set, right bit clear

        DecodedReading reading = ProximityDecoder.Decode(m, table, At);

        Assert.AreEqual(true, reading.Left.InEar);
        Assert.AreEqual(false, reading.Right.InEar);
    }

    [TestMethod]
    public void LidOpenBitAndCounterFollowTheTable()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { LidOpenBit = 3, LidCounterMask = 0x0F };
        ProximityMessage open = Message(lid: 0b0000_1101); // bit 3 set, counter nibble 0xD
        ProximityMessage closed = Message(lid: 0b0000_0101); // bit 3 clear, counter nibble 0x5

        DecodedReading openReading = ProximityDecoder.Decode(open, table, At);
        DecodedReading closedReading = ProximityDecoder.Decode(closed, table, At);

        Assert.AreEqual(true, openReading.LidOpen);
        Assert.AreEqual(0x0D, openReading.LidCounter);
        Assert.AreEqual(false, closedReading.LidOpen);
        Assert.AreEqual(0x05, closedReading.LidCounter);
    }

    [TestMethod]
    public void AnUnknownNibbleStaysUnknownWhateverTheTable()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        ProximityMessage m = Message(batteryA: 0xF3, batteryB: 0x0F); // right nibble 0xF, case nibble 0xF

        DecodedReading reading = ProximityDecoder.Decode(m, table, At);

        Assert.IsNull(reading.Right.Percent);
        Assert.AreEqual(30, reading.Left.Percent);
        Assert.IsNull(reading.Case.Percent);
    }

    [TestMethod]
    public void ReadAtIsSetOnlyForAKnownPercent()
    {
        ProximityDecodeTable table = ProximityDecodeTable.Unproved with { HighNibbleIsRight = true };
        ProximityMessage m = Message(batteryA: 0xF3, batteryB: 0x07); // right unknown, left known, case known

        DecodedReading reading = ProximityDecoder.Decode(m, table, At);

        Assert.IsNull(reading.Right.ReadAt);
        Assert.AreEqual(At, reading.Left.ReadAt);
        Assert.AreEqual(At, reading.Case.ReadAt);
    }

    // The owner's own set-ups contradicted the documented case nibble twice with nothing in its favour: it
    // decodes to no case at all, as if it had never been read.
    [TestMethod]
    public void ADoubtedCaseDecodesNoCase()
    {
        ProximityMessage m = Message(batteryA: 0x53, batteryB: 0x37);
        ProximityDecodeTable doubted = ProximityDecodeTable.Unproved with { CaseNibbleDoubted = true, CaseChargingBit = 4 };

        DecodedReading reading = ProximityDecoder.Decode(m, doubted, At);

        Assert.IsNull(reading.Case.Percent);
        Assert.IsNull(reading.Case.Charging, "A doubted case is not shown as charging either.");
        Assert.IsNull(reading.Case.ReadAt, "No read time for a value that is not shown.");
        Assert.AreEqual(70, ProximityDecoder.Decode(m, ProximityDecodeTable.Unproved with { CaseChargingBit = 4 }, At).Case.Percent, "Sanity: undoubted, the same message reads 70.");
    }
}
