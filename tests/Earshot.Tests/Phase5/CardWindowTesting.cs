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

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
