using Earshot.Contracts;

namespace Earshot.Widget;

// A verdict of "moved": the pair kept sending, out of the case and near, after a leave Earshot did not start. The card's compact
// "Moved to" row is shown from this; Where already reads Elsewhere by then.
internal sealed class MovedToEventArgs(MovedVerdict verdict, DateTimeOffset at) : EventArgs
{
    public MovedVerdict Verdict { get; } = verdict;

    public DateTimeOffset At { get; } = at;
}

// What the tray needs of the service for Moved to, apart from IWidgetStatus: the leave Earshot did not start goes in, and the
// verdict comes out. UI thread for the event.
internal interface IMovedToSource
{
    event EventHandler<MovedToEventArgs>? MovedTo;

    void NoteLeftByOther(DateTimeOffset at);
}

// The service's side of Moved to. The rule (MovedToRule) is fed every message of the paired model, and of the remembered colour
// when one is remembered, linked or not: it shows no figure, so it needs no link. A verdict of moved latches, and while the
// latch holds Where reads Elsewhere, which is what makes "On your iPhone" and "On another device" appear. The latch clears three
// ways: this PC has the AirPods again, any case-known message of the paired model, and BroadcastRules.LostLimit with no message
// of the paired model. The latch runs whatever the Moved to setting says; only the compact card obeys it.
internal sealed partial class WidgetStatusService
{
    private readonly MovedToRule _moved = new();
    private bool _movedElsewhere;
    private DateTimeOffset _lastPairedMessageAt;
    private ITimer? _movedTimer;
    private DateTimeOffset? _movedTimerDue;

    public event EventHandler<MovedToEventArgs>? MovedTo;

    // A leave Earshot did not start (PauseOnLeave.LeftByOther). Any thread.
    public void NoteLeftByOther(DateTimeOffset at)
    {
        MovedVerdict? raise;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _moved.Left(at);
            raise = ApplyMovedVerdictLocked(_moved.Check(at, backOnThisPc: false));
            RescheduleMovedTimerLocked();
        }

        if (raise is not null)
        {
            RaiseMovedTo(raise, at);
        }

        PublishAndNotify();
    }

    // Must be called holding _gate. One message of the paired model: kept for the rule, clears the latch when it is case-known,
    // and may bring the verdict. Returns the verdict to raise (moved), or null.
    private MovedVerdict? FeedMovedLocked(ProximityMessage message, sbyte rssi, DateTimeOffset at)
    {
        ushort? paired = _selector.PairedModel ?? _selector.Memory?.Model;
        if (paired is not ushort model || message.Model != model || (_selector.Memory is { } memory && memory.Colour != message.Colour))
        {
            return null;
        }

        bool caseKnown = BroadcastSenderSets.CaseKnown(message);
        _lastPairedMessageAt = at;
        _moved.Observe(at, rssi, caseKnown);
        if (caseKnown && _movedElsewhere)
        {
            ClearMovedLocked("a case-known message of the pair came");
        }

        MovedVerdict? verdict = _moved.Check(at, backOnThisPc: false);
        if (verdict is null)
        {
            return null;
        }

        MovedVerdict? raise = ApplyMovedVerdictLocked(verdict);
        RescheduleMovedTimerLocked();
        return raise;
    }

    // Must be called holding _gate. Writes the verdict's line, and latches on moved.
    private MovedVerdict? ApplyMovedVerdictLocked(MovedVerdict? verdict)
    {
        if (verdict is null)
        {
            return null;
        }

        _log.Info(verdict.Line());
        if (verdict.Kind != MovedVerdictKind.Moved)
        {
            return null;
        }

        _movedElsewhere = true;
        return verdict;
    }

    // Must be called holding _gate. This PC has the AirPods: whatever was moved is over, and a leave still being watched ends.
    private void NoteThisPcBackLocked()
    {
        ClearMovedLocked("this PC has the AirPods again");
        _ = ApplyMovedVerdictLocked(_moved.Check(_timeProvider.GetUtcNow(), backOnThisPc: true));
        RescheduleMovedTimerLocked();
    }

    private void ClearMovedLocked(string why)
    {
        if (!_movedElsewhere)
        {
            return;
        }

        _movedElsewhere = false;
        _log.Info("Move: " + why + ", so the AirPods are no longer shown as on another device.");
    }

    // A different pair was adopted: nothing of the old one's leave or latch is kept.
    private void ForgetMovedLocked()
    {
        _movedElsewhere = false;
        _moved.Reset();
        RescheduleMovedTimerLocked();
    }

    private void RaiseMovedTo(MovedVerdict verdict, DateTimeOffset at) =>
        _uiPost(() => MovedTo?.Invoke(this, new MovedToEventArgs(verdict, at)));

    // The next time the rule or the latch has something to do with no message to prompt it: the bound, the long line, and the
    // loss of the pair. Must be called holding _gate.
    private void RescheduleMovedTimerLocked()
    {
        if (_closed)
        {
            return;
        }

        DateTimeOffset? due = _moved.NextDue;
        if (_movedElsewhere)
        {
            DateTimeOffset lost = _lastPairedMessageAt + BroadcastRules.LostLimit;
            due = due is { } soonest && soonest < lost ? soonest : lost;
        }

        if (due is not { } at)
        {
            if (_movedTimerDue is not null)
            {
                _movedTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _movedTimerDue = null;
            }

            return;
        }

        if (_movedTimerDue == at)
        {
            return;
        }

        TimeSpan wait = at - _timeProvider.GetUtcNow();
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        _movedTimer ??= _timeProvider.CreateTimer(
            static state => ((WidgetStatusService)state!).OnMovedDue(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _movedTimer.Change(wait, Timeout.InfiniteTimeSpan);
        _movedTimerDue = at;
    }

    // Runs on a timer thread, so like the other timers it catches what nothing else would, logs it and never lets it end the
    // process.
    private void OnMovedDue()
    {
        try
        {
            MovedVerdict? raise;
            DateTimeOffset now;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                now = _timeProvider.GetUtcNow();
                _movedTimerDue = null;
                raise = ApplyMovedVerdictLocked(_moved.Check(now, backOnThisPc: false));
                if (_moved.TakeLongSummary(now) is { } held)
                {
                    _log.Info(MovedVerdict.LongLine(held));
                }

                if (_movedElsewhere && now - _lastPairedMessageAt >= BroadcastRules.LostLimit)
                {
                    ClearMovedLocked("no message of the pair for " + (long)BroadcastRules.LostLimit.TotalSeconds + " s");
                }

                RescheduleMovedTimerLocked();
            }

            if (raise is not null)
            {
                RaiseMovedTo(raise, now);
            }

            PublishAndNotify();
        }
        catch (Exception ex)
        {
            _log.Error("Widget moved-to check failed with an unexpected error (0x" + ex.HResult.ToString("X8") + ").", ex);
        }
    }
}
