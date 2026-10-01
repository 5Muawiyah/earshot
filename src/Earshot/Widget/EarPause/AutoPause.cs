using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Stage 1 only: a bud leaving the ear pauses the one session playing to the AirPods. Stage 2 (resuming what
// was paused) is not built; the seam (IMediaSessions.PlaybackInfoChanged, ITryPlayAsync) is ready for it,
// and leaving it out for now changes nothing about how stage 1 behaves.
//
// Fed one reading of the chosen set at a time by whatever owns the widget's pipeline (out of scope here): the
// two in-ear bits (null when the table does not set them), when the reading was taken, and the facts that
// decide "renders to the AirPods" (Where, the default render endpoint's container, the container this build
// watches).
internal sealed class AutoPause
{
    private readonly IMediaSessions _sessions;
    private readonly Func<bool> _autoPauseEnabled;
    private readonly ILog _log;

    private bool? _lastLeftInEar;
    private bool? _lastRightInEar;

    public AutoPause(IMediaSessions sessions, Func<bool> autoPauseEnabled, ILog log)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(autoPauseEnabled);
        ArgumentNullException.ThrowIfNull(log);
        _sessions = sessions;
        _autoPauseEnabled = autoPauseEnabled;
        _log = log;
    }

    // Returns true exactly when this call paused a session.
    public async Task<bool> ApplyAsync(
        bool? leftInEar,
        bool? rightInEar,
        DateTimeOffset readingAtUtc,
        DateTimeOffset nowUtc,
        AirPodsWhere where,
        Guid defaultRenderContainerId,
        Guid watchedContainerId,
        CancellationToken ct)
    {
        // A bud's bit going true to false since the last reading of the chosen set. Only the chosen set's readings
        // reach here, so another pair's can neither cause nor hide an edge.
        bool leftLeftTheEar = _lastLeftInEar == true && leftInEar == false;
        bool rightLeftTheEar = _lastRightInEar == true && rightInEar == false;

        if (leftInEar is not null)
        {
            _lastLeftInEar = leftInEar;
        }

        if (rightInEar is not null)
        {
            _lastRightInEar = rightInEar;
        }

        if (!(leftLeftTheEar || rightLeftTheEar))
        {
            return false;
        }

        if (nowUtc - readingAtUtc > WidgetTiming.EarFreshWindow)
        {
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
