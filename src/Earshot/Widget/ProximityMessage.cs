namespace Earshot.Widget;

// The furiousMAC Continuity notes and the Celosia and Cunche paper describe one 25-byte form of the
// Apple 0x07 proximity-pairing message; every other 0x07 form, the 17-byte 0x06 form the phase 0 probe
// saw included, is not decoded.
public enum ProximityParseStatus
{
    Ok,            // a 0x07 item with prefix 0x01 and length 25: every field read
    WrongCompany,  // the section is not Apple's (0x004C); nothing else is read
    WrongType,     // Apple data with no 0x07 item; the item types seen are counted
    Truncated,     // a header cut short, an item running past the data, or prefix 0x01 shorter than 25
    UnknownForm    // a 0x07 item of any other prefix or length; the 17-byte 0x06 form lands here
}

// The documented fields, raw. Nothing here says which nibble is which bud or what a bit means: that is
// ProximityDecodeTable's job.
public readonly record struct ProximityMessage(
    byte ModelHigh, byte ModelLow,  // the two model bytes in wire order, not in order of significance
    byte Status,
    byte BatteryA,                  // the two bud nibbles
    byte BatteryB,                  // charging flags and the case nibble
    byte Lid,
    byte Colour,
    byte Reserved)                  // the documented 0x00, recorded as read
{
    // The model as the number Windows holds as the device's product id. The first wire byte is the low byte:
    // bytes 1 and 2 of the value read 0x27 then 0x20 for the product id 0x2027. ModelHigh and ModelLow name the
    // first and second position on the wire, not which byte is the more significant.
    [System.Text.Json.Serialization.JsonIgnore]
    public ushort Model => (ushort)(ModelHigh | (ModelLow << 8));
}

public sealed record ProximityParse(
    ProximityParseStatus Status,
    ProximityMessage? Message,      // set for Ok only
    byte? Prefix,                   // set for UnknownForm and Truncated when a prefix byte exists
    int? Length,                    // the 0x07 item's declared length, for UnknownForm and Truncated
    int ProximityItems,             // 0x07 items in this section, however formed
    IReadOnlyList<byte> TypesSeen); // Apple item types met before the decision, for the counters
