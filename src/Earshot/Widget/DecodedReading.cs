namespace Earshot.Widget;

// One bud or the case, as the widget shows it: the last known percent, whether it is charging, and (for a bud)
// whether it is in the ear. Percent is null when the part has no value; ReadAt is set only when Percent is, so
// the card's "read at" line is never newer than a value it is not showing. How old a value may be before it is
// drawn greyed, or dropped by the gauge, is BatteryFreshness's rule, not this record's.
public sealed record PartReading(int? Percent, bool? Charging, bool? InEar)
{
    public DateTimeOffset? ReadAt { get; init; }

    public static PartReading Unknown => new(null, null, null);
}

// What ProximityDecoder.Decode read out of one documented-form message, subject to the decode table it
// was given. LidOpen and LidCounter are null unless the table sets the corresponding bit or mask.
public sealed record DecodedReading(PartReading Left, PartReading Right, PartReading Case, bool? LidOpen, int? LidCounter);
