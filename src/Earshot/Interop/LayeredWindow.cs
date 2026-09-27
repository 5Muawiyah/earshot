using System.Runtime.InteropServices;

namespace Earshot.Interop;

// UpdateLayeredWindow and the mouse-tracking call the gauge window needs. A layered window's hit testing
// follows the shape and transparency of what was actually painted: an alpha-0 pixel lets a click through
// to whatever is beneath it, unless WS_EX_TRANSPARENT is also set, which the gauge never sets.
// https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
internal static partial class LayeredWindow
{
    private const string User32 = "user32.dll";

    // UpdateLayeredWindow flags.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow
    internal const uint ULW_ALPHA = 0x00000002;

    // BLENDFUNCTION constants.
    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;

    // TrackMouseEvent flags: TME_LEAVE asks for one WM_MOUSELEAVE when the cursor leaves the window.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackmouseevent
    internal const uint TME_LEAVE = 0x00000002;

    // pptSrc, pptDst and prcDirty are all optional; the gauge always draws the whole bitmap at (0,0) into
    // itself, so pptSrc is {0,0} and prcDirty is null.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, in POINT pptDst, in SIZE psize, nint hdcSrc, in POINT pptSrc,
        int crKey, in BLENDFUNCTION pblend, uint dwFlags);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint SelectObject(nint hdc, nint hgdiobj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint hObject);

    [LibraryImport(User32, SetLastError = true)]
    internal static partial nint GetDC(nint hwnd);

    [LibraryImport(User32)]
    internal static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    // A top-down, 32 bpp DIB section with its own pixel memory (ppvBits), the shape UpdateLayeredWindow
    // needs: a DDB from Bitmap.GetHbitmap carries no alpha channel. DIB_RGB_COLORS = 0.
    // https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibsection
    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreateDIBSection(nint hdc, in BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
}

// https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapinfoheader
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

// https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapinfo
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
}

// https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-blendfunction
[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-trackmouseevent
[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}
