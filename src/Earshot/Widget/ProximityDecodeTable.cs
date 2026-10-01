namespace Earshot.Widget;

// How to turn the documented message's raw bytes into left, right, case, charging, in-ear and lid. Documented
// is the one table the running program decodes with; the record shape stays so a test can hand the decoder a
// table with other bits set (for instance in-ear bits).
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
    bool CaseNibbleProved = false)        // the case nibble may be read
{
    public static ProximityDecodeTable Unproved => new(null, null, false, null, null, null, null, null, false, null, null, null);

    // The documented 25-byte form, as the permitted sources and the saved capture settle it.
    //
    // Bud nibbles (byte 4): the high nibble is the right bud and the low nibble the left, in the message's own
    // frame (furiousMAC Continuity notes, github.com/furiousMAC/continuity, messages/proximity_pairing.md).
    // Bit 5 of the status byte swaps the two, and the two charging bits with them. No permitted source
    // documents that bit: it is the local reading of the saved capture, where the two senders of one pair always
    // differ by exactly bits 5 and 6 of the status byte and carry the two nibbles swapped. The capture cannot
    // say which of those two bits is the flip, so the polarity, and with it which physical side a figure
    // belongs to, is unproved. Bit 5 clear reads as furiousMAC writes it; either candidate bit gives the same
    // steady pair for one set's two senders. The polarity lives here and nowhere else.
    //
    // Charging (byte 5): bit 6 the case, bit 5 the bud paired with the high nibble, bit 4 the bud paired with
    // the low nibble; the case level is the low nibble of the same byte (Celosia and Cunche, "Discontinued
    // Privacy", PETS 2020, petsymposium.org/popets/2020/popets-2020-0003.pdf, Fig. 5, bit positions read from
    // the figure's labels).
    //
    // In-ear and the lid: in neither source and in no saved record, so not decoded.
    public static ProximityDecodeTable Documented { get; } = new(
        HighNibbleIsRight: true,
        FlipBit: 5,
        FlipWhenSet: true,
        CaseChargingBit: 6,
        RightChargingBit: 5,
        LeftChargingBit: 4,
        LeftInEarBit: null,
        RightInEarBit: null,
        InEarWhenSet: false,
        LidOpenBit: null,
        LidCounterMask: null,
        CaseNibbleReadsOnlyWithLidOpen: null,
        CaseNibbleProved: true);
}
