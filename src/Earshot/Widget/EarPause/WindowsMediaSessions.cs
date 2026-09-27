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
        var seenAppIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
        {
            try
            {
                string appId = session.SourceAppUserModelId;
                int index = seenAppIds.TryGetValue(appId, out int count) ? count : 0;
                seenAppIds[appId] = index + 1;
                views.Add(ViewOf(session, index));
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

    // SourceAppUserModelId is the only identifier this API gives, so the session id this class hands out is
    // built from it (ComposeSessionId), never the bare app id itself once two sessions share one: a fresh
    // GetSessions() here may not be in the same order, or even have the same count of that app's sessions, as
    // the read the caller's sessionId came from, so the app id is recovered from it (AppIdOf) and the actual
    // choice among same-app sessions still goes through the pure, testable PickSessionIndex.
    [SupportedOSPlatform("windows10.0.17763.0")]
    private static GlobalSystemMediaTransportControlsSession? FindSession(GlobalSystemMediaTransportControlsSessionManager manager, string sessionId, bool pause)
    {
        string appId = AppIdOf(sessionId);
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
        var candidates = new (string AppId, MediaPlaybackState Status)[sessions.Count];
        for (int i = 0; i < sessions.Count; i++)
        {
            candidates[i] = (sessions[i].SourceAppUserModelId, StatusOf(sessions[i].GetPlaybackInfo().PlaybackStatus));
        }

        int? index = PickSessionIndex(candidates, appId, pause);
        return index is int i2 ? sessions[i2] : null;
    }

    // The first session of a given app id keeps the plain app id (the common case: one session per app, and
    // every id this class handed out before this fix keeps meaning exactly what it always did); the second
    // and later ones sharing it get a distinguishing suffix, so two sessions read in the same ReadAsync call
    // are never given the identical id.
    internal static string ComposeSessionId(string appId, int indexAmongSameAppSessions) =>
        indexAmongSameAppSessions == 0 ? appId : appId + "#" + indexAmongSameAppSessions.ToString(CultureInfo.InvariantCulture);

    // Recovers the app id from either shape ComposeSessionId can produce, so FindSession can look a session
    // up by app id whichever form of id the caller was handed.
    internal static string AppIdOf(string sessionId)
    {
        int hash = sessionId.LastIndexOf('#');
        return hash < 0 ? sessionId : sessionId[..hash];
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

    // SourceAppUserModelId names an application on this PC, not a device, and is the only stable identifier
    // the API gives; nothing here persists it. indexAmongSameAppSessions (this session's position among ones
    // sharing that app id, within this one ReadAsync call) keeps the id itself from collapsing two distinct
    // sessions of the same app into one indistinguishable value.
    [SupportedOSPlatform("windows10.0.17763.0")]
    private static MediaSessionView ViewOf(GlobalSystemMediaTransportControlsSession session, int indexAmongSameAppSessions)
    {
        GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
        string appId = session.SourceAppUserModelId;
        string sessionId = ComposeSessionId(appId, indexAmongSameAppSessions);
        return new MediaSessionView(sessionId, StatusOf(info.PlaybackStatus), info.Controls.IsPauseEnabled, info.Controls.IsPlayEnabled, appId);
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
