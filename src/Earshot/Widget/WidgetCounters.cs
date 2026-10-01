namespace Earshot.Widget;

// Everything the widget saw, as numbers only: never an address, a per-run tag, a payload byte or a device
// name. UnknownForms keeps at most 16 distinct (prefix, length) shapes, so a device sending many different
// malformed forms cannot grow the snapshot without bound.
//
// AllSections counts manufacturer-data sections, not advertisement packets: the source raises one Received
// per section (WinRtAdvertisementSource.OnReceived), and one BLE advertisement can carry more than one, so a
// single packet with two manufacturer-data sections counts as two here, not one.
//
// After OkForm, a documented-form message lands in exactly one of these while a paired model is known:
//   ModelMismatch   another model than the paired AirPods'
//   ColourMismatch  the paired model, another colour than the chosen set's
//   OtherSet        the paired model and colour, from a set that is not the chosen one
//   Chosen          a message of the chosen set: the values shown come from these
// and a message heard before the first choice is in none. NoPairedModel counts documented-form messages heard
// while no paired model is known. Switches counts the times another set took over. Sets is how many sets were in
// range at the last message, not a running total. BudOrderDisagree counts messages whose other sender of the
// chosen set, heard within two seconds, decoded to a different left and right: it should stay 0.
public sealed record WidgetCounters(
    long AllSections, long AppleSections, long OtherCompanySections,
    long ProximityItems, long OkForm, long Truncated, long UnknownForm,
    long ModelMismatch, long ColourMismatch, long OtherSet, long Chosen, long NoPairedModel,
    long BudOrderDisagree, long Switches, long Sets,
    IReadOnlyList<(byte? Prefix, int Length, long Count)> UnknownForms)
{
    public const int MaxUnknownFormShapes = 16;

    public static WidgetCounters Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<(byte?, int, long)>());
}
