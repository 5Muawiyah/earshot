using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The two user32 calls the gauge's event log uses to say which window covered it or took the foreground.
// A widget type may not declare a native call of its own (WidgetAtRestTests), so they sit here, on the
// type the widget is already allowed to reach.
internal static partial class NativeMethods
{
    // Copies the window's class name into lpClassName, at most nMaxCount characters including the null.
    // Returns the length copied, or 0 on failure (a window that closed in the meantime).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclassnamew
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    internal static unsafe partial int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);

    // The window the user is working with, or 0 when there is none (during a switch).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();
}
