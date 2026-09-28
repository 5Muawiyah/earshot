using System.Runtime.InteropServices;
using System.Text;

namespace Earshot.Tests.TestWindow;

// The name of the desktop the calling thread belongs to. The desktop the owner sees is named
// "Default" (the interactive desktop of window station WinSta0); a test that shows a form there
// draws on, and takes focus from, the owner's real desktop.
// https://learn.microsoft.com/en-us/windows/win32/winstation/desktops
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getthreaddesktop
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw
internal static class TestDesktop
{
    public const string OwnersDesktopName = "Default";

    private const int UoiName = 2;

    public static string CurrentName() => NameForThread(GetCurrentThreadId());

    // The name of the desktop that owns the thread threadId is: any thread this process created,
    // including one that created a top-level window, not only the caller. Used by
    // TopLevelWindowVisibilityGuard to tell which desktop a window a WinEvent hook just saw belongs to,
    // without needing the window's own thread to be the one asking.
    public static string NameForThread(uint threadId)
    {
        nint desktop = GetThreadDesktop(threadId);
        if (desktop == 0)
        {
            throw new InvalidOperationException("GetThreadDesktop failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        var buffer = new byte[512];
        if (!GetUserObjectInformationW(desktop, UoiName, buffer, buffer.Length, out int needed))
        {
            throw new InvalidOperationException("GetUserObjectInformationW failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
    }

    // An allow-list, not a deny-list: MainForm.ForceControlCreationForTests refuses to show a real form
    // unless this reads false, so it must read true for anything that is not a confirmed-safe desktop,
    // never only for the one name "Default" happens to be. A deny-list version of this (comparing only
    // against "Default") passed on Default, failed correctly under CardDesktop.Run's own private
    // desktops, and then quietly showed the real form for real on a third kind of desktop neither name
    // covers - an external test-isolation tool's own desktop, say - since it recognised that desktop as
    // "not Default" and let the form through. Fail closed instead: safe only on a desktop this suite
    // itself created and can vouch for (Phase5.CardDesktop.Run's own "EarshotCardTest-" prefix).
    public static bool IsOwnersDesktop() => !CurrentName().StartsWith(Earshot.Tests.Phase5.CardDesktop.PrivateDesktopNamePrefix, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);
}
