using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Shell, monitor and DPI calls for the tray icon size and the popup card placement.
internal static unsafe partial class Shell
{
    private const string Shell32 = "shell32.dll";
    private const string User32 = "user32.dll";
    private const string Shcore = "shcore.dll";

    // SHAppBarMessage messages (shellapi.h). ABM_GETTASKBARPOS needs only cbSize and fills rc in screen
    // coordinates. uEdge is not documented as an output of it: derive the edge from rc.
    // https://learn.microsoft.com/en-us/windows/win32/shell/abm-gettaskbarpos
    internal const uint ABM_GETSTATE = 0x00000004;
    internal const uint ABM_GETTASKBARPOS = 0x00000005;

    // ABM_GETSTATE result bits.
    // https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate
    internal const uint ABS_AUTOHIDE = 0x0000001;
    internal const uint ABS_ALWAYSONTOP = 0x0000002;

    // APPBARDATA.uEdge values.
    internal const uint ABE_LEFT = 0;
    internal const uint ABE_TOP = 1;
    internal const uint ABE_RIGHT = 2;
    internal const uint ABE_BOTTOM = 3;

    // MonitorFromRect flags (WinUser.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect
    internal const uint MONITOR_DEFAULTTONULL = 0x00000000;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // MONITOR_DPI_TYPE (ShellScalingApi.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/ne-shellscalingapi-monitor_dpi_type
    internal const int MDT_EFFECTIVE_DPI = 0;
    internal const int MDT_ANGULAR_DPI = 1;
    internal const int MDT_RAW_DPI = 2;

    // GetSystemMetricsForDpi indexes. SM_CXSMICON is the documented tray icon size (LoadIconMetric LIM_SMALL).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics
    internal const int SM_CXICON = 11;
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;

    // CalculatePopupWindowPosition flags (WinUser.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-calculatepopupwindowposition
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_CENTERALIGN = 0x0004;
    internal const uint TPM_RIGHTALIGN = 0x0008;
    internal const uint TPM_TOPALIGN = 0x0000;
    internal const uint TPM_VCENTERALIGN = 0x0010;
    internal const uint TPM_BOTTOMALIGN = 0x0020;
    internal const uint TPM_HORIZONTAL = 0x0000;
    internal const uint TPM_VERTICAL = 0x0040;
    internal const uint TPM_WORKAREA = 0x10000;

    // QUERY_USER_NOTIFICATION_STATE (shellapi.h). A card not triggered by a click shows only for
    // QUNS_ACCEPTS_NOTIFICATIONS or QUNS_APP.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
    internal const int QUNS_NOT_PRESENT = 1;
    internal const int QUNS_BUSY = 2;
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    internal const int QUNS_QUIET_TIME = 6;
    internal const int QUNS_APP = 7;

    // The registered message the shell broadcasts to top-level windows when the taskbar is created (and,
    // on Windows 10 and later, when the primary display DPI changes).
    // https://learn.microsoft.com/en-us/windows/win32/shell/taskbar
    internal const string TaskbarCreatedMessageName = "TaskbarCreated";

    // Returns TRUE (non-zero) on success for ABM_GETTASKBARPOS. Set cbSize from Marshal.SizeOf.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage
    [LibraryImport(Shell32)]
    internal static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [LibraryImport(User32)]
    internal static partial nint MonitorFromRect(in RECT lprc, uint dwFlags);

    // Per-display DPI for a per-monitor-aware caller. Returns an HRESULT.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor
    [LibraryImport(Shcore)]
    internal static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // DPI-aware GetSystemMetrics. Never use GetSystemMetrics on a per-monitor-aware thread.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi
    [LibraryImport(User32)]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    // Fallback when the taskbar monitor cannot be found. Do not cache the value.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforsystem
    [LibraryImport(User32)]
    internal static partial uint GetDpiForSystem();

    // Places a popup of windowSize next to anchorPoint, avoiding excludeRect, within the anchor's monitor.
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CalculatePopupWindowPosition(in POINT anchorPoint, in SIZE windowSize, uint flags, in RECT excludeRect, out RECT popupWindowPosition);

    // The same call without an exclude rectangle (excludeRect is optional).
    [LibraryImport(User32, EntryPoint = "CalculatePopupWindowPosition", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CalculatePopupWindowPositionNoExclude(in POINT anchorPoint, in SIZE windowSize, uint flags, RECT* excludeRect, out RECT popupWindowPosition);

    // Returns an HRESULT; pquns receives a QUNS_* value.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
    [LibraryImport(Shell32)]
    internal static partial int SHQueryUserNotificationState(out int pquns);

    // Returns 0 on failure (see last error). Use TaskbarCreatedMessageName.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerwindowmessagew
    [LibraryImport(User32, EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string lpString);
}

// APPBARDATA, 48 bytes on x64.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-appbardata
[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public uint cbSize;
    public nint hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public nint lParam;
}

// https://learn.microsoft.com/en-us/windows/win32/api/windef/ns-windef-rect
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

// https://learn.microsoft.com/en-us/windows/win32/api/windef/ns-windef-point
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

// https://learn.microsoft.com/en-us/windows/win32/api/windef/ns-windef-size
[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}
