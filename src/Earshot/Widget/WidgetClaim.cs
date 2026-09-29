namespace Earshot.Widget;

// The last reading a claim was made or re-synced from. Raw nibbles, 0 to 10, or null when that part was
// never read. Before the decode table proves the bud order, NibbleHigh and NibbleLow hold BatteryA's wire
// high and low nibble exactly as read, since there is no bud identity to give them yet; once the order is
// proved, OwnershipRule keeps writing to the same two slots but by name instead of by wire position
// (NibbleHigh for the right bud, NibbleLow for the left), so a reading taken after the primary bud changed
// still lines up against the correct named part.
public sealed record OwnedBattery(int? NibbleHigh, int? NibbleLow, int? Case, DateTimeOffset AtUtc)
{
    // Reads the two bud nibbles and the case nibble out of one documented-form message, using the wire
    // position (order unproved) or named-part (order proved) convention the class comment above describes,
    // and keeps the last known value for anything this message did not read. previous is null for a first
    // claim, when there is nothing yet to keep.
    internal static OwnedBattery FromMessage(ProximityMessage m, ProximityDecodeTable t, OwnedBattery? previous, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(t);

        bool budsProved = t.HighNibbleIsRight is not null;
        DecodedReading reading = ProximityDecoder.Decode(m, t, at);
        int? wireHigh = KnownNibble((m.BatteryA >> 4) & 0x0F);
        int? wireLow = KnownNibble(m.BatteryA & 0x0F);
        int? left = budsProved ? PercentToNibble(reading.Left.Percent) : null;
        int? right = budsProved ? PercentToNibble(reading.Right.Percent) : null;
        int? currentCase = PercentToNibble(reading.Case.Percent);

        int? high = budsProved ? right ?? previous?.NibbleHigh : wireHigh ?? previous?.NibbleHigh;
        int? low = budsProved ? left ?? previous?.NibbleLow : wireLow ?? previous?.NibbleLow;
        int? updatedCase = currentCase ?? previous?.Case;
        return new OwnedBattery(high, low, updatedCase, at);
    }

    private static int? PercentToNibble(int? percent) => percent is int p ? p / 10 : null;

    private static int? KnownNibble(int nibble) => nibble is >= 0 and <= 10 ? nibble : null;
}

// %LOCALAPPDATA%\Earshot\widget\claim.json. The device the owner told Earshot is his: what a battery set-up
// read once, kept here rather than in settings.json because it is a device-derived record and not a
// preference, and never roamed.
//
// SchemaVersion 2 carries the signal figures the set-up measured (the threshold is derived from them, never
// a constant) and the name of the set-up record it was made from. A version 1 file is not used and is left
// where it is.
//
// NibblesAreNamedOrder records which of the two conventions OwnedBattery.FromMessage's comment above
// describes was used to write Last.NibbleHigh/NibbleLow: false when they are BatteryA's wire high and low
// nibble (the order was unproved at the time), true when they are the right and left bud by name (the
// order was proved). When the decode table's own current provedness no longer matches this flag, the stored
// nibbles cannot be told apart from a stranger's without redoing the set-up, so OwnershipRule fails closed
// rather than guess which they are.
public sealed record WidgetClaim(
    int SchemaVersion,            // 2
    byte ModelHigh, byte ModelLow,
    byte Colour,
    sbyte SignalThresholdDbm,     // the weakest signal the set-up saw, less its margin
    sbyte SignalMinDbm,
    sbyte SignalMedianDbm,
    sbyte SignalMaxDbm,
    int SignalSamples,            // the messages those three figures were taken from
    string SetupRecord,           // the set-up record's file name, no path
    DateTimeOffset ClaimedAtUtc,
    OwnedBattery Last,            // the reading the claim was made from, then the last owned reading
    bool NibblesAreNamedOrder = false)
{
    public const int CurrentSchemaVersion = 2;
}
