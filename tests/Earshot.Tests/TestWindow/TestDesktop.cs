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

    public static string CurrentName()
    {
        nint desktop = GetThreadDesktop(GetCurrentThreadId());
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

    public static bool IsOwnersDesktop() => string.Equals(CurrentName(), OwnersDesktopName, StringComparison.OrdinalIgnoreCase);

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
