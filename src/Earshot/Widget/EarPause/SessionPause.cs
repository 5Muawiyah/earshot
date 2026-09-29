namespace Earshot.Widget.EarPause;

// How a pause through Windows Media Controls ended. NoneToPause: no session is playing that can be paused.
// Ambiguous: two or more are, and the system's own current session is not necessarily the one rendering to the
// AirPods, so none is paused. Paused: the one playing session was paused. Refused: Windows did not take the pause.
internal enum SessionPauseOutcome
{
    NoneToPause,
    Ambiguous,
    Paused,
    Refused,
}

internal sealed record SessionPauseResult(SessionPauseOutcome Outcome, int PlayingCount, string? AppId);

// The one way Earshot pauses playback: read the sessions, and pause the single one that is playing and can be
// paused. Shared by auto-pause (a bud leaves the ear) and pause on leave (the AirPods leave this PC), so both act on
// the same session by the same rule. It never plays, and never resumes anything.
//
// The session is the one playing on this PC whichever output it is using: Windows Media Controls do not say which
// device a session renders to, and tying one to the AirPods' endpoint would mean matching it to a per-application
// audio session through Core Audio, which is not a cheap or a reliable match. So a song playing on the speakers is
// paused when the AirPods leave, as one playing to the AirPods is, and with two sessions playing none is paused
// (Ambiguous), never a guess.
internal static class SessionPause
{
    public static async Task<SessionPauseResult> PauseTheOnePlayingAsync(IMediaSessions sessions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        IReadOnlyList<MediaSessionView> all = await sessions.ReadAsync(ct).ConfigureAwait(false);
        List<MediaSessionView> playing = new();
        foreach (MediaSessionView session in all)
        {
            if (session.PlaybackStatus == MediaPlaybackState.Playing && session.IsPauseEnabled)
            {
                playing.Add(session);
            }
        }

        if (playing.Count == 0)
        {
            return new SessionPauseResult(SessionPauseOutcome.NoneToPause, 0, null);
        }

        if (playing.Count > 1)
        {
            return new SessionPauseResult(SessionPauseOutcome.Ambiguous, playing.Count, null);
        }

        MediaSessionView chosen = playing[0];
        bool paused = await sessions.TryPauseAsync(chosen.SessionId, ct).ConfigureAwait(false);
        string appId = chosen.SourceAppUserModelId ?? "(unknown)";
        return new SessionPauseResult(paused ? SessionPauseOutcome.Paused : SessionPauseOutcome.Refused, 1, appId);
    }
}
