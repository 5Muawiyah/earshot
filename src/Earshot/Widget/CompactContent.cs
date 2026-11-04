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
// Glyph is set, else a text button showing Label, right-aligned on the last line.
internal sealed record CompactAction(string Label, char Glyph = '\0')
{
    public bool IsIcon => Glyph != '\0';
}

// How far a compact action has got. Idle: its glyph or label. Progress: a 16 px accent ring turning while the work runs.
// Done: a tick (E73E) for the time the design holds it.
internal enum CompactActionState { Idle, Progress, Done }

// What a compact row says. Glyph is '\0' when the row has none. Hold is how long the row stays once its entrance has finished,
// or null for a row that stays until it is closed. Kind says what the row is about, for the arbiter.
internal sealed record CompactContent(char Glyph, InkRole Ink, string Text, CompactAction? Action, TimeSpan? Hold)
{
    public UnaskedKind Kind { get; init; } = UnaskedKind.Notice;
}
