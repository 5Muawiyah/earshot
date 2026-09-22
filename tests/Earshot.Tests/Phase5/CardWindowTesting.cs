using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// Runs WinForms work on its own STA thread and rethrows its exception on the caller.
internal static class CardSta
{
    public static void Run(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot card test STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new AssertFailedException("The STA work did not finish within 60 seconds.");
        }

        failure?.Throw();
    }

    // Pumps messages on the current thread until done returns true or the limit passes.
    public static bool PumpUntil(Func<bool> done, TimeSpan limit)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > limit)
            {
                return false;
            }

            Application.DoEvents();
            Thread.Sleep(5);
        }

        return true;
    }
}

// Runs window work on its own thread attached to a new, private desktop, and rethrows its exception on the
// caller. Windows there are never on the input desktop, so a test can show, activate and focus its own
// windows without drawing on the screen or taking the foreground from whatever the user is doing.
//
// The thread is not STA: starting an STA thread creates a COM window on it, and SetThreadDesktop fails
// for a thread that already has a window. Activation and focus belong to the window manager, not COM.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop
internal static class CardDesktop
{
    // DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU | DESKTOP_WRITEOBJECTS
    // https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights
    private const uint DesktopAccess = 0x0001 | 0x0002 | 0x0004 | 0x0080;

    public static void Run(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        nint desktop = CreateDesktopW("EarshotCardTest-" + Guid.NewGuid().ToString("N"), 0, 0, 0, DesktopAccess, 0);
        if (desktop == 0)
        {
            throw new AssertFailedException("CreateDesktopW failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (!SetThreadDesktop(desktop))
                    {
                        throw new AssertFailedException("SetThreadDesktop failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                    }

                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                    work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Earshot card test desktop",
            };
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60)))
            {
                throw new AssertFailedException("The desktop work did not finish within 60 seconds.");
            }
        }
        finally
        {
            // The thread has ended, so nothing is attached to the desktop any more.
            if (!CloseDesktop(desktop))
            {
                failure ??= ExceptionDispatchInfo.Capture(new AssertFailedException(
                    "CloseDesktop failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + "."));
            }
        }

        failure?.Throw();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateDesktopW(string lpszDesktop, nint lpszDevice, nint pDevmode, uint dwFlags, uint dwDesiredAccess, nint lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint hDesktop);
}

// Read-only window queries for the card tests. Nothing here changes a window other than the test's own.
internal static class TestWindows
{
    public const int GWL_EXSTYLE = -20;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;

    public static long ExtendedStyle(nint hwnd) => GetWindowLongPtrW(hwnd, GWL_EXSTYLE);

    public static nint Send(nint hwnd, int message) => SendMessageW(hwnd, (uint)message, 0, 0);

    // With explicit wParam/lParam, for a message whose meaning depends on them (WM_QUERYENDSESSION,
    // WM_ENDSESSION, WM_POWERBROADCAST): SendMessage blocks the calling thread until the receiving thread's
    // window procedure has returned, which is exactly the real behaviour a hand-back's held reply depends on.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagew
    public static nint Send(nint hwnd, int message, nint wParam, nint lParam) => SendMessageW(hwnd, (uint)message, wParam, lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    // The active window and the focus window of the calling thread's message queue.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getactivewindow
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getfocus
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern nint GetFocus();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
