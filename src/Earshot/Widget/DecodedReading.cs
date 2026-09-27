namespace Earshot.Widget;

// One bud or the case, as the widget shows it: the last known percent (never expires), whether it is
// charging, and (for a bud) whether it is in the ear. Percent is null when there is no proved way to read
// it yet; ReadAt is set only when Percent is, so the card's "read at" line is never newer than a value it
// is not showing.
public sealed record PartReading(int? Percent, bool? Charging, bool? InEar)
{
    public DateTimeOffset? ReadAt { get; init; }

    public static PartReading Unknown => new(null, null, null);
}

// What ProximityDecoder.Decode read out of one documented-form message, subject to the decode table it
// was given. LidOpen and LidCounter are null unless the table proves the corresponding bit or mask.
public sealed record DecodedReading(PartReading Left, PartReading Right, PartReading Case, bool? LidOpen, int? LidCounter);
