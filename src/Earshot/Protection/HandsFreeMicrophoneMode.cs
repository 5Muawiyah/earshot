using System.Text.RegularExpressions;
using Earshot.Widget;

namespace Earshot.Contracts;

// SettingsUri is the page the button opens: the AirPods' microphone when its id is known and well formed, otherwise
// the list of sound devices.
internal sealed record MicrophoneRow(MicrophoneRowState State, string SettingsUri);

// The Hands-Free "microphone off" mode, as pure rules with no device, window or clock. It lives with the other pure
// settings and node rules in Contracts. The row's state (MicrophoneRowState) is the widget's own type, because the widget
// may reach only its own types and never the device path.
//
// What the mode is: Protect audio quality turned off, so the Hands-Free link stays up (which may let Windows read the
// battery; that has not been observed with this link up on this PC), and the person switching the AirPods' Hands-Free microphone off in Windows' sound settings, so an app that opens a
// microphone finds nothing to switch the AirPods to. Earshot cannot switch that microphone off itself. Core Audio
// documents reading an endpoint's state (DEVICE_STATE_DISABLED) and no call that sets it, and the interface that does is
// undocumented, so the mode opens Windows' own page and says what to press.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants
//
// The mode changes the protection intent and nothing else. The idle block, the boot block and the hand-back select
// nodes by container and address, not by what the mode says, so a Hands-Free service the mode leaves installed is
// blocked with the rest and the AirPods stay unpaged at rest (NodeMatch.IsDisableTarget).
internal static partial class HandsFreeMicrophoneMode
{
    public const string SoundDevicesUri = "ms-settings:sound-devices";

    private const string SoundPropertiesPrefix = "ms-settings:sound-properties?endpointId=";

    // A capture endpoint's id, in the form the id of an AirPods capture endpoint has been seen to have on this PC:
    // {0.0.1.<8 hex>}.{<GUID>}. Core Audio documents endpoint ids as opaque strings, so no part of that form is a
    // documented promise (the 1 is the digit those capture ids carry, not a documented flow code); the check is a
    // precaution that only an id of exactly that form is put into the settings address, and any other id opens the list
    // of sound devices instead. \z, not $, so a trailing line break does not pass.
    // https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings
    // https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
    [GeneratedRegex(@"^\{0\.0\.1\.[0-9A-Fa-f]{8}\}\.\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z")]
    private static partial Regex CaptureEndpointId();

    // The mode as the settings say it. Protect audio quality wins: both set reads as off.
    public static bool IsOn(EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.HandsFreeMicrophoneOffMode;
    }

    // Turning the mode on turns protection off; turning it off turns protection back on. Both are the saved intent: the
    // protection coordinator applies it, and at rest it is only kept until the nodes are next allowed.
    public static void Switch(EarshotSettings settings, bool on)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ProtectAudioQuality = !on;
        settings.HandsFreeMicrophoneOffMode = on;
    }

    // Protect audio quality chosen on its own (the menu item, its shortcut): either way the mode is off, because the
    // mode is a state of protection that was reached through the mode.
    public static void ChooseProtection(EarshotSettings settings, bool protect)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ProtectAudioQuality = protect;
        settings.HandsFreeMicrophoneOffMode = false;
    }

    public static bool IsCaptureEndpointId(string? id) => id is not null && CaptureEndpointId().IsMatch(id);

    // The page for one endpoint, or the list of sound devices when there is none or its id is not in the form Settings takes.
    public static string SettingsUriFor(AudioEndpoint? endpoint) =>
        endpoint is not null && IsCaptureEndpointId(endpoint.EndpointId) ? SoundPropertiesPrefix + endpoint.EndpointId : SoundDevicesUri;

    // The AirPods' capture endpoint in the snapshot's target group that is there to open: active or unplugged first, a
    // disabled one only when no other is. Null when the group has none.
    public static AudioEndpoint? CaptureEndpoint(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AudioEndpoint? disabled = null;
        AudioEndpoint? unplugged = null;
        foreach (AudioEndpoint endpoint in snapshot.Target?.Endpoints ?? [])
        {
            if (endpoint.Flow != EndpointFlow.Capture)
            {
                continue;
            }

            if (endpoint.State.HasFlag(EndpointState.Active))
            {
                return endpoint;
            }

            if (endpoint.State.HasFlag(EndpointState.Unplugged))
            {
                unplugged ??= endpoint;
            }
            else if (endpoint.State.HasFlag(EndpointState.Disabled))
            {
                disabled ??= endpoint;
            }
        }

        return unplugged ?? disabled;
    }

    public static MicrophoneRow Describe(DeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AudioEndpoint? capture = CaptureEndpoint(snapshot);
        if (capture is null)
        {
            // A read that failed says nothing about whether the microphone exists, so the person is not told to connect.
            return snapshot.ReadStatus == SnapshotReadStatus.Ok
                ? new MicrophoneRow(MicrophoneRowState.ConnectFirst, SoundDevicesUri)
                : new MicrophoneRow(MicrophoneRowState.OpenSettings, SoundDevicesUri);
        }

        MicrophoneRowState state = capture.State.HasFlag(EndpointState.Disabled) && !capture.State.HasFlag(EndpointState.Active)
            ? MicrophoneRowState.OffInWindows
            : MicrophoneRowState.OpenSettings;
        return new MicrophoneRow(state, SettingsUriFor(capture));
    }
}
