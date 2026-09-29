namespace Earshot.Widget;

// How to turn the documented message's raw bytes into left, right, case, charging, in-ear and lid. Every
// entry is null (Unproved) until DecodeProof proves it from the owner's own set-up records; a bud field is
// never decoded from a guess. There is no constant table: the only tables that exist are Unproved and the
// ones DecodeProofStore derives from the records at run time.
public sealed record ProximityDecodeTable(
    bool? HighNibbleIsRight,   // BatteryA bits 7..4 are the right bud when true, the left when false
    int? FlipBit,              // a bit of Status that swaps the two nibbles' sides; null: no flip proved
    bool FlipWhenSet,          // the nibbles swap when FlipBit reads 1 (true) or 0 (false)
    int? CaseChargingBit,      // bits of BatteryB
    int? RightChargingBit,
    int? LeftChargingBit,
    int? LeftInEarBit,         // bits of Status
    int? RightInEarBit,
    bool InEarWhenSet,
    int? LidOpenBit,           // a bit of Lid that reads open, or null when the lid is a counter only
    byte? LidCounterMask,      // the bits of Lid that count lid opens, or null
    bool? CaseNibbleReadsOnlyWithLidOpen, // the paper's claim, once observed
    bool CaseNibbleDoubted = false)       // two of the owner's own set-ups contradict the documented case nibble
{
    public static ProximityDecodeTable Unproved => new(null, null, false, null, null, null, null, null, false, null, null, null);
}
