using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Ear detection: a bud leaving the ear pauses the one session playing to the AirPods, and the bud going back resumes
// that same session when AutoResume's conditions all hold (see its header). Pause when the AirPods leave this PC is a
// different feature (PauseOnLeave) and never resumes anything.
//
// Nothing is resumed over something the person did by hand only as far as Windows reports it: the session manager's
// change events cancel the remembered pause, and a read of the sessions just before resuming must still find the
// paused session paused and nothing else playing. A source that is not listening to those events (ReportsChanges false)
// resumes nothing, so the guarantee is never made on a read alone.
//
// Ear detection is inactive today. The in-ear value comes from two bits of the broadcast's status byte, and neither
// permitted description of the message says which bits those are, nor does any saved capture, so the decode table
// sets none and every reading carries a null in-ear value for both buds. Nothing here invents a bit: with null values
// there is no edge, so nothing pauses and nothing resumes. The logic is complete and proved with a table that sets
// invented bits; setting the real two bits in ProximityDecodeTable switches it on with no change here.
//
// Fed one reading of the chosen set at a time by whatever owns the widget's pipeline: the two in-ear bits (null when
// the table does not set them), when the reading was taken, and the facts that decide "renders to the AirPods" (Where,
// the default render endpoint's container, the container this build watches).
internal sealed class AutoPause : IDisposable
{
    private readonly IMediaSessions _sessions;
    private readonly Func<bool> _autoPauseEnabled;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly AutoResume _resume = new();
    private readonly Lock _gate = new();

    private InEarState _left;
    private InEarState _right;
    private long _generation;

    public AutoPause(IMediaSessions sessions, Func<bool> autoPauseEnabled, ILog log, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(autoPauseEnabled);
        ArgumentNullException.ThrowIfNull(log);
        _sessions = sessions;
        _autoPauseEnabled = autoPauseEnabled;
        _log = log;
        _time = time ?? TimeProvider.System;
        _sessions.PlaybackInfoChanged += OnPlaybackInfoChanged;
    }

    // Whether a pause is remembered and could still be resumed.
    internal bool HasRememberedPause => _resume.HasPending;

    public void Dispose() => _sessions.PlaybackInfoChanged -= OnPlaybackInfoChanged;

    // The AirPods stopped being this PC's output: a remembered pause is forgotten and never resumed.
    public void NoteOutput(bool rendersToAirPods)
    {
        if (rendersToAirPods)
        {
            return;
        }

        if (_resume.NoteOutputGone() is { } reason)
        {
            _log.Info("Auto-resume cancelled: " + reason + ".");
        }
    }

    // Raised on whatever thread Windows calls back on.
    private void OnPlaybackInfoChanged(object? sender, string sessionId)
    {
        if (_resume.NoteSessionChanged(sessionId, _time.GetUtcNow()) is { } reason)
        {
            _log.Info("Auto-resume cancelled: " + reason + ".");
        }
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
        CancellationToken ct,
        long selectionGeneration = 0)
    {
        bool rendersToAirPods = where == AirPodsWhere.ThisPc && watchedContainerId != Guid.Empty && defaultRenderContainerId == watchedContainerId;

        bool leftLeftTheEar;
        bool rightLeftTheEar;
        InEarState beforeLeft;
        InEarState beforeRight;
        ResumeDecision decision;
        bool setChanged = false;
        bool forgotPause = false;
        lock (_gate)
        {
            // In-ear values and a remembered pause belong to one chosen set. When the chosen set is not the one the
            // earlier readings came from (a first choice, a switch to a nearer pair, a set chosen again after the
            // addresses rotated), none of that is carried over: a bud of one pair "in" and a bud of another "out" is not
            // a bud leaving, and another pair's bud being in is not the paused pair's bud coming back.
            if (selectionGeneration != _generation)
            {
                _generation = selectionGeneration;
                _left = default;
                _right = default;
                forgotPause = _resume.Forget();
                setChanged = true;
            }

            // A bud's bit going true to false since the last reading of the chosen set, taken only against a previous
            // value that is itself fresh: an old "in" must never pair with a new "out". Only the chosen set's readings
            // reach here, so another pair's can neither cause nor hide an edge.
            beforeLeft = _left;
            beforeRight = _right;
            leftLeftTheEar = leftInEar == false && beforeLeft.Value == true && IsFresh(beforeLeft.At, nowUtc);
            rightLeftTheEar = rightInEar == false && beforeRight.Value == true && IsFresh(beforeRight.At, nowUtc);

            if (leftInEar is not null)
            {
                _left = new InEarState(leftInEar, readingAtUtc);
            }

            if (rightInEar is not null)
            {
                _right = new InEarState(rightInEar, readingAtUtc);
            }

            decision = _resume.Evaluate(nowUtc, readingAtUtc, _autoPauseEnabled(), rendersToAirPods, watchedContainerId, _left, _right);
        }

        if (setChanged && forgotPause)
        {
            _log.Info("Auto-resume cancelled: another set of AirPods was chosen.");
        }

        if (decision.Verdict == ResumeVerdict.Cancel)
        {
            _log.Info("Auto-resume cancelled: " + decision.Reason + ".");
        }
        else if (decision.Verdict == ResumeVerdict.Resume && decision.Pause is { } remembered)
        {
            if (!_sessions.ReportsChanges)
            {
                _log.Info("Auto-resume cancelled: the media sessions are not reporting changes.");
                return false;
            }

            await ResumeAsync(remembered, ct).ConfigureAwait(false);
            return false;
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

        if (!rendersToAirPods)
        {
            return false;
        }

        // Two or more Playing: the system's own "current" session is not necessarily the one rendering to
        // the AirPods, so none is paused, and that is logged rather than guessed at.
        var recorder = new PauseRecorder(_sessions);
        SessionPauseResult result = await SessionPause.PauseTheOnePlayingAsync(recorder, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SessionPauseOutcome.NoneToPause:
                return false;
            case SessionPauseOutcome.Ambiguous:
                _log.Info("Auto-pause: " + result.PlayingCount + " sessions are playing, so none was paused.");
                return false;
            case SessionPauseOutcome.Paused:
                _log.Info("Auto-pause paused " + result.AppId + ".");
                if (recorder.PausedSessionId is not null && !_sessions.ReportsChanges)
                {
                    // A pause is only resumed when a play or pause made by hand in between would have been reported.
                    _log.Info("Auto-resume is not armed: the media sessions are not reporting changes, so this pause will not be resumed.");
                }
                else if (recorder.PausedSessionId is { } rememberedId)
                {
                    // Another reading may have chosen another set while the pause was being made, and reset what was
                    // remembered for the first. A pause made for a set that is no longer the chosen one is not
                    // remembered: the new set's buds being in is not that set's bud coming back. The check and the
                    // remembering are one step under the lock, so a choice cannot land between them.
                    bool stillTheChosenSet;
                    lock (_gate)
                    {
                        stillTheChosenSet = selectionGeneration == _generation;
                        if (stillTheChosenSet)
                        {
                            // The buds that were in the ear before this reading are the ones that have to be back.
                            _resume.Remember(new RememberedPause(
                                rememberedId, result.AppId ?? "(unknown)",
                                LeftWasIn: beforeLeft.Value == true,
                                RightWasIn: beforeRight.Value == true,
                                watchedContainerId, nowUtc, nowUtc + WidgetTiming.OwnPauseEchoWindow));
                        }
                    }

                    if (!stillTheChosenSet)
                    {
                        _log.Info("Auto-resume is not armed: another set of AirPods was chosen while this pause was being made.");
                    }
                }

                return true;
            default:
                _log.Warn("Auto-pause could not pause " + result.AppId + ".");
                return false;
        }
    }

    // Plays the remembered session, after a read of the sessions confirms it is still the person-untouched state Earshot
    // left it in: that session reads Paused and can be played, and nothing else is playing.
    private async Task ResumeAsync(RememberedPause pause, CancellationToken ct)
    {
        IReadOnlyList<MediaSessionView> all = await _sessions.ReadAsync(ct).ConfigureAwait(false);
        MediaSessionView? mine = null;
        foreach (MediaSessionView session in all)
        {
            if (string.Equals(session.SessionId, pause.SessionId, StringComparison.Ordinal))
            {
                mine = session;
            }
            else if (session.PlaybackStatus == MediaPlaybackState.Playing)
            {
                _log.Info("Auto-resume cancelled: another session is playing.");
                return;
            }
        }

        if (mine is null || mine.PlaybackStatus != MediaPlaybackState.Paused)
        {
            _log.Info("Auto-resume cancelled: " + pause.AppId + " no longer reads paused.");
            return;
        }

        if (!mine.IsPlayEnabled)
        {
            _log.Info("Auto-resume cancelled: " + pause.AppId + " cannot be played.");
            return;
        }

        bool played = await _sessions.TryPlayAsync(pause.SessionId, ct).ConfigureAwait(false);
        if (played)
        {
            _log.Info("Auto-resume resumed " + pause.AppId + ".");
        }
        else
        {
            _log.Warn("Auto-resume could not resume " + pause.AppId + ".");
        }
    }

    private static bool IsFresh(DateTimeOffset? at, DateTimeOffset now) => at is DateTimeOffset a && now - a <= WidgetTiming.EarFreshWindow;

    // Passes everything through and notes which session the pause went to, so the remembered pause names the session
    // SessionPause actually chose rather than one guessed from a second read.
    private sealed class PauseRecorder(IMediaSessions inner) : IMediaSessions
    {
        public string? PausedSessionId { get; private set; }

        public event EventHandler<string>? PlaybackInfoChanged
        {
            add => inner.PlaybackInfoChanged += value;
            remove => inner.PlaybackInfoChanged -= value;
        }

        public bool ReportsChanges => inner.ReportsChanges;

        public Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);

        public async Task<bool> TryPauseAsync(string sessionId, CancellationToken ct)
        {
            bool paused = await inner.TryPauseAsync(sessionId, ct).ConfigureAwait(false);
            if (paused)
            {
                PausedSessionId = sessionId;
            }

            return paused;
        }

        public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct) => inner.TryPlayAsync(sessionId, ct);
    }
}
