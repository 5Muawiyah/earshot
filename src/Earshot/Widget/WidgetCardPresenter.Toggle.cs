using Earshot.Contracts;

namespace Earshot.Widget;

// A connect or a disconnect on the one card. The tray tells it when one starts and ends (IToggleCard): while one runs the button
// of every card on screen shows a ring and ignores a press, and a card opened meanwhile shows the same. A failure is said in the
// status row of whichever card is open: the reason whole, E783 in caution, and the next step as a text button. With no card open
// and a next step to offer, the full card opens unasked once the gauge's cross has had its time, through the same gates every
// unasked card keeps; a gate that refuses holds the failure, and the card he asks for next shows it. A failure ends with the next
// attempt, a change of where the AirPods are, a success, or the card it was shown on closing.
internal sealed partial class WidgetCardPresenter : IToggleCard
{
    // A failure: Held is a failure no card has said yet (a gate refused the one that would have). Hold is how long the unasked card
    // that opened for it stays; the asked card and a card already open keep it until it closes.
    // How long a failure a gate held back stays to be said on the next asked card. A design choice, not a measurement.
    internal static readonly TimeSpan HeldFailureLimit = TimeSpan.FromMinutes(5);

    private sealed record ActiveFailure(string Reason, FailureNextStep? Next, AirPodsWhere Where, int Token)
    {
        public TimeSpan? Hold { get; init; }

        // When a gate held it back, so a held failure can grow too old to say.
        public DateTimeOffset? HeldAt { get; init; }

        public bool Held { get; init; }

        // A card on screen says it now. Neither held nor displayed: it waits for the gauge's cross to have had its time.
        public bool Displayed { get; init; }
    }

    private CardButtonWork _work = CardButtonWork.None;

    // How the toggle that ran last ended, for what the log says of a failure: only a Failed one is told by the gauge's cross.
    private ToggleOutcome? _lastOutcome;
    private ActiveFailure? _failure;
    private int _failureToken;
    private ITimer? _failureTimer;

    // What the buttons are doing and the failure on show, for tests.
    internal CardButtonWork WorkForTest => _work;

    internal bool FailureHeldForTest => _failure is { Held: true };

    internal bool FailureActiveForTest => _failure is { Displayed: true };

    public void ToggleStarted(bool connect) => _uiPost(() => ToggleStartedOnUiThread(connect));

    public void ToggleEnded(bool connect, ToggleOutcome outcome) => _uiPost(() => ToggleEndedOnUiThread(connect, outcome));

    public Task<FailureShown> ShowFailed(string reason, FailureNextStep? nextStep)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var result = new TaskCompletionSource<FailureShown>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiPost(() => result.TrySetResult(ShowFailedOnUiThread(reason, nextStep)));
        return result.Task;
    }

    private void ToggleStartedOnUiThread(bool connect)
    {
        if (_disposed)
        {
            return;
        }

        if (_unaskedKind == UnaskedKind.ConnectFailed && IsUnaskedFullShown)
        {
            // The card that opened for the last failure has done its work: the gauge tells this attempt.
            CloseUnasked("a new attempt began");
        }

        NoteToggleStartedForMovedTo(connect);
        _lastOutcome = null;
        DropFailure();
        _work = connect ? CardButtonWork.Connecting : CardButtonWork.Disconnecting;
        _log.Write(LogLevel.Debug, "Card: " + (connect ? "connecting" : "disconnecting") + ".");
        RenderEveryCard();
    }

    private void ToggleEndedOnUiThread(bool connect, ToggleOutcome outcome)
    {
        if (_disposed)
        {
            return;
        }

        _work = CardButtonWork.None;
        _lastOutcome = outcome;
        if (outcome is ToggleOutcome.Succeeded or ToggleOutcome.Partial)
        {
            DropFailure();
        }

        _log.Write(LogLevel.Debug, "Card: " + (connect ? "connect" : "disconnect") + " ended, " + outcome + ".");
        bool restartHold = NoteToggleEndedForMovedTo(outcome);
        RenderEveryCard();
        if (restartHold)
        {
            foreach ((_, WidgetCard card, _) in UnaskedOpenCards())
            {
                card.RestartHold();
            }
        }
    }

    private FailureShown ShowFailedOnUiThread(string reason, FailureNextStep? next)
    {
        if (_disposed)
        {
            return FailureShown.NotShown;
        }

        DropFailure();
        var failure = new ActiveFailure(reason, next, _callbacks.CurrentSnapshot().Where, ++_failureToken);
        if (IsShown || IsUnaskedFullShown)
        {
            _failure = failure with { Displayed = true };
            RenderEveryCard();
            AnnounceFailure(reason);
            _log.Write(LogLevel.Debug, "Card: connect failed, said in the open card" + (next is null ? "." : ", with a next step."));
            return FailureShown.Shown;
        }

        if (next is null)
        {
            _log.Write(LogLevel.Debug, _lastOutcome == ToggleOutcome.Failed
                ? "Card: connect failed, no card is open and there is no next step, so the gauge's cross says it."
                : "Card: failure not said on a card, no card is open and there is no next step, and no cross is drawn for it.");
            return FailureShown.NotShown;
        }

        // The gauge shows its cross first; the card follows when that has had its time, if the failure still stands.
        _failure = failure;
        int token = failure.Token;
        _failureTimer = _time.CreateTimer(_ => _uiPost(() => OpenFailedCard(token)), null, GaugeStory.CrossHold, Timeout.InfiniteTimeSpan);
        return FailureShown.Pending;
    }

    private void OpenFailedCard(int token)
    {
        StopFailureTimer();
        if (_disposed || _failure is not { Held: false } failure || failure.Token != token || IsShown)
        {
            // Gone, or the asked card was opened meanwhile and carries it.
            return;
        }

        // Displayed before the card is built, since a card draws the failure only once it is displayed (see FailureContent); a refusal
        // below puts it back to held.
        _failure = failure with { Hold = OneCardArbiter.ActionHold, Displayed = true };
        if (OpenUnaskedOnUiThread(UnaskedKind.ConnectFailed, gaugeBounds: null, _generation.Current))
        {
            return;
        }

        _failure = failure with { Held = true, Hold = null, HeldAt = _time.GetUtcNow() };
        _log.Write(LogLevel.Debug, "Card: held (connect failed), it is shown when he next asks for the card.");
    }

    // The status row's content for the failure on show, or null. A held failure is on no card yet, and one still waiting for the
    // gauge's cross is drawn by none: a button on a card that did not mark the failure displayed would press into nothing.
    private CompactContent? FailureContent() => _failure is { Held: false, Displayed: true } failure
        ? new CompactContent(
            FluentGlyphs.Error, InkRole.Caution, failure.Reason, failure.Next is null ? null : new CompactAction(failure.Next.Label), failure.Hold)
        {
            Kind = UnaskedKind.ConnectFailed,
        }
        : null;

    private bool IsUnaskedFullShown => _unaskedCompact is null && UnaskedOpenCards().Any();

    // Every card on screen draws again, the asked card and the unasked ones.
    private void RenderEveryCard()
    {
        RenderKeepingBottom();
        RefreshUnasked();
    }

    // The failure is over: forgotten, with no card drawn again (the caller does that), and any card waiting on it cancelled.
    private void DropFailure()
    {
        StopFailureTimer();
        _failure = null;
    }

    private void StopFailureTimer()
    {
        _failureTimer?.Dispose();
        _failureTimer = null;
    }

    // Where the AirPods are changed: a failure that was about how things stood is over.
    private void EndFailureOnStateChange()
    {
        if (_failure is not { } failure || _callbacks.CurrentSnapshot().Where == failure.Where)
        {
            return;
        }

        _log.Write(LogLevel.Debug, "Card: the failure is over, where the AirPods are changed.");
        if (_unaskedKind == UnaskedKind.ConnectFailed && IsUnaskedFullShown)
        {
            CloseUnasked("the state changed");
        }

        DropFailure();
    }

    // The card he asked for opened: a failure a gate held back is now said on it, and so is one still waiting for the gauge's cross to
    // have had its time (its timer is cancelled, so no second card opens for it later). Either way it is then on a card, with its button
    // working, and it ends when that card closes.
    private void ShowHeldFailure()
    {
        if (_failure is { Held: true, HeldAt: { } heldAt } && _time.GetUtcNow() - heldAt > HeldFailureLimit)
        {
            _log.Write(LogLevel.Debug, "Card: a held failure was dropped, it is too old to say anything about now.");
            DropFailure();
            return;
        }

        if (_failure is { Displayed: false } waiting)
        {
            StopFailureTimer();
            _failure = waiting with { Held = false, Hold = null, Displayed = true };
        }
    }

    // The card it was said on closed, so it is over. A failure no card has said yet stays.
    private void EndShownFailure()
    {
        if (_failure is { Displayed: true })
        {
            DropFailure();
        }
    }

    private void AnnounceFailure(string reason)
    {
        WidgetCard? card = IsShown ? _card : UnaskedOpenCards().Select(c => c.Card).FirstOrDefault();
        if (card is not null && !card.Announce(reason))
        {
            _log.Write(LogLevel.Debug, "Card: the screen reader notification was not raised.");
        }
    }

    // The failure line's button, pressed on the asked card or an unasked one: the failure goes and then the next step runs.
    private void PressFailureNextStep(bool unasked)
    {
        if (_failure is not { Displayed: true, Next: { } next })
        {
            return;
        }

        _log.Write(LogLevel.Debug, "Card: button pressed (" + next.Label + ").");
        DropFailure();
        if (unasked)
        {
            CloseUnasked("its button was pressed");
        }
        else
        {
            RenderKeepingBottom();
        }

        RunNoticeAction(next.OnPress, next.Label);
    }

    private void OnUnaskedNoticeActionRequested(object? sender, EventArgs e) => PressFailureNextStep(unasked: true);
}
