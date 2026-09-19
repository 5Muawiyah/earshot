namespace Earshot.Hotkeys;

// RegisterHotKey's fsModifiers bits. This is deliberately not System.Windows.Forms.Keys: that enum's
// own Control field is 0x20000 (not MOD_CONTROL 0x0002) and it has no Windows-key member, so a Keys
// value must never be passed as fsModifiers.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.keys
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,      // MOD_ALT
    Control = 0x0002,  // MOD_CONTROL
    Shift = 0x0004,    // MOD_SHIFT
    Windows = 0x0008,  // MOD_WIN
}
