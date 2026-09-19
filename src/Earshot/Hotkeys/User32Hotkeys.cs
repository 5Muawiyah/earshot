using System.Runtime.InteropServices;
using Earshot.Interop;

namespace Earshot.Hotkeys;

// The only file in this feature that touches user32.dll. The RegisterHotKey and UnregisterHotKey
// declarations live in Earshot.Interop.NativeMethods, alongside the rest of this assembly's user32
// P/Invoke, and are read here through Marshal.GetLastPInvokeError on the very next statement after the
// call, before anything else can clear it.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getlastpinvokeerror
public sealed class User32Hotkeys : INativeHotkeys
{
    public NativeCallResult Register(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey)
    {
        bool succeeded = NativeMethods.RegisterHotKey(windowHandle, hotkeyId, modifiers, virtualKey);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded ? NativeCallResult.Success() : NativeCallResult.Failure(error);
    }

    public NativeCallResult Unregister(nint windowHandle, int hotkeyId)
    {
        bool succeeded = NativeMethods.UnregisterHotKey(windowHandle, hotkeyId);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded ? NativeCallResult.Success() : NativeCallResult.Failure(error);
    }
}
