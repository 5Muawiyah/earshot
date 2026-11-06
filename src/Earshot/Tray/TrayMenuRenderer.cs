using System.Runtime.InteropServices;
using Earshot.Icons;
using Earshot.Interop;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tray;

// Draws the icon of each tray menu command in the menu's image column. The strip reserves the box with one shared transparent
// image of the icon size on each command; this renderer paints the icon over it with IconPainter, a pixel to a pixel, in the ink
// the item's text has (Ink). Items that name no icon, and the submenus' entries, are drawn by the base renderer as before.
internal sealed class TrayMenuRenderer(Func<ToolStripItem, (Icon Icon, IconStyle Style)?> iconOf) : ToolStripProfessionalRenderer
{
    private readonly Func<ToolStripItem, (Icon Icon, IconStyle Style)?> _iconOf = iconOf ?? throw new ArgumentNullException(nameof(iconOf));

    // The icon's colour: the text colour of an enabled item, the grey text colour of a disabled one, and in a high contrast theme the
    // highlight text colour on the selected item (the menu paints the text that way, so the icon follows it).
    internal static Color Ink(bool enabled, bool selected, bool highContrast, Color foreColor)
    {
        if (!enabled)
        {
            return SystemColors.GrayText;
        }

        return selected && highContrast ? SystemColors.HighlightText : foreColor;
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_iconOf(e.Item) is not { } mark)
        {
            base.OnRenderItemImage(e);
            return;
        }

        // The reserved image is the box: the icon is drawn as large as the box the layout gave it.
        int box = Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height);
        if (box <= 0)
        {
            return;
        }

        Color ink = Ink(e.Item.Enabled, e.Item.Selected, SystemInformation.HighContrast, e.Item.ForeColor);
        IconPainter.Draw(e.Graphics, mark.Icon, mark.Style, e.ImageRectangle, box, ink, IconRule.B);
    }
}

// The size and the dpi of the icons in the tray menu, from documented calls only.
internal static partial class TrayMenuScale
{
    private const string User32 = "user32.dll";

    // The monitor holding a point, or with MONITOR_DEFAULTTONEAREST the nearest.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfrompoint
    [LibraryImport(User32)]
    private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    // The dpi of a window; 0 for an invalid handle.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow
    [LibraryImport(User32)]
    private static partial uint GetDpiForWindow(nint hwnd);

    // The icon box of a menu at dpi: the small icon metric the tray uses, SM_CXSMICON at that dpi (16, 20, 24, 32 at 96, 120, 144,
    // 192). The metric is 0 only for a dpi Windows rejects; then the 16 px small icon scaled by dpi / 96 stands in.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi
    internal static int IconPx(uint dpi)
    {
        int metric = Shell.GetSystemMetricsForDpi(Shell.SM_CXSMICON, dpi);
        return metric > 0 ? metric : Math.Max(16, (int)Math.Round(16.0 * dpi / 96.0, MidpointRounding.AwayFromZero));
    }

    // The dpi of the monitor the cursor is on: where the menu opens. The system dpi when Windows will not say.
    // https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor
    internal static uint DpiAtCursor()
    {
        Point at = Control.MousePosition;
        nint monitor = MonitorFromPoint(new POINT { x = at.X, y = at.Y }, Shell.MONITOR_DEFAULTTONEAREST);
        if (monitor != 0 && Shell.GetDpiForMonitor(monitor, Shell.MDT_EFFECTIVE_DPI, out uint dpiX, out _) >= 0 && dpiX != 0)
        {
            return dpiX;
        }

        return Shell.GetDpiForSystem();
    }

    // The dpi the menu's own window is at. The system dpi for a window that has no handle yet.
    internal static uint DpiOfWindow(nint hwnd)
    {
        uint dpi = hwnd == 0 ? 0 : GetDpiForWindow(hwnd);
        return dpi != 0 ? dpi : Shell.GetDpiForSystem();
    }
}
