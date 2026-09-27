using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Stage 1 only: a bud leaving the ear pauses the one session playing to the AirPods. Stage 2 (resuming what
// was paused) is not built; the seam (IMediaSessions.PlaybackInfoChanged, ITryPlayAsync) is ready for it,
// and leaving it out changes nothing about stage 1, the way the spec for this allows.
//
// Fed one owned-or-not reading at a time by whatever owns the widget's pipeline (out of scope here): the
// ownership verdict, the two in-ear bits (null when unproved), when the reading was taken, and the facts
// that decide "renders to the AirPods" (Where, the default render endpoint's container, the container this
// build watches).
internal sealed class AutoPause
{
    private readonly IMediaSessions _sessions;
    private readonly Func<bool?> _broadcastContinuesWhilePlayingFromThisPc;
    private readonly Func<bool> _autoPauseEnabled;
    private readonly ILog _log;

    private bool? _lastLeftInEar;
    private bool? _lastRightInEar;

    // Production entry point: always reads phase 0's gate from WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc
    // itself, so nothing composing this class can accidentally wire up a different value (M4).
    public AutoPause(IMediaSessions sessions, Func<bool> autoPauseEnabled, ILog log)
        : this(sessions, static () => WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc, autoPauseEnabled, log)
    {
    }

    // Test-only: supplies the gate directly, so a test can exercise the "phase 0 has proved this" path
    // without WidgetDefaults.BroadcastContinuesWhilePlayingFromThisPc ever holding anything but its shipped
    // null default, the way ClaimFlow's internal overload does for its own phase 0 constant.
    internal AutoPause(IMediaSessions sessions, Func<bool?> broadcastContinuesWhilePlayingFromThisPc, Func<bool> autoPauseEnabled, ILog log)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(broadcastContinuesWhilePlayingFromThisPc);
        ArgumentNullException.ThrowIfNull(autoPauseEnabled);
        ArgumentNullException.ThrowIfNull(log);
        _sessions = sessions;
        _broadcastContinuesWhilePlayingFromThisPc = broadcastContinuesWhilePlayingFromThisPc;
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
        bool owned = verdict is OwnershipVerdict.Owned or OwnershipVerdict.OwnedByLiveConnection;

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

        if (_broadcastContinuesWhilePlayingFromThisPc() != true)
        {
            _log.Info("Auto-pause is waiting for phase 0 to confirm the broadcast continues while playing from this PC.");
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

        IReadOnlyList<MediaSessionView> sessions = await _sessions.ReadAsync(ct).ConfigureAwait(false);
        List<MediaSessionView> playing = new();
        foreach (MediaSessionView session in sessions)
        {
            if (session.PlaybackStatus == MediaPlaybackState.Playing && session.IsPauseEnabled)
            {
                playing.Add(session);
            }
        }

        // Two or more Playing: the system's own "current" session is not necessarily the one rendering to
        // the AirPods, so none is paused, and that is logged rather than guessed at.
        if (playing.Count == 0)
        {
            return false;
        }

        if (playing.Count > 1)
        {
            _log.Info("Auto-pause: " + playing.Count + " sessions are playing, so none was paused.");
            return false;
        }

        MediaSessionView chosen = playing[0];
        bool paused = await _sessions.TryPauseAsync(chosen.SessionId, ct).ConfigureAwait(false);
        string appId = chosen.SourceAppUserModelId ?? "(unknown)";
        if (paused)
        {
            _log.Info("Auto-pause paused " + appId + ".");
        }
        else
        {
            _log.Warn("Auto-pause could not pause " + appId + ".");
        }

        return paused;
    }
}
