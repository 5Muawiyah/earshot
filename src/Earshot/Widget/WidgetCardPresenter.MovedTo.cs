using System.Globalization;
using Earshot.App;
using Earshot.Contracts;

namespace Earshot.Widget;

// The compact "Moved to" card: another device took the AirPods. WidgetStatusService decides it (MovedToRule) and Where reads
// Elsewhere; this shows the one-row card, subject to the same gates as any card nobody asked for, and only while it is still true.
//
// Take back is Connect, reached only by a genuine click on the card's button (WidgetCard.CompactActionRequested), through the same
// RequestToggle path the card's Connect button uses. Nothing else here calls it: no timer, no fold, no verdict. A press shows a
// ring in the button; the connect ending shows a tick for 800 ms and the card folds; a failure grows the card to the full one in
// its failed state, which says why. The compact row never shows an error itself.
internal sealed partial class WidgetCardPresenter
{
    // How long a pressed Take back waits for the tray to start the connect before the ring stops. The tray may refuse the click
    // (safe mode, a menu action in flight) and then no start ever comes, so the ring must not turn for ever. A design choice: the
    // tray starts a connect it accepts within a few milliseconds, and a connect that waits for one in flight is told first.
    internal static readonly TimeSpan TakeBackStartLimit = TimeSpan.FromSeconds(5);

    private enum TakeBackState { None, Progress, Done }

    private TakeBackState _takeBack;
    private bool _takeBackStarted;
    private int _takeBackToken;
    private ITimer? _takeBackTimer;

    // The button's state in the compact row on show: a ring while Take back runs, then the tick.
    private CompactActionState CompactStateFor(CompactContent compact) => compact.Kind != UnaskedKind.MovedTo
        ? CompactActionState.Idle
        : _takeBack switch
        {
            TakeBackState.Progress => CompactActionState.Progress,
            TakeBackState.Done => CompactActionState.Done,
            _ => CompactActionState.Idle,
        };

    // WidgetStatusService's verdict: the AirPods are on another device. Any thread.
    public void MovedTo() => _uiPost(MovedToOnUiThread);

    private void MovedToOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        if (!MovedToIsTrue(out string? why))
        {
            _log.Write(LogLevel.Debug, "Card: not shown (moved to), " + why + ".");
            return;
        }

        // A refusal (a gate, the card he asked for, a full-screen application) is logged by the one path and not queued, and
        // the status still reads true. A card that outranks it makes it wait one turn.
        _ = ShowCompactOnUiThread(new CompactRequest(CompactContents.MovedTo(_callbacks.OtherDeviceLabel()), CardAnchor.NearTray));
    }

    // Whether the card may show now: the setting is on and the AirPods are on another device. Why says which not.
    private bool MovedToIsTrue(out string? why)
    {
        if (!(_callbacks.MovedToOn?.Invoke() ?? true))
        {
            why = "its setting is off";
            return false;
        }

        if (_callbacks.CurrentSnapshot().Where != AirPodsWhere.Elsewhere)
        {
            why = "the AirPods are not on another device now";
            return false;
        }

        why = null;
        return true;
    }

    // ---- Take back

    // A genuine left down and up on the compact card's button (or a screen reader's default action on it): the owner asked for
    // the AirPods back. The only place the compact card asks for a toggle.
    private void OnTakeBackPressed()
    {
        CardDecision decision = OneCardArbiter.Decide(CurrentCardState(), new CardEvent(CardEventKind.ConnectOwnButton));
        if (decision.Does != CardDoes.TakeBack || _takeBack != TakeBackState.None)
        {
            return;
        }

        _takeBack = TakeBackState.Progress;
        _takeBackStarted = false;
        int token = ++_takeBackToken;
        _log.Write(LogLevel.Debug, "Card: button pressed (" + WidgetCopy.TakeBack + ").");
        StopTakeBackTimer();
        _takeBackTimer = _time.CreateTimer(_ => _uiPost(() => TakeBackNotStarted(token)), null, TakeBackStartLimit, Timeout.InfiniteTimeSpan);
        RefreshUnasked();
        _callbacks.RequestToggle(CardPlace.NearTray);
    }

    // The tray started a toggle. While Take back waits for it, a connect is its own; anything else that starts means it is not
    // taking anything back. A connect that starts while a Moved to card holds, and was not its own, is the AirPods coming back
    // from somewhere else: the arbiter says what the card does about it.
    private void NoteToggleStartedForMovedTo(bool connect)
    {
        if (_takeBack == TakeBackState.Progress)
        {
            if (connect)
            {
                _takeBackStarted = true;
                StopTakeBackTimer();
            }
            else
            {
                EndTakeBack("a disconnect began instead");
            }

            return;
        }

        if (!connect)
        {
            return;
        }

        CardDecision decision = OneCardArbiter.Decide(CurrentCardState(), new CardEvent(CardEventKind.ConnectElsewhere));
        if (decision.Does == CardDoes.FoldNow)
        {
            CloseUnasked("the AirPods are back on this PC");
        }
    }

    // The toggle Take back started ended. Returns whether the compact row holds again (it was cancelled). The caller draws.
    private bool NoteToggleEndedForMovedTo(ToggleOutcome outcome)
    {
        if (_takeBack != TakeBackState.Progress || !_takeBackStarted)
        {
            return false;
        }

        StopTakeBackTimer();
        if (_unaskedCompact is not { Kind: UnaskedKind.MovedTo })
        {
            // The row is gone (the case opened over it, or it grew): nothing of Take back is left to show.
            _takeBack = TakeBackState.None;
            return false;
        }

        switch (outcome)
        {
            case ToggleOutcome.Succeeded:
            case ToggleOutcome.Partial:
                // The AirPods are back (Partial: back, with the protection not applied, which is said as a notice, not as a failed
                // take back). The tick holds 800 ms and the card folds (WidgetCard); the gauge then shows the reading.
                _takeBack = TakeBackState.Done;
                return false;

            case ToggleOutcome.Failed:
                // The compact never shows an error: the card grows to the full one, which says why when the failure arrives.
                _takeBack = TakeBackState.None;
                _unaskedCompact = null;
                _unaskedRequest = null;
                _unaskedKind = UnaskedKind.ConnectFailed;
                _log.Write(LogLevel.Debug, "Card: take back failed, grown to the full card.");
                return false;

            default:
                _takeBack = TakeBackState.None;
                return true;
        }
    }

    // No connect started after the press: the ring stops and the row holds again.
    private void TakeBackNotStarted(int token)
    {
        if (_disposed || token != _takeBackToken || _takeBack != TakeBackState.Progress || _takeBackStarted)
        {
            return;
        }

        _log.Write(LogLevel.Debug, "Card: take back did not start, no connect began within " +
            ((long)TakeBackStartLimit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms, so the ring stops.");
        EndTakeBack(null);
    }

    // Take back is over without a result: the button reads Take back again and the row holds again.
    private void EndTakeBack(string? why)
    {
        StopTakeBackTimer();
        if (_takeBack == TakeBackState.None)
        {
            return;
        }

        _takeBack = TakeBackState.None;
        if (why is not null)
        {
            _log.Write(LogLevel.Debug, "Card: take back ended, " + why + ".");
        }

        RefreshUnasked();
        foreach ((_, WidgetCard card, _) in UnaskedOpenCards())
        {
            card.RestartHold();
        }
    }

    // The card closed or the presenter is going: nothing of Take back outlives it.
    private void ResetTakeBack()
    {
        StopTakeBackTimer();
        _takeBack = TakeBackState.None;
        _takeBackStarted = false;
    }

    private void StopTakeBackTimer()
    {
        _takeBackTimer?.Dispose();
        _takeBackTimer = null;
    }
}
