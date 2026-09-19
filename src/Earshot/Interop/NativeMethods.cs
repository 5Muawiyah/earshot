using System.Runtime.InteropServices;

// Every P/Invoke in this assembly resolves its DLL from System32 only, never from the
// application folder or the current directory.
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Earshot.Interop;

// kernel32 calls made before any run mode starts, plus the user32 window calls and message constants
// shared by the tray, the shell message window and the popup card.
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";
    private const string User32 = "user32.dll";

    // SetDefaultDllDirectories flags.
    // https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories
    internal const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
    internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    // AttachConsole: use the console of the parent process. (DWORD)-1.
    // https://learn.microsoft.com/en-us/windows/console/attachconsole
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // AttachConsole fails with this when the process already has a console.
    internal const int ERROR_ACCESS_DENIED = 5;

    // GetStdHandle: standard output is (DWORD)-11. The call returns NULL when the process has no
    // standard handle (a Windows-subsystem program started without redirection) and
    // INVALID_HANDLE_VALUE when it fails.
    // https://learn.microsoft.com/en-us/windows/console/getstdhandle
    internal const uint STD_OUTPUT_HANDLE = 0xFFFFFFF5;
    internal const nint INVALID_HANDLE_VALUE = -1;

    // GetFileType results. FILE_TYPE_UNKNOWN is also returned when the call fails.
    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletype
    internal const uint FILE_TYPE_UNKNOWN = 0x0000;
    internal const uint FILE_TYPE_DISK = 0x0001;
    internal const uint FILE_TYPE_CHAR = 0x0002;
    internal const uint FILE_TYPE_PIPE = 0x0003;
    internal const uint FILE_TYPE_REMOTE = 0x8000;

    // Window messages (WinUser.h).
    // WM_QUERYENDSESSION: return TRUE at once and defer work; a windowless app is killed about 5 s in and
    // EWX_FORCE sends no query at all.
    // https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession
    internal const int WM_QUERYENDSESSION = 0x0011;

    // https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession
    internal const int WM_ENDSESSION = 0x0016;

    // WM_SETTINGCHANGE is WM_WININICHANGE. Broadcast to top-level windows only, never message-only ones.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange
    internal const int WM_SETTINGCHANGE = 0x001A;

    // https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate
    internal const int WM_MOUSEACTIVATE = 0x0021;

    // https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange
    internal const int WM_DISPLAYCHANGE = 0x007E;

    // https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged
    internal const int WM_DPICHANGED = 0x02E0;

    // https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-themechanged
    internal const int WM_THEMECHANGED = 0x031A;

    // "Sent as a signal that a window or an application should terminate."
    // https://learn.microsoft.com/windows/win32/winmsg/wm-close
    internal const int WM_CLOSE = 0x0010;

    // Posted to the window that registered a hot key with RegisterHotKey.
    // https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey
    internal const int WM_HOTKEY = 0x0312;

    // WM_QUERYENDSESSION / WM_ENDSESSION lParam bits. 0 means shutdown or restart.
    internal const uint ENDSESSION_CLOSEAPP = 0x00000001;
    internal const uint ENDSESSION_CRITICAL = 0x40000000;
    internal const uint ENDSESSION_LOGOFF = 0x80000000;

    // WM_MOUSEACTIVATE results. The card answers MA_NOACTIVATE so a click never takes focus.
    internal const int MA_ACTIVATE = 1;
    internal const int MA_ACTIVATEANDEAT = 2;
    internal const int MA_NOACTIVATE = 3;
    internal const int MA_NOACTIVATEANDEAT = 4;

    // Extended window styles for the card's CreateParams. WS_EX_TOPMOST goes in CreateParams, never through
    // the Form.TopMost setter, which activates the window.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    // ShowWindow command that shows without activating.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow
    internal const int SW_SHOWNOACTIVATE = 4;

    // SetWindowPos insert-after handles and flags. Without SWP_NOACTIVATE the window is activated.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
    internal const nint HWND_TOPMOST = -1;
    internal const nint HWND_NOTOPMOST = -2;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOREDRAW = 0x0008;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    // Removes the current directory and PATH from the DLL search order for every later load.
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);

    // https://learn.microsoft.com/en-us/windows/console/getstdhandle
    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial nint GetStdHandle(uint nStdHandle);

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletype
    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint GetFileType(nint hFile);

    // Closes a kernel handle, such as a Bluetooth radio handle from BluetoothFindFirstRadio.
    // https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-closehandle
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // fsModifiers is one of HotkeyModifiers combined with MOD_NOREPEAT (0x4000); vk is a virtual-key
    // code. id must stay in 0x0000 to 0xBFFF: 0xC000 and above is reserved for shared DLLs, which take
    // theirs from GlobalAddAtom. Nonzero on success; on failure the caller reads
    // Marshal.GetLastPInvokeError on the very next statement, before anything else can clear it.
    // "If a hot key already exists with the same hWnd and id parameters, it is maintained along with
    // the new hot key" on every OS this assembly targets: a second RegisterHotKey call for the same id
    // does not replace the first, so the caller must UnregisterHotKey it first.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    // Returns the id of the thread that created hWnd, and through lpdwProcessId the id of its process.
    // Used to check that a window call is made on the thread that owns the window, since a captured
    // managed thread id is not necessarily the same OS thread a window's message queue belongs to.
    // "If the window handle is invalid, the return value is zero", so a destroyed window is owned by no thread.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid
    [LibraryImport(User32)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentthreadid
    [LibraryImport(Kernel32)]
    internal static partial uint GetCurrentThreadId();

    // "Frees a hot key previously registered by the calling thread." The docs do not say a hot key is
    // released when its window is destroyed or the process ends, so the caller unregisters explicitly,
    // on the thread that registered it, before the handle goes.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);
}
