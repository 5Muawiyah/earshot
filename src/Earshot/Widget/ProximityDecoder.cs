namespace Earshot.Widget;

// Turns one documented-form message into a decoded reading, subject to the proved parts of the table.
// Pure: no clock read here, "at" is handed in.
public static class ProximityDecoder
{
    public static DecodedReading Decode(ProximityMessage m, ProximityDecodeTable t, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(t);

        // A case nibble the owner's own set-ups contradict is not shown, compared or charged: null percent,
        // null charging and no read time, the same as a case that was never read.
        int? casePercent = t.CaseNibbleDoubted ? null : BatteryNibble.ToPercent(m.BatteryB & 0x0F);
        PartReading caseReading = new PartReading(casePercent, t.CaseNibbleDoubted ? null : ChargingBit(m, t.CaseChargingBit), InEar: null)
        {
            ReadAt = casePercent is not null ? at : null,
        };

        (int? leftNibble, int? rightNibble) = SplitBudNibbles(m, t);
        int? leftPercent = leftNibble is int ln ? BatteryNibble.ToPercent(ln) : null;
        int? rightPercent = rightNibble is int rn ? BatteryNibble.ToPercent(rn) : null;

        PartReading left = new PartReading(leftPercent, ChargingBit(m, t.LeftChargingBit), InEarBit(m, t.LeftInEarBit, t.InEarWhenSet))
        {
            ReadAt = leftPercent is not null ? at : null,
        };
        PartReading right = new PartReading(rightPercent, ChargingBit(m, t.RightChargingBit), InEarBit(m, t.RightInEarBit, t.InEarWhenSet))
        {
            ReadAt = rightPercent is not null ? at : null,
        };

        bool? lidOpen = t.LidOpenBit is int openBit ? IsBitSet(m.Lid, openBit) : null;
        int? lidCounter = t.LidCounterMask is byte mask ? m.Lid & mask : null;

        return new DecodedReading(left, right, caseReading, lidOpen, lidCounter);
    }

    // The high nibble of BatteryA goes right when HighNibbleIsRight is true, else left; a proved flip bit
    // that reads FlipWhenSet swaps that assignment for this one message. Null, null when the order is not
    // proved at all: an unproved order never decodes a bud percent, even though the raw nibbles are there.
    private static (int? Left, int? Right) SplitBudNibbles(ProximityMessage m, ProximityDecodeTable t)
    {
        if (t.HighNibbleIsRight is not bool highIsRight)
        {
            return (null, null);
        }

        int high = (m.BatteryA >> 4) & 0x0F;
        int low = m.BatteryA & 0x0F;
        bool flipped = t.FlipBit is int flipBit && IsBitSet(m.Status, flipBit) == t.FlipWhenSet;
        bool highIsRightNow = highIsRight ^ flipped;

        return highIsRightNow ? (low, high) : (high, low);
    }

    private static bool? ChargingBit(ProximityMessage m, int? bit) => bit is int b ? IsBitSet(m.BatteryB, b) : null;

    private static bool? InEarBit(ProximityMessage m, int? bit, bool whenSet) => bit is int b ? IsBitSet(m.Status, b) == whenSet : null;

    private static bool IsBitSet(byte value, int bit) => ((value >> bit) & 1) == 1;
}
