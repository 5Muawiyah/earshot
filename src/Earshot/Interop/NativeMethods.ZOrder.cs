using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The user32 calls that tie one window's place in the z-order to another's. A widget type may not declare a native
// call of its own (WidgetAtRestTests), so they sit here, on the type the widget is already allowed to reach.
internal static partial class NativeMethods
{
    // GWLP_HWNDPARENT: for a top-level window the index that sets its owner (not a parent: the window stays top-level).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw
    internal const int GWLP_HWNDPARENT = -8;

    // GetWindow's GW_OWNER: the window's owner, or 0 when it has none.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindow
    internal const uint GW_OWNER = 4;

    // Sets the owner of a top-level window. The previous owner comes back (0 when there was none), so 0 is also what a
    // failure returns: the generated call clears the last error before it and keeps it after, and the caller reads it
    // only when the function returned 0.
    // "An owned window is always above its owner in the z-order", wherever the owner is moved to.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows
    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    // The window's owner (GW_OWNER) or 0.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindow
    [LibraryImport(User32, SetLastError = true)]
    internal static partial nint GetWindow(nint hWnd, uint uCmd);
}
