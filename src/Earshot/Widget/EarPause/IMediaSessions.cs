using Earshot.Contracts;

namespace Earshot.Widget.EarPause;

// Mirrors GlobalSystemMediaTransportControlsSessionPlaybackStatus, so nothing outside WindowsMediaSessions
// ever names the WinRT type.
// https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionplaybackstatus
public enum MediaPlaybackState { Closed, Changing, Stopped, Playing, Paused }

public sealed record MediaSessionView(
    string SessionId, MediaPlaybackState PlaybackStatus, bool IsPauseEnabled, bool IsPlayEnabled, string? SourceAppUserModelId);

// The whole of Windows Media Controls, as far as auto-pause is concerned: one real implementation
// (WindowsMediaSessions), a safe-mode decorator (SafeMediaSessions) and a fake in tests.
internal interface IMediaSessions
{
    // A read of every current session. Never pauses, plays or changes anything.
    Task<IReadOnlyList<MediaSessionView>> ReadAsync(CancellationToken ct);

    Task<bool> TryPauseAsync(string sessionId, CancellationToken ct);

    Task<bool> TryPlayAsync(string sessionId, CancellationToken ct);

    // The session id whose playback info changed, for stage 2. Raised on whatever thread Windows calls back on.
    event EventHandler<string>? PlaybackInfoChanged;
}
