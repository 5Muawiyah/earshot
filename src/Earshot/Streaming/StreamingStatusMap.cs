using System.Runtime.Versioning;
using Windows.Media.Audio;

namespace Earshot.Streaming;

// The two WinRT enums, mapped onto Earshot's own. Apart from WindowsStreamingPlatform this is the only file
// that names a Windows.* type, and it names these two so the mapping is checked against the real values
// rather than a copy of them. Both maps have a default arm that returns the unknown member instead of
// throwing, because a later Windows build may add a member.
// https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionopenresultstatus
// https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionstate
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class StreamingStatusMap
{
    public static StreamingOpenStatus FromWinRt(AudioPlaybackConnectionOpenResultStatus status) => status switch
    {
        AudioPlaybackConnectionOpenResultStatus.Success => StreamingOpenStatus.Open,
        AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => StreamingOpenStatus.TimedOut,
        AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => StreamingOpenStatus.DeniedBySystem,
        AudioPlaybackConnectionOpenResultStatus.UnknownFailure => StreamingOpenStatus.UnknownFailure,
        _ => StreamingOpenStatus.UnknownFailure,
    };

    public static StreamingLinkState FromWinRt(AudioPlaybackConnectionState state) => state switch
    {
        AudioPlaybackConnectionState.Closed => StreamingLinkState.Closed,
        AudioPlaybackConnectionState.Opened => StreamingLinkState.Opened,
        _ => StreamingLinkState.Unknown,
    };
}
