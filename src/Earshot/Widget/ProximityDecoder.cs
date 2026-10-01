namespace Earshot.Widget;

// Turns one documented-form message into a decoded reading, subject to the table it is given. Pure: no clock
// read here, "at" is handed in.
//
// A nibble that is not a level (0xF, or 11 to 14) gives no value for that part: no percent, no read time and no
// charging flag, so a caller keeps whatever it held for that part with that part's own read time.
public static class ProximityDecoder
{
    public static DecodedReading Decode(ProximityMessage m, ProximityDecodeTable t, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(t);

        int? casePercent = BatteryNibble.ToPercent(m.BatteryB & 0x0F);
        PartReading caseReading = new PartReading(casePercent, casePercent is null ? null : ChargingBit(m, t.CaseChargingBit), InEar: null)
        {
            ReadAt = casePercent is not null ? at : null,
        };

        (int? leftNibble, int? rightNibble, bool flipped) = SplitBudNibbles(m, t);
        int? leftPercent = leftNibble is int ln ? BatteryNibble.ToPercent(ln) : null;
        int? rightPercent = rightNibble is int rn ? BatteryNibble.ToPercent(rn) : null;

        // The charging bits pair with the nibbles, so when the flip bit swaps the nibbles' sides the two bits swap
        // with them.
        int? leftChargingBit = flipped ? t.RightChargingBit : t.LeftChargingBit;
        int? rightChargingBit = flipped ? t.LeftChargingBit : t.RightChargingBit;

        PartReading left = new PartReading(leftPercent, leftPercent is null ? null : ChargingBit(m, leftChargingBit), InEarBit(m, t.LeftInEarBit, t.InEarWhenSet))
        {
            ReadAt = leftPercent is not null ? at : null,
        };
        PartReading right = new PartReading(rightPercent, rightPercent is null ? null : ChargingBit(m, rightChargingBit), InEarBit(m, t.RightInEarBit, t.InEarWhenSet))
        {
            ReadAt = rightPercent is not null ? at : null,
        };

        bool? lidOpen = t.LidOpenBit is int openBit ? IsBitSet(m.Lid, openBit) : null;
        int? lidCounter = t.LidCounterMask is byte mask ? m.Lid & mask : null;

        return new DecodedReading(left, right, caseReading, lidOpen, lidCounter);
    }

    // The high nibble of BatteryA goes right when HighNibbleIsRight is true, else left; a flip bit that reads
    // FlipWhenSet swaps that assignment for this one message. Null, null when the order is not set at all: an
    // unset order never decodes a bud percent, even though the raw nibbles are there. Flipped says the swap
    // applied, so the charging bits can follow the nibbles.
    private static (int? Left, int? Right, bool Flipped) SplitBudNibbles(ProximityMessage m, ProximityDecodeTable t)
    {
        if (t.HighNibbleIsRight is not bool highIsRight)
        {
            return (null, null, false);
        }

        int high = (m.BatteryA >> 4) & 0x0F;
        int low = m.BatteryA & 0x0F;
        bool flipped = t.FlipBit is int flipBit && IsBitSet(m.Status, flipBit) == t.FlipWhenSet;
        bool highIsRightNow = highIsRight ^ flipped;

        return highIsRightNow ? (low, high, flipped) : (high, low, flipped);
    }

    private static bool? ChargingBit(ProximityMessage m, int? bit) => bit is int b ? IsBitSet(m.BatteryB, b) : null;

    private static bool? InEarBit(ProximityMessage m, int? bit, bool whenSet) => bit is int b ? IsBitSet(m.Status, b) == whenSet : null;

    private static bool IsBitSet(byte value, int bit) => ((value >> bit) & 1) == 1;
}
