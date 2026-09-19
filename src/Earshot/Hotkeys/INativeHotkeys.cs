namespace Earshot.Hotkeys;

// The two native calls this feature makes. Tests use a fake; User32Hotkeys is the only implementation
// that touches user32.dll.
public interface INativeHotkeys
{
    NativeCallResult Register(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey);

    NativeCallResult Unregister(nint windowHandle, int hotkeyId);
}
