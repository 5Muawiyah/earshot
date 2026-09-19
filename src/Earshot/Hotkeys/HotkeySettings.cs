namespace Earshot.Hotkeys;

// v1.1: the owner's global keyboard shortcuts, one per HotkeyAction. Nested in EarshotSettings as
// Hotkeys and persisted with it (Earshot.Infra.SettingsJsonContext already reaches this type through
// EarshotSettings, so it needs no source-generation attribute of its own).
//
// Defaults: switched off, every shortcut text empty. That is the whole point of the default: nothing is
// registered, and nothing is taken from another program, until the owner types a combination and turns
// hotkeys on. The texts are stored exactly as the owner typed them, not canonicalised on save, so the
// settings file still shows what they wrote if HotkeyText.TryParse later rejects it; the canonical form
// from HotkeyText.Format is only used in messages.
public sealed class HotkeySettings
{
    public bool Enabled { get; set; }

    public string ToggleConnection { get; set; } = string.Empty;

    public string ToggleAudioProtection { get; set; } = string.Empty;

    public string ToggleBlockAtBoot { get; set; } = string.Empty;

    public string SpeakStatus { get; set; } = string.Empty;

    public static HotkeySettings Defaults => new();

    // The text for an action, or an empty string. Never null.
    public string TextFor(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleConnection => ToggleConnection,
        HotkeyAction.ToggleAudioProtection => ToggleAudioProtection,
        HotkeyAction.ToggleBlockAtBoot => ToggleBlockAtBoot,
        HotkeyAction.SpeakStatus => SpeakStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown hotkey action."),
    };
}
