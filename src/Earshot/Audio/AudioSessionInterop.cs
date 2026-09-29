using System.Runtime.InteropServices;

namespace Earshot.Audio;

// The audio session interfaces pause on leave reads with: which sessions on a render endpoint have an active
// stream. Vtable orders are the SDK's (um\audiopolicy.h), not the alphabetical order the documentation pages use.
// Read-only: no method that changes a session, its volume or its notifications is ever called, and every one this
// build does not call is declared only to keep the vtable in order. Like every interface in Interop\CoreAudio.cs
// these are used only on the audio worker's MTA thread, and every method keeps its HRESULT ([PreserveSig]).
//
// AudioSessionState, https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audiosessionstate
internal static class AudioSessionState
{
    // "The session has no active audio streams."
    internal const int Inactive = 0;

    // "The session has active audio streams."
    internal const int Active = 1;

    // "The session is dormant."
    internal const int Expired = 2;
}

// IAudioSessionManager2 is reached through IMMDevice::Activate (CoreAudio.Activate<T>). It derives from
// IAudioSessionManager, whose two methods come first in the vtable.
// https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionmanager2
[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager::GetAudioSessionControl, not called.
    [PreserveSig]
    int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);

    // IAudioSessionManager::GetSimpleAudioVolume, not called.
    [PreserveSig]
    int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);

    // ppSessionEnum is an owned reference: wrap it with ComActivation.TakeInterface<IAudioSessionEnumerator>.
    [PreserveSig]
    int GetSessionEnumerator(out nint sessionEnum);

    // The three below are not called; RegisterSessionNotification would add a listener, which this build never does.
    [PreserveSig]
    int RegisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(nint sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

// https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionenumerator
[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    // session is an owned reference to an IAudioSessionControl: wrap it with
    // ComActivation.TakeInterface<IAudioSessionControl2>, which asks for the derived interface by QueryInterface.
    [PreserveSig]
    int GetSession(int sessionIndex, out nint session);
}

// IAudioSessionControl2 derives from IAudioSessionControl, so its nine methods come first and are declared here in
// order: only GetState (from the base) and IsSystemSoundsSession are called. IsSystemSoundsSession returns S_OK for
// the system sounds session and S_FALSE for any other.
// https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessioncontrol2
// https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-issystemsoundssession
[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl::GetState. https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol-getstate
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName(out nint displayName);

    [PreserveSig]
    int SetDisplayName(nint value, nint eventContext);

    [PreserveSig]
    int GetIconPath(out nint iconPath);

    [PreserveSig]
    int SetIconPath(nint value, nint eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(nint groupingParam, nint eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(nint newNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(nint newNotifications);

    [PreserveSig]
    int GetSessionIdentifier(out nint sessionIdentifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out nint sessionInstanceIdentifier);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
