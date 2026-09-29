using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Stage 1 only: a bud leaving the ear pauses the one session playing to the AirPods. Stage 2 (resuming what
// was paused) is not built; the seam (IMediaSessions.PlaybackInfoChanged, ITryPlayAsync) is ready for it,
// and leaving it out for now changes nothing about how stage 1 behaves.
//
// Fed one owned-or-not reading at a time by whatever owns the widget's pipeline (out of scope here): the
// ownership verdict, the two in-ear bits (null when unproved), when the reading was taken, and the facts
// that decide "renders to the AirPods" (Where, the default render endpoint's container, the container this
// build watches).
internal sealed class AutoPause
{
    private readonly IMediaSessions _sessions;
    private readonly Func<bool?> _broadcastObserved;
    private readonly Func<bool> _autoPauseEnabled;
    private readonly ILog _log;

    private bool? _lastLeftInEar;
    private bool? _lastRightInEar;

    // broadcastObserved: whether the AirPods were observed to keep broadcasting while this PC plays to them
    // (the proof store's run-time observation): true once observed, null before. Nothing is paused until it
    // is true.
    public AutoPause(IMediaSessions sessions, Func<bool?> broadcastObserved, Func<bool> autoPauseEnabled, ILog log)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(broadcastObserved);
        ArgumentNullException.ThrowIfNull(autoPauseEnabled);
        ArgumentNullException.ThrowIfNull(log);
        _sessions = sessions;
        _broadcastObserved = broadcastObserved;
        _autoPauseEnabled = autoPauseEnabled;
        _log = log;
    }

    // Returns true exactly when this call paused a session.
    public async Task<bool> ApplyAsync(
        OwnershipVerdict verdict,
        bool? leftInEar,
        bool? rightInEar,
        DateTimeOffset readingAtUtc,
        DateTimeOffset nowUtc,
        AirPodsWhere where,
        Guid defaultRenderContainerId,
        Guid watchedContainerId,
        CancellationToken ct)
    {
        bool owned = verdict is OwnershipVerdict.Owned;

        // A bud's proved bit going true to false since the last OWNED reading. A stranger's reading never
        // updates the last-known state, so it can neither cause nor hide an edge.
        bool leftLeftTheEar = _lastLeftInEar == true && leftInEar == false;
        bool rightLeftTheEar = _lastRightInEar == true && rightInEar == false;

        if (owned)
        {
            if (leftInEar is not null)
            {
                _lastLeftInEar = leftInEar;
            }

            if (rightInEar is not null)
            {
                _lastRightInEar = rightInEar;
            }
        }

        if (!owned || !(leftLeftTheEar || rightLeftTheEar))
        {
            return false;
        }

        if (nowUtc - readingAtUtc > WidgetTiming.EarFreshWindow)
        {
            return false;
        }

        if (_broadcastObserved() != true)
        {
            _log.Info("Auto-pause is waiting for the broadcast to be observed while playing from this PC.");
            return false;
        }

        if (!_autoPauseEnabled())
        {
            return false;
        }

        bool rendersToAirPods = where == AirPodsWhere.ThisPc && watchedContainerId != Guid.Empty && defaultRenderContainerId == watchedContainerId;
        if (!rendersToAirPods)
        {
            return false;
        }

        // Two or more Playing: the system's own "current" session is not necessarily the one rendering to
        // the AirPods, so none is paused, and that is logged rather than guessed at.
        SessionPauseResult result = await SessionPause.PauseTheOnePlayingAsync(_sessions, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SessionPauseOutcome.NoneToPause:
                return false;
            case SessionPauseOutcome.Ambiguous:
                _log.Info("Auto-pause: " + result.PlayingCount + " sessions are playing, so none was paused.");
                return false;
            case SessionPauseOutcome.Paused:
                _log.Info("Auto-pause paused " + result.AppId + ".");
                return true;
            default:
                _log.Warn("Auto-pause could not pause " + result.AppId + ".");
                return false;
        }
    }
}
