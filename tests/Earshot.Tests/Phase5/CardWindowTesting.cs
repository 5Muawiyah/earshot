using System.Globalization;
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

    public static void Run(Action work) => Run(_ => work());

    // Binds the calling thread to desktop, for a second thread that needs to see the same windows as the
    // one CardDesktop.Run started: UI Automation documents that its worker thread must own no window, so
    // it cannot be the thread Run itself binds.
    public static void BindCurrentThread(nint desktop)
    {
        DisableImeForThisThread();
        if (!SetThreadDesktop(desktop))
        {
            throw new AssertFailedException("SetThreadDesktop failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }
    }

    // Hands the created desktop's handle to work, so a second thread can SetThreadDesktop(sameDesktop)
    // and read UI Automation elements on that desktop too: the UIA worker thread must own no window, so
    // it cannot be the thread CardDesktop.Run already binds to the desktop.
    public static void Run(Action<nint> work)
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
                    DisableImeForThisThread();
                    if (!SetThreadDesktop(desktop))
                    {
                        throw new AssertFailedException("SetThreadDesktop failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                    }

                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                    work(desktop);
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
            // The work thread has ended. CloseDesktop fails while any thread in this process still has
            // the desktop as its thread desktop. The one cause found here was the Text Services
            // Framework's worker threads, created from a thread that activated a window with its IME
            // enabled and keeping that thread's desktop for the life of the process; the thread above
            // disables its IME before its first window, so they never start from a test desktop
            // (CardDesktopTextServicesTests). A short ERROR_BUSY while the ended thread's state is torn
            // down is still retried; running out of retries fails the test and names every thread that
            // still holds the desktop.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            CloseOrNameTheHolders(desktop);
        }

        failure?.Throw();
    }

    private static void CloseOrNameTheHolders(nint desktop)
    {
        try
        {
            RetryCloseDesktop(
                () =>
                {
                    bool closed = CloseDesktop(desktop);
                    return (closed, closed ? 0u : unchecked((uint)Marshal.GetLastPInvokeError()));
                },
                maxAttempts: 200,
                retryDelay: TimeSpan.FromMilliseconds(50),
                sleep: Thread.Sleep);
        }
        catch (AssertFailedException stuck)
        {
            throw new AssertFailedException(stuck.Message + " " + ThreadsHolding(desktop), stuck);
        }
    }

    internal const uint ErrorBusy = 170;

    // The CloseDesktop retry policy, extracted so its rules can be proven without a real desktop or real
    // timing: tolerate only ERROR_BUSY and keep retrying; anything else fails immediately; exhausting the
    // budget fails too, rather than being logged and swallowed as a security review found this doing
    // before. `attempt` performs one close attempt and reports whether it closed and, if not, the last
    // Win32 error; `sleep` runs between busy retries (Thread.Sleep in production, a no-op in tests).
    //
    // Windows documents CloseDesktop failing only when a thread in this process is still using the handle
    // (https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-closedesktop): it does not
    // document a distinct error code for that case versus any other reason the handle might still be busy
    // (a COM RCW not yet finalized, say), so this cannot tell "a thread is still bound to this desktop" -
    // the real defect a review asked to be caught - apart from the benign race by error code alone. Both
    // present as ERROR_BUSY. Exhausting the retry budget while still seeing ERROR_BUSY is therefore treated
    // as a failure rather than assumed benign: a genuinely still-bound thread is exactly what that outcome
    // would also look like, so it is not swallowed either.
    internal static void RetryCloseDesktop(Func<(bool Closed, uint LastError)> attempt, int maxAttempts, TimeSpan retryDelay, Action<TimeSpan> sleep)
    {
        uint lastError = 0;
        bool closed = false;
        for (int i = 0; i < maxAttempts && !closed; i++)
        {
            (closed, lastError) = attempt();
            if (!closed)
            {
                if (lastError != ErrorBusy)
                {
                    throw new AssertFailedException(
                        "CloseDesktop failed with Win32 error " + lastError.ToString(CultureInfo.InvariantCulture) +
                        ", not ERROR_BUSY (" + ErrorBusy.ToString(CultureInfo.InvariantCulture) +
                        "). Not retried: a code other than ERROR_BUSY is not the documented desktop-still-in-use " +
                        "race this retry budget exists for.");
                }

                sleep(retryDelay);
            }
        }

        if (!closed)
        {
            throw new AssertFailedException(
                "CloseDesktop did not succeed within its retry budget (last Win32 error " +
                lastError.ToString(CultureInfo.InvariantCulture) + "). See RetryCloseDesktop's own comment for " +
                "why exhausting the budget is not treated as a benign teardown race.");
        }
    }

    // Must run before the thread creates its first top-level window.
    // https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immdisableime
    private static void DisableImeForThisThread()
    {
        if (!ImmDisableIME(GetCurrentThreadId()))
        {
            throw new AssertFailedException("ImmDisableIME failed for a card test thread, so the Text Services Framework could bind its threads to the test's private desktop and stop it closing.");
        }
    }

    // Every thread of this process whose thread desktop is desktop, with the module its start address
    // falls in, for the failure message when CloseDesktop keeps reporting ERROR_BUSY.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getthreaddesktop
    private static string ThreadsHolding(nint desktop)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var holders = new List<string>();
        foreach (System.Diagnostics.ProcessThread thread in process.Threads)
        {
            if (GetThreadDesktop(thread.Id) == desktop)
            {
                holders.Add("thread " + thread.Id.ToString(CultureInfo.InvariantCulture) + " starting in " + StartModule(process, thread.Id));
            }
        }

        return holders.Count == 0
            ? "No thread of this process reports the desktop as its thread desktop."
            : "Threads still on the desktop: " + string.Join("; ", holders) + ".";
    }

    // ThreadQuerySetWin32StartAddress (9) through NtQueryInformationThread.
    // https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryinformationthread
    private static string StartModule(System.Diagnostics.Process process, int threadId)
    {
        const uint ThreadQueryInformation = 0x0040;
        nint handle = OpenThread(ThreadQueryInformation, false, (uint)threadId);
        if (handle == 0)
        {
            return "(OpenThread failed, Win32 error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ")";
        }

        nint start = 0;
        int status;
        try
        {
            status = NtQueryInformationThread(handle, 9, ref start, nint.Size, 0);
        }
        finally
        {
            CloseHandle(handle);
        }

        if (status != 0)
        {
            return "(NtQueryInformationThread status 0x" + status.ToString("X8", CultureInfo.InvariantCulture) + ")";
        }

        foreach (System.Diagnostics.ProcessModule module in process.Modules)
        {
            long moduleBase = module.BaseAddress;
            if (start >= moduleBase && start < moduleBase + module.ModuleMemorySize)
            {
                return module.ModuleName;
            }
        }

        return "0x" + start.ToString("X", CultureInfo.InvariantCulture);
    }

    [DllImport("imm32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmDisableIME(uint idThread);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationThread(nint threadHandle, int threadInformationClass, ref nint threadInformation, int threadInformationLength, nint returnLength);

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

// CardDesktop.RetryCloseDesktop's own policy, proven without a real desktop: a fake attempt() sequence
// drives it through the busy-then-succeeds, non-busy-error, and budget-exhausted paths.
[TestClass]
public sealed class CardDesktopRetryPolicyTests
{
    [TestMethod]
    public void SucceedsAfterRetryingOnlyOnErrorBusy()
    {
        var attempts = new Queue<(bool, uint)>([(false, CardDesktop.ErrorBusy), (false, CardDesktop.ErrorBusy), (true, 0u)]);
        int sleeps = 0;

        CardDesktop.RetryCloseDesktop(() => attempts.Dequeue(), maxAttempts: 10, retryDelay: TimeSpan.FromMilliseconds(50), sleep: _ => sleeps++);

        Assert.AreEqual(2, sleeps, "One sleep per busy retry before the attempt that succeeds.");
    }

    [TestMethod]
    public void AnErrorOtherThanBusyFailsImmediatelyWithoutRetrying()
    {
        const uint AccessDenied = 5;
        int attemptCount = 0;
        int sleeps = 0;

        var ex = Assert.ThrowsExactly<AssertFailedException>(() =>
            CardDesktop.RetryCloseDesktop(
                () =>
                {
                    attemptCount++;
                    return (false, AccessDenied);
                },
                maxAttempts: 10,
                retryDelay: TimeSpan.FromMilliseconds(50),
                sleep: _ => sleeps++));

        Assert.AreEqual(1, attemptCount, "A non-ERROR_BUSY code must not be retried.");
        Assert.AreEqual(0, sleeps);
        StringAssert.Contains(ex.Message, "5");
    }

    // The defect a security review found: exhausting the retry budget while every attempt reports
    // ERROR_BUSY used to be logged and swallowed, which also swallowed a genuinely still-bound thread
    // (indistinguishable from the benign race by error code alone). It must now fail the test.
    [TestMethod]
    public void ExhaustingTheBudgetOnPersistentErrorBusyFailsRatherThanSwallowing()
    {
        int attemptCount = 0;
        int sleeps = 0;

        Assert.ThrowsExactly<AssertFailedException>(() =>
            CardDesktop.RetryCloseDesktop(
                () =>
                {
                    attemptCount++;
                    return (false, CardDesktop.ErrorBusy);
                },
                maxAttempts: 5,
                retryDelay: TimeSpan.FromMilliseconds(50),
                sleep: _ => sleeps++));

        Assert.AreEqual(5, attemptCount);
        Assert.AreEqual(5, sleeps);
    }
}

// The input method layer must never reach a CardDesktop.Run thread. Activating a window with the IME
// enabled starts the Text Services Framework's process-wide worker threads (their start addresses are in
// msctfmonitor.dll and MSCTF.dll); they are created from the activating thread, so they take its private
// desktop as their thread desktop and keep it for the life of the process, and CloseDesktop then fails
// with ERROR_BUSY forever, because "CloseDesktop will fail if any thread in the calling process is using
// the specified desktop handle". Which test's desktop they land on depends on test order, and a later
// desktop that happens to reuse the same handle value is caught too. Checked through the thread's default
// IME window, which exists for every thread whose IME is enabled and never for one whose IME is disabled,
// so this holds whatever order the tests run in.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-closedesktop
// https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetdefaultimewnd
[TestClass]
public sealed class CardDesktopTextServicesTests
{
    [TestMethod]
    public void AWindowOnACardDesktopThreadHasNoImeWindow()
    {
        nint imeWindow = -1;
        CardDesktop.Run(() =>
        {
            using var form = new Form { ShowInTaskbar = false };
            imeWindow = ImmGetDefaultIMEWnd(form.Handle);
        });

        Assert.AreEqual(0, imeWindow, "A CardDesktop.Run thread must have its IME disabled before its first window, or the Text Services Framework's threads bind to its private desktop and it can never be closed.");
    }

    [DllImport("imm32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint ImmGetDefaultIMEWnd(nint hWnd);
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

    // The window that currently holds mouse capture, or 0 when none does.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcapture
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern nint GetCapture();
}
