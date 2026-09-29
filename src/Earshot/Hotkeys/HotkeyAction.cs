namespace Earshot.Hotkeys;

// The commands a global keyboard shortcut can raise. This feature never performs one of these itself: it only
// names which one a registered key combination asked for. Connecting, protecting audio and blocking at boot are
// the host's own controllers; speaking a status aloud is the announcer's.
//
// SwitchToPc and SwitchToPhone are stated directions: a press asks for that end state whatever the tray last
// believed, unlike ToggleConnection, which does the opposite of what the tray shows. New members are only ever
// appended, because each one's hot key id is HotkeyManager.HotkeyIdBase plus its value.
public enum HotkeyAction
{
    ToggleConnection,
    ToggleAudioProtection,
    ToggleBlockAtBoot,
    SpeakStatus,
    SwitchToPc,
    SwitchToPhone,
}
