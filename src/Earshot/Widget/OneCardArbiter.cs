namespace Earshot.Widget;

// What is on screen, as the arbiter sees it. A card on its way out is not on screen: it answers no press (WidgetCard ignores a
// mouse or key press, and the compact button's screen reader press, while it fades), so nothing can happen to it, and a new card opens
// as if none were showing.
internal enum CardShowing
{
    Nothing,
    AskedMain,
    AskedSubPage,
    UnaskedFull,
    CompactHolding,
}

// Kind is what an unasked or compact card is about; null for the asked card and for nothing.
internal readonly record struct CardState(CardShowing Showing, UnaskedKind? Kind = null);

internal enum CardEventKind
{
    // Something wants to open an unasked card (About says what; HasAction and StaysUp describe a notice).
    Unasked,

    // The owner's gauge click or shortcut asks for the card.
    AskedOpen,

    // A click on the compact card's body, not on its button.
    BodyClick,

    PointerOver,
    PointerLeave,

    // A Connect begins, from the compact card's own button or from anywhere else (shortcut, gauge click, the full card).
    ConnectOwnButton,
    ConnectElsewhere,
}

internal readonly record struct CardEvent(CardEventKind Kind, UnaskedKind? About = null, bool HasAction = false, bool StaysUp = false)
{
    public static CardEvent Opens(UnaskedKind about, bool hasAction = false, bool staysUp = false) => new(CardEventKind.Unasked, about, hasAction, staysUp);
}

// What the card does about an event.
internal enum CardDoes
{
    // Nothing is shown and nothing is queued: the event is over.
    Nothing,

    // The card already open takes the new values; nothing new opens.
    UpdateValuesOnly,

    // A notice shown in the asked card's status row, or in a one-row strip at the foot of a sub-page.
    StatusRowCarries,
    FooterStrip,

    CloseUnaskedOpenAsked,
    MorphToFull,
    GrowToFull,
    PauseHold,
    ResumeHold,
    TakeBack,
    FoldNow,
    StayToHold,
    ReplaceInPlace,

    // Held in the one slot of its kind; shown after the current card only if it is still true then.
    WaitInSlot,

    // Opens, subject to the gates.
    Open,
}

// Hold is how long what the decision shows stays (null: until it is closed). LogRefusal: the refusal is logged.
internal readonly record struct CardDecision(CardDoes Does, TimeSpan? Hold = null, bool LogRefusal = false);

// Which card shows, and what it does, when two things want the one card. Pure: it takes the state and an event and returns what
// to do; it holds no window and keeps no state. Written from the design's "What wins" and the one card spec's table (5.2).
internal static class OneCardArbiter
{
    // Design: 6000 ms, counted once the entrance finishes.
    public static readonly TimeSpan CompactHold = TimeSpan.FromMilliseconds(6000);

    // A notice with a button stays 12 s: the rule the notices have always had.
    public static readonly TimeSpan ActionHold = TimeSpan.FromSeconds(12);

    // How long a notice stays: 6 s, 12 s with a button, until closed when it stays up.
    public static TimeSpan? NoticeHold(bool hasAction, bool staysUp) => staysUp ? null : hasAction ? ActionHold : CompactHold;

    // Whether a card about incoming may replace one about current in place: a higher or equal priority, a lower number.
    public static bool OutranksOrEquals(UnaskedKind incoming, UnaskedKind current) => incoming <= current;

    public static CardDecision Decide(CardState state, CardEvent ev)
    {
        switch (state.Showing)
        {
            case CardShowing.Nothing:
                return ev.Kind is CardEventKind.Unasked or CardEventKind.AskedOpen
                    ? new CardDecision(CardDoes.Open, ev.Kind == CardEventKind.Unasked ? HoldFor(ev) : null)
                    : new CardDecision(CardDoes.Nothing);

            case CardShowing.AskedMain:
            case CardShowing.AskedSubPage:
                return OnAsked(state.Showing == CardShowing.AskedSubPage, ev);

            case CardShowing.UnaskedFull:
                return OnUnaskedFull(state, ev);

            case CardShowing.CompactHolding:
                return OnCompact(state, ev);

            default:
                return new CardDecision(CardDoes.Nothing);
        }
    }

    private static TimeSpan? HoldFor(CardEvent ev) => ev.About switch
    {
        UnaskedKind.Notice => NoticeHold(ev.HasAction, ev.StaysUp),
        UnaskedKind.CaseOpened or UnaskedKind.ConnectFailed => null,
        _ => CompactHold,
    };

    private static CardDecision OnAsked(bool subPage, CardEvent ev)
    {
        if (ev.Kind != CardEventKind.Unasked)
        {
            return new CardDecision(CardDoes.Nothing);
        }

        return ev.About switch
        {
            UnaskedKind.CaseOpened => new CardDecision(CardDoes.UpdateValuesOnly, LogRefusal: true),
            UnaskedKind.Notice => new CardDecision(subPage ? CardDoes.FooterStrip : CardDoes.StatusRowCarries, NoticeHold(ev.HasAction, ev.StaysUp)),
            _ => new CardDecision(CardDoes.Nothing),
        };
    }

    private static CardDecision OnUnaskedFull(CardState state, CardEvent ev)
    {
        if (ev.Kind == CardEventKind.AskedOpen)
        {
            return new CardDecision(CardDoes.CloseUnaskedOpenAsked);
        }

        if (ev.Kind != CardEventKind.Unasked || ev.About is not { } about)
        {
            return new CardDecision(CardDoes.Nothing);
        }

        UnaskedKind current = state.Kind ?? UnaskedKind.CaseOpened;
        return OutranksOrEquals(about, current)
            ? new CardDecision(CardDoes.ReplaceInPlace, HoldFor(ev))
            : new CardDecision(CardDoes.WaitInSlot);
    }

    private static CardDecision OnCompact(CardState state, CardEvent ev)
    {
        UnaskedKind current = state.Kind ?? UnaskedKind.Notice;
        switch (ev.Kind)
        {
            case CardEventKind.AskedOpen:
                return new CardDecision(CardDoes.CloseUnaskedOpenAsked);

            case CardEventKind.Unasked when ev.About is { } about:
                if (about == UnaskedKind.CaseOpened)
                {
                    return new CardDecision(CardDoes.MorphToFull);
                }

                return OutranksOrEquals(about, current)
                    ? new CardDecision(CardDoes.ReplaceInPlace, HoldFor(ev))
                    : new CardDecision(CardDoes.WaitInSlot);

            case CardEventKind.BodyClick:
                return new CardDecision(CardDoes.GrowToFull);

            case CardEventKind.PointerOver:
                return new CardDecision(CardDoes.PauseHold);

            case CardEventKind.PointerLeave:
                return new CardDecision(CardDoes.ResumeHold);

            case CardEventKind.ConnectOwnButton:
                return new CardDecision(CardDoes.TakeBack);

            case CardEventKind.ConnectElsewhere:
                // Moved to is no longer true once the AirPods are back on this PC; the others stay to their hold.
                return new CardDecision(current == UnaskedKind.MovedTo ? CardDoes.FoldNow : CardDoes.StayToHold);

            default:
                return new CardDecision(CardDoes.Nothing);
        }
    }
}
