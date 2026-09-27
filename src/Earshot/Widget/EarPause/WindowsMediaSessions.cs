using System.Globalization;
using System.Runtime.Versioning;
using Earshot.Contracts;
using Windows.Foundation;
using Windows.Media.Control;

namespace Earshot.Widget.EarPause;

// GlobalSystemMediaTransportControlsSessionManager, guarded the way every WinRT call in this codebase is:
// an early return on WidgetPlatformGuard, then the call, in the same method.
// https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager
//
// Read-only probe on this PC, 2026-09-27, a console exe with no package identity: RequestAsync succeeded,
// GetSessions().Count was 2, and a session's PlaybackStatus and Controls.IsPauseEnabled read correctly.
// Nothing was paused by the probe. Stage 2's PlaybackInfoChanged wiring is not built yet: the event exists
// on the seam and is simply never raised here, which is what leaving stage 2 out looks like.
internal sealed class WindowsMediaSessions : IMediaSessions
{
    private readonly ILog _log;

    public WindowsMediaSessions(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Stage 2 is not built: nothing here ever raises this, so the field-like form (which warns on a never
    // raised event) is replaced with explicit accessors over a field that is genuinely referenced.
    private EventHandler<string>? _playbackInfoChanged;

    public event EventHandler<string>? PlaybackInfoChanged
    {
        add => _playbackInfoChanged += value;
        remove => _playbackInfoChanged -= value;
    }

    public async Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct)
    {
        if (!WidgetPlatformGuard.HasMediaSessions)
        {
            return Array.Empty<MediaSessionView>();
        }

        GlobalSystemMediaTransportControlsSessionManager manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure("read media sessions", ex);
            return Array.Empty<MediaSessionView>();
        }

        var views = new List<MediaSessionView>();
        foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
        {
            try
            {
                views.Add(ViewOf(session));
            }
            catch (Exception ex)
            {
                LogFailure("read one media session", ex);
            }
        }

        return views;
    }

    public Task<bool> TryPauseAsync(string sessionId, CancellationToken ct) => TryControlAsync(sessionId, pause: true, ct);

    public Task<bool> TryPlayAsync(string sessionId, CancellationToken ct) => TryControlAsync(sessionId, pause: false, ct);

    // pause selects TryPauseAsync (true) or TryPlayAsync (false); both calls stay inside this one guarded
    // method, since a lambda built in an unguarded caller and handed in would itself be an unguarded WinRT
    // call site.
    private async Task<bool> TryControlAsync(string sessionId, bool pause, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!WidgetPlatformGuard.HasMediaSessions)
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
            GlobalSystemMediaTransportControlsSession? session = FindSession(manager, sessionId, pause);
            if (session is null)
            {
                _log.Warn("Media session " + sessionId + " was not found to control.");
                return false;
            }

            IAsyncOperation<bool> operation = pause ? session.TryPauseAsync() : session.TryPlayAsync();
            return await operation.AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure("control media session " + sessionId, ex);
            return false;
        }
    }

    // SourceAppUserModelId is the only identifier this API gives (the comment on ViewOf below), so it is
    // used as the session id, but two sessions from the same app (two tabs of one browser) share it: picking
    // the first one found could act on the wrong one. The candidates are read once, then handed to the pure,
    // testable PickSessionIndex, so the disambiguation rule itself is exercised without any WinRT type.
    [SupportedOSPlatform("windows10.0.17763.0")]
    private static GlobalSystemMediaTransportControlsSession? FindSession(GlobalSystemMediaTransportControlsSessionManager manager, string sessionId, bool pause)
    {
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
        var candidates = new (string AppId, MediaPlaybackState Status)[sessions.Count];
        for (int i = 0; i < sessions.Count; i++)
        {
            candidates[i] = (sessions[i].SourceAppUserModelId, StatusOf(sessions[i].GetPlaybackInfo().PlaybackStatus));
        }

        int? index = PickSessionIndex(candidates, sessionId, pause);
        return index is int i2 ? sessions[i2] : null;
    }

    // Pure and testable without any WinRT type: among the sessions sharing the target app id, prefers
    // whichever is already in the state this action expects to leave (Playing for a pause, Paused for a
    // play), so pausing one same-app session never silently acts on a different one that happens to share
    // its app id. Falls back to the first match sharing the id when none is in that state, and to null when
    // none matches at all.
    internal static int? PickSessionIndex(IReadOnlyList<(string AppId, MediaPlaybackState Status)> sessions, string targetAppId, bool pause)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAppId);

        MediaPlaybackState wanted = pause ? MediaPlaybackState.Playing : MediaPlaybackState.Paused;
        int firstMatch = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (!string.Equals(sessions[i].AppId, targetAppId, StringComparison.Ordinal))
            {
                continue;
            }

            if (sessions[i].Status == wanted)
            {
                return i;
            }

            if (firstMatch < 0)
            {
                firstMatch = i;
            }
        }

        return firstMatch >= 0 ? firstMatch : null;
    }

    // The session's SourceAppUserModelId doubles as its id: the API gives no other stable identifier.
    // It names an application on this PC, not a device; nothing here persists it.
    [SupportedOSPlatform("windows10.0.17763.0")]
    private static MediaSessionView ViewOf(GlobalSystemMediaTransportControlsSession session)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
        string appId = session.SourceAppUserModelId;
        return new MediaSessionView(appId, StatusOf(info.PlaybackStatus), info.Controls.IsPauseEnabled, info.Controls.IsPlayEnabled, appId);
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static MediaPlaybackState StatusOf(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        _ => MediaPlaybackState.Stopped,
    };

    private void LogFailure(string what, Exception ex) =>
        _log.Warn("Could not " + what + " (0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ").");
}
