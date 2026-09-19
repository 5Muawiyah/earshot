namespace Earshot.Hotkeys;

public sealed class HotkeyActivatedEventArgs(HotkeyAction action) : EventArgs
{
    public HotkeyAction Action { get; } = action;
}
