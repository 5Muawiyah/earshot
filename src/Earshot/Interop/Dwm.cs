using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Desktop Window Manager attributes for the popup card. WinForms Form.FormCornerPreference already calls
// DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE); this declaration is for anything it does not cover.
internal static partial class Dwm
{
    private const string Dll = "dwmapi.dll";

    // DWMWINDOWATTRIBUTE values (dwmapi.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    internal const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // "Enables or disables the use of the alpha channel in the window's redirection bitmap ... supported
    // starting with Windows 11 Build 26100." A local probe found this refused with E_INVALIDARG on build
    // 26200.9457; recorded here, not used.
    internal const uint DWMWA_REDIRECTIONBITMAP_ALPHA = 26;
    internal const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const uint DWMWA_BORDER_COLOR = 34;
    internal const uint DWMWA_CAPTION_COLOR = 35;
    internal const uint DWMWA_TEXT_COLOR = 36;
    internal const uint DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;
    internal const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_WINDOW_CORNER_PREFERENCE. A hint only, Windows 11 build 22000 and later; windows with per-pixel
    // alpha layering or a window region are never rounded.
    // https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners
    internal const int DWMWCP_DEFAULT = 0;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMWCP_ROUNDSMALL = 3;

    // COLORREF values for DWMWA_BORDER_COLOR.
    internal const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // DWM_SYSTEMBACKDROP_TYPE (Windows 11 build 22621 and later).
    internal const int DWMSBT_AUTO = 0;
    internal const int DWMSBT_NONE = 1;
    internal const int DWMSBT_MAINWINDOW = 2;
    internal const int DWMSBT_TRANSIENTWINDOW = 3;
    internal const int DWMSBT_TABBEDWINDOW = 4;

    // Every attribute Earshot sets is a 4-byte value (BOOL, enum or COLORREF), so pvAttribute is an int
    // and cbAttribute is 4. A COLORREF is passed as unchecked((int)value). Returns an HRESULT.
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmsetwindowattribute
    [LibraryImport(Dll)]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, in int pvAttribute, uint cbAttribute);

    // Extends the window's frame (here, the system backdrop material) into the client area. -1 on every
    // margin extends across the whole client, which a local probe found makes the backdrop material fill
    // the window when the client itself is painted with alpha 0. Returns an HRESULT.
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea
    [LibraryImport(Dll)]
    internal static partial int DwmExtendFrameIntoClientArea(nint hwnd, in MARGINS pMarInset);
}

// https://learn.microsoft.com/en-us/windows/win32/api/uxtheme/ns-uxtheme-margins
[StructLayout(LayoutKind.Sequential)]
internal struct MARGINS
{
    public int cxLeftWidth;
    public int cxRightWidth;
    public int cyTopHeight;
    public int cyBottomHeight;

    // Every margin -1: the recipe that extends the backdrop across the whole client area.
    public static readonly MARGINS Full = new() { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
}
