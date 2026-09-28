using System.Runtime.InteropServices;
using Earshot.Tests.TestWindow;

namespace Earshot.Tests.Widget;

// One row this process's own top-level window was shown, or took the foreground, on a desktop that is
// not one of CardDesktop.Run's own private ones.
internal readonly record struct TopLevelWindowViolation(
    string EventName, string ClassName, string WindowTitle, string DesktopName, DateTime UtcTimestamp)
{
    public override string ToString() =>
        UtcTimestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + EventName +
        " class=\"" + ClassName + "\" title=\"" + WindowTitle + "\" desktop=\"" + DesktopName + "\"";
}

// A structural, process-wide safety net: this class of defect (a real window reaching the owner's own
// desktop from a test or a probe run) has now come back four times - the test window's own form, the
// gauge, and twice over in WidgetCard's own capture path (Control.DrawToBitmap on a top-level,
// activatable Form was found to make it briefly visible, and briefly take the foreground, to do its own
// internal layout, on whatever desktop the calling thread happened to be attached to). A per-class
// construction counter, the kind WidgetRealSurfaceGuardTests already keeps for the four real widget
// surfaces, only ever catches what someone remembered to instrument; this instead watches the desktop
// itself, at the OS level, for the one fact that actually matters regardless of which code path caused it:
// did a top-level window belonging to this process ever become visible, or take the foreground, anywhere
// but a private test desktop.
//
// SetWinEventHook(EVENT_SYSTEM_FOREGROUND) and SetWinEventHook(EVENT_OBJECT_SHOW), both WINEVENT_OUTOFCONTEXT
// so the callback runs in this process with no injected DLL, installed from a dedicated thread that does
// nothing else but pump the message queue those callbacks are delivered through. Every event for a window
// this process owns is checked against the desktop its owning thread belongs to (TestDesktop.NameForThread):
// CardDesktop.Run's own desktops are all named "EarshotCardTest-" plus a GUID, so anything else - most of
// all the owner's real "Default" desktop - is a violation, recorded and reported by AssemblyCleanup, never
// swallowed.
//
// What this cannot do: attribute a violation to the specific test that caused it. MSTest exposes the
// current test's name only through a TestContext property each test class must declare and MSTest injects
// per instance; there is no assembly-wide "which test is running now" this static hook can read without
// that same plumbing in every test class in the assembly, which is a far larger change than this guard
// itself. The timestamp, window class name and window title are what AssemblyCleanup reports instead: an
// owner or a reviewer correlates that against the test log's own timestamps to find the run that caused it.
// https://learn.microsoft.com/en-us/windows/win32/winauto/entry-winevents-overview
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
internal static class TopLevelWindowVisibilityGuard
{
    private const uint WinEventOutOfContext = 0x0000;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint WmQuit = 0x0012;
    private const int GwlStyle = -16;
    private const int WsChild = unchecked((int)0x40000000);

    private static readonly List<TopLevelWindowViolation> ViolationsList = new();
    private static readonly Lock Gate = new();
    private static readonly WinEventDelegate Callback = OnWinEvent;

    private static Thread? _hookThread;
    private static uint _hookThreadId;
    private static nint _foregroundHook;
    private static nint _showHook;
    private static readonly ManualResetEventSlim Ready = new(initialState: false);
    private static readonly uint CurrentProcessId = GetCurrentProcessId();

    public static IReadOnlyList<TopLevelWindowViolation> Violations
    {
        get
        {
            lock (Gate)
            {
                return ViolationsList.ToArray();
            }
        }
    }

    // Installs both hooks on a dedicated message-pumping thread and waits for them to be live before
    // returning, so a window shown immediately after this call is never missed by a hook not yet armed.
    public static void Start()
    {
        lock (Gate)
        {
            ViolationsList.Clear();
        }

        Ready.Reset();
        _hookThread = new Thread(PumpAndHook) { IsBackground = true, Name = "Earshot top-level window guard" };
        _hookThread.Start();
        if (!Ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("TopLevelWindowVisibilityGuard's hook thread did not start within 10 seconds.");
        }
    }

    public static void Stop()
    {
        if (_hookThread is null)
        {
            return;
        }

        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }

        if (_showHook != 0)
        {
            UnhookWinEvent(_showHook);
            _showHook = 0;
        }

        PostThreadMessage(_hookThreadId, WmQuit, 0, 0);
        _hookThread.Join(TimeSpan.FromSeconds(10));
        _hookThread = null;
    }

    private static void PumpAndHook()
    {
        _hookThreadId = GetCurrentThreadId();
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, Callback, 0, 0, WinEventOutOfContext);
        _showHook = SetWinEventHook(EventObjectShow, EventObjectShow, 0, Callback, 0, 0, WinEventOutOfContext);
        Ready.Set();

        // GetMessage returns a tristate int (-1 on error, 0 on WM_QUIT, nonzero otherwise), not a plain
        // bool: -1 marshaled as a 4-byte BOOL would read as "true" and loop forever on an error.
        while (GetMessage(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint eventTime)
    {
        if (hwnd == 0)
        {
            return;
        }

        // EVENT_OBJECT_SHOW fires for controls inside a window too; only the window object itself, becoming
        // visible as a whole, is what "a top-level window was shown" means here.
        if (eventType == EventObjectShow && (idObject != ObjIdWindow || idChild != ChildIdSelf))
        {
            return;
        }

        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId != CurrentProcessId)
        {
            return;
        }

        // A child control window can still raise EVENT_SYSTEM_FOREGROUND-adjacent noise in principle; this
        // guard is about top-level windows, so a WS_CHILD one is not what it is looking for.
        if ((GetWindowLong(hwnd, GwlStyle) & WsChild) != 0)
        {
            return;
        }

        string desktopName;
        try
        {
            desktopName = TestDesktop.NameForThread(threadId);
        }
        catch (InvalidOperationException)
        {
            // The window's own thread has already ended (a torn-down test), or its desktop handle is no
            // longer valid: nothing more can be learned about where it was shown, so this event is skipped
            // rather than guessed at.
            return;
        }

        if (desktopName.StartsWith(Phase5.CardDesktop.PrivateDesktopNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var violation = new TopLevelWindowViolation(
            eventType == EventSystemForeground ? "EVENT_SYSTEM_FOREGROUND" : "EVENT_OBJECT_SHOW",
            ClassNameOf(hwnd),
            TitleOf(hwnd),
            desktopName,
            DateTime.UtcNow);
        lock (Gate)
        {
            ViolationsList.Add(violation);
        }
    }

    private static string ClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(unknown)";
    }

    private static string TitleOf(nint hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        int written = GetWindowText(hwnd, buffer, buffer.Length);
        return written > 0 ? new string(buffer, 0, written) : "";
    }

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
}
