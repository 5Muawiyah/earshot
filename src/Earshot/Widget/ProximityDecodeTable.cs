namespace Earshot.Widget;

// How to turn the documented message's raw bytes into left, right, case, charging, in-ear and lid. Every
// entry ships null (Unproved) until the phase 0 sitting proves it from the owner's own AirPods; a bud
// field is never decoded from a guess.
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
    bool? CaseNibbleReadsOnlyWithLidOpen) // the paper's claim, once observed
{
    public static ProximityDecodeTable Unproved => new(null, null, false, null, null, null, null, null, false, null, null, null);

    // Replaced by the phase 0 analysis with the proved values; Unproved until then.
    public static ProximityDecodeTable Current => Unproved;
}
