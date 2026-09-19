namespace Earshot.Hotkeys;

// A parsed shortcut: the modifier keys and the one virtual key pressed with them.
public readonly record struct HotkeyCombination(HotkeyModifiers Modifiers, ushort VirtualKey);
