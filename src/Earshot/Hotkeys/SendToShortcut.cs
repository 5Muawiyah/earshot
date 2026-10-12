namespace Earshot.Hotkeys;

// Whether the Send to shortcut is a way in to the picker now: shortcuts are on and it holds a chord, so clearing the chord turns
// it off by itself. Whether the chord could be registered (another program may hold it) is the manager's outcome, not this.
internal static class SendToShortcut
{
    public static bool IsOn(HotkeySettings hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        return hotkeys.Enabled && !string.IsNullOrWhiteSpace(hotkeys.SendTo);
    }
}
