namespace Earshot.Widget;

// Everything the widget saw, as numbers only: never an address, a per-run tag, a payload byte or a device
// name. UnknownForms keeps at most 16 distinct (prefix, length) shapes, so a device sending many different
// malformed forms cannot grow the snapshot without bound.
//
// AllSections counts manufacturer-data sections, not advertisement packets: the source raises one Received
// per section (WinRtAdvertisementSource.OnReceived), and one BLE advertisement can carry more than one, so a
// single packet with two manufacturer-data sections counts as two here, not one.
public sealed record WidgetCounters(
    long AllSections, long AppleSections, long OtherCompanySections,
    long ProximityItems, long OkForm, long Truncated, long UnknownForm,
    long Owned, long OwnedByLiveConnection, long NoClaim, long ModelOrColourMismatch,
    long SignalBelowThreshold, long BatteryUnreadable, long BatteryInconsistent, long AmbiguousCandidates,
    IReadOnlyList<(byte? Prefix, int Length, long Count)> UnknownForms)
{
    public const int MaxUnknownFormShapes = 16;

    public static WidgetCounters Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<(byte?, int, long)>());
}
