using System.Runtime.Versioning;

namespace Earshot.Streaming;

// The CA1416 guard, written once. Every call into Windows.Media.Audio sits behind it.
//
// Windows.Media.Audio.AudioPlaybackConnection: "Windows 10, version 2004 (introduced in 10.0.19041.0)".
// https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection
// The projection this build compiles against says the same: with the guard removed, the analyser reports
// "'AudioPlaybackConnection.GetDeviceSelector()' is only supported on: 'Windows' 10.0.19041.0 and later".
//
// The guard is real because SupportedOSPlatformVersion stays below 10.0.19041.0 (Directory.Build.props). The
// analyser recognises a member annotated this way, so nothing is ever suppressed.
// https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1416
internal static class PlatformGuard
{
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    internal static bool HasAudioPlaybackConnection { get; } =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}
