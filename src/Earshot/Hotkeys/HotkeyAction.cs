namespace Earshot.Hotkeys;

// The four commands a global keyboard shortcut can raise. This feature never performs one of these
// itself: it only names which one a registered key combination asked for. Connecting, protecting
// audio and blocking at boot are the host's own controllers; speaking a status aloud is a separate
// feature that this build does not have, so SpeakStatus currently reaches no code that acts on it.
public enum HotkeyAction
{
    ToggleConnection,
    ToggleAudioProtection,
    ToggleBlockAtBoot,
    SpeakStatus,
}
