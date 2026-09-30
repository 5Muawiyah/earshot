using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The user32 calls the gauge's event log and its foreground hook use. A widget type may not declare a native
// call of its own (WidgetAtRestTests), so they sit here, on the type the widget is already allowed to reach.
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

    // SetWinEventHook flags. WINEVENT_OUTOFCONTEXT: the callback is not mapped into the process that raises
    // the event, and the event is delivered on the thread that called SetWinEventHook, which must pump
    // messages. WINEVENT_SKIPOWNPROCESS: no events raised by threads in this process.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNTHREAD = 0x0001;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    internal const uint WINEVENT_INCONTEXT = 0x0004;

    // "The foreground window has changed. The system sends this event even if the foreground window has
    // changed to another window in the same thread." hwnd is the new foreground window, idObject is
    // OBJID_WINDOW and idChild is CHILDID_SELF.
    // https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    // "An object has been shown" and "An object is hidden". hwnd is the window, idObject OBJID_WINDOW and idChild
    // CHILDID_SELF for the window itself. The two values are adjacent, so one hook covers exactly both.
    // https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint EVENT_OBJECT_HIDE = 0x8003;
    internal const int OBJID_WINDOW = 0;
    internal const int CHILDID_SELF = 0;

    // Installs a hook for the events from eventMin to eventMax. hmodWinEventProc is 0 for an out-of-context
    // hook; idProcess and idThread of 0 mean every process and thread on the current desktop. Returns the
    // hook handle, or 0 on failure. The callback is WINEVENTPROC:
    // (hWinEventHook, event, hwnd, idObject, idChild, idEventThread, dwmsEventTime).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nc-winuser-wineventproc
    // pfnWinEventProc is the address of an [UnmanagedCallersOnly] function of that shape, passed as a plain
    // pointer-sized integer so no function pointer type appears in a signature the widget calls.
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc, nint pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    // Removes a hook. Must be called from the thread that installed it. True on success.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwinevent
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hWinEventHook);
}
