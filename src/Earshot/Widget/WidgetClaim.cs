namespace Earshot.Widget;

// The last reading a claim was made or re-synced from. Raw nibbles, 0 to 10, or null when that part was
// never read. Before the decode table proves the bud order, NibbleHigh and NibbleLow hold BatteryA's wire
// high and low nibble exactly as read, since there is no bud identity to give them yet; once the order is
// proved, OwnershipRule keeps writing to the same two slots but by name instead of by wire position
// (NibbleHigh for the right bud, NibbleLow for the left), so a reading taken after the primary bud changed
// still lines up against the correct named part.
public sealed record OwnedBattery(int? NibbleHigh, int? NibbleLow, int? Case, DateTimeOffset AtUtc);

// %LOCALAPPDATA%\Earshot\widget\claim.json. The device the owner told Earshot is his: what a claiming run
// read once, kept here rather than in settings.json because it is a device-derived record and not a
// preference, and never roamed.
public sealed record WidgetClaim(
    int SchemaVersion,            // 1
    byte ModelHigh, byte ModelLow,
    byte Colour,
    sbyte SignalThresholdDbm,     // copied from WidgetDefaults.SignalThresholdDbm at claim time
    DateTimeOffset ClaimedAtUtc,
    OwnedBattery Last);           // the reading the claim was made from, then the last owned reading
