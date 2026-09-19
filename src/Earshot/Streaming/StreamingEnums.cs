namespace Earshot.Streaming;

// v1.1 audio streaming: this PC as a Bluetooth audio sink for a phone or another source, through
// Windows.Media.Audio.AudioPlaybackConnection. A different Bluetooth role from the one the rest of Earshot
// manages (the PC as a source for the AirPods); nothing in this folder touches the AirPods, their device
// nodes, the block controller or the SYSTEM tasks.
// https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/enable-remote-audio-playback

// Whether this build of Windows can do the feature at all.
// NotChecked is the zero value, so nothing that was never checked reads as supported.
internal enum StreamingSupport
{
    NotChecked,   // nobody has asked yet
    Supported,    // build is 10.0.19041 or later and the WinRT type answered
    BuildTooOld,  // OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) was false
    TypeMissing,  // build is new enough but the type did not answer at runtime
}

internal enum StreamingDiscoveryStatus
{
    NotStarted,
    Ok,           // the enumeration ran; Devices is what it found, and may be empty
    Failed,       // the enumeration threw or ran out of time; Devices is empty and Step says why
    Unsupported,  // the support check failed, so no enumeration was attempted
}

internal enum StreamingEnableStatus
{
    NotStarted,
    Enabled,           // TryCreateFromId returned an object and StartAsync completed
    NotStreamCapable,  // TryCreateFromId returned null, which is documented and is not an error
    StartFailed,       // TryCreateFromId or StartAsync threw; Step names which
    Unsupported,
    Excluded,          // the device is the one Earshot manages; nothing was attempted
}

internal enum StreamingOpenStatus
{
    NotStarted,
    Open,            // OpenAsync returned Success
    TimedOut,        // OpenAsync returned RequestTimedOut, or Earshot's own limit ran out first
    DeniedBySystem,  // OpenAsync returned DeniedBySystem
    UnknownFailure,  // OpenAsync returned UnknownFailure
    CallFailed,      // OpenAsync itself threw, so there is no status from Windows
    NotEnabled,      // no enabled connection exists for that id
    Unsupported,
}

// Mirrors Windows.Media.Audio.AudioPlaybackConnectionState, plus an Unknown zero value because the WinRT
// enum has no "we have not heard yet" member. There is no Connecting, Opening or Failed: Windows has none.
// https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnectionstate
internal enum StreamingLinkState
{
    Unknown,
    Closed,
    Opened,
}

// What a click on one item of the Play from a phone submenu asks for.
internal enum StreamingMenuCommand
{
    None,     // a sentence, never clickable
    Play,     // start playing from DeviceId
    Stop,     // stop accepting audio from the device in use
    Refresh,  // read the list of paired devices again
}
