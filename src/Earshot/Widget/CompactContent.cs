using IconId = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Widget;

// Who asked for a card. An asked card is the gauge click or a shortcut: activatable, takes focus and keys. An unasked one
// (the case opening, a moved-to notice, a failure with a next step) is created with WS_EX_NOACTIVATE and never takes focus.
internal enum CardOpening { Asked, Unasked }

// The two forms of the one card: the full card, and the compact one-row form (the same window, folded).
internal enum CardForm { Full, Compact }

// The ink a compact row's glyph is drawn in: the card's primary ink, the accent, or caution. Under high contrast every one of
// them is the window text colour, as the rest of the card maps them.
internal enum InkRole { Primary, Accent, Caution }

// What an unasked card is about. A lower number outranks a higher one: the case opening wins over everything, then a connect
// failure (it is the result of something the owner did), then moved to, the low and charged fallbacks, and a plain notice.
internal enum UnaskedKind
{
    CaseOpened = 1,
    ConnectFailed = 2,
    MovedTo = 3,
    LowFallback = 4,
    ChargedFallback = 5,
    Notice = 6,
}

// The button a compact row can carry: an icon button (a glyph, 32 x 32 at 100% text, named and given a tooltip by Label) when
// Glyph is not null, else a text button showing Label, right-aligned on the last line. RunningLabel is what a screen reader calls the
// button while its work runs ("Taking back").
internal sealed record CompactAction(string Label, IconId? Glyph = null, string? RunningLabel = null)
{
    public bool IsIcon => Glyph is not null;
}

// How far a compact action has got. Idle: its glyph or label. Progress: a 16 px accent ring turning while the work runs.
// Done: a tick for the time the design holds it.
internal enum CompactActionState { Idle, Progress, Done }

// What a compact row says. Glyph is null when the row has none. Hold is how long the row stays once its entrance has finished,
// or null for a row that stays until it is closed. Kind says what the row is about, for the arbiter.
internal sealed record CompactContent(IconId? Glyph, InkRole Ink, string Text, CompactAction? Action, TimeSpan? Hold)
{
    public UnaskedKind Kind { get; init; } = UnaskedKind.Notice;
}

// The compact rows the design has, from the owner's label for the other device and from the reading that crossed. Each holds six
// seconds once its entrance has finished (OneCardArbiter.CompactHold); only moved to carries a button.
internal static class CompactContents
{
    // The phone and "Moved to iPhone"; with the label empty, the device icon and "Moved to another device". The button is the computer,
    // "Take back".
    public static CompactContent MovedTo(string otherDeviceLabel) => new(
        string.IsNullOrWhiteSpace(otherDeviceLabel) ? IconId.Device : IconId.Cellphone,
        InkRole.Primary,
        WidgetCopy.MovedTo(otherDeviceLabel),
        new CompactAction(WidgetCopy.TakeBack, IconId.Computer, WidgetCopy.TakingBack),
        OneCardArbiter.CompactHold)
    {
        Kind = UnaskedKind.MovedTo,
    };

    // The alert triangle in caution and the part that crossed with its number.
    public static CompactContent LowBattery(string partsLabel, int percent) => new(
        IconId.Alert, InkRole.Caution, WidgetCopy.LowBatteryCompact(partsLabel, percent), null, OneCardArbiter.CompactHold)
    {
        Kind = UnaskedKind.LowFallback,
    };

    // A tick in the accent and "Fully charged".
    public static CompactContent FullyCharged() => new(
        IconId.Check, InkRole.Accent, WidgetCopy.FullyChargedCompact, null, OneCardArbiter.CompactHold)
    {
        Kind = UnaskedKind.ChargedFallback,
    };
}
