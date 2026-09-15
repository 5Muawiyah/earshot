using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Popup;

// The side of a display the taskbar is on.
internal enum TaskbarEdge { Bottom, Top, Left, Right }

// One display in physical pixels, as Screen reports it in a per-monitor DPI aware process.
internal readonly record struct DisplayArea(Rectangle Bounds, Rectangle WorkArea, bool IsPrimary);

// What placement reads from the desktop, once per card, all in physical pixels.
//   Cursor           the cursor position when the card was asked for
//   Displays         every display
//   Taskbar          the SHAppBarMessage(ABM_GETTASKBARPOS) rectangle, or null when the call failed
//   TaskbarAutoHide  ABM_GETSTATE reported ABS_AUTOHIDE
internal sealed record PlacementScene(Point Cursor, IReadOnlyList<DisplayArea> Displays, Rectangle? Taskbar, bool TaskbarAutoHide);

// Where a card goes: the display, the taskbar edge on it, the strip the taskbar covers on it (empty when
// there is none) and the area the card stays inside (the work area less that strip).
internal readonly record struct CardTarget(DisplayArea Display, TaskbarEdge Edge, Rectangle TaskbarBand, Rectangle Area);

// Pure placement maths for the popup card. No window, no native call.
//
// Display. A card after a click (NearCursor) goes on the display under the cursor. A card nobody clicked
// for (NearTray) goes on the display the taskbar with the notification area is on, or the primary
// display when the taskbar rectangle could not be read.
//
// Edge. Derived from geometry, never from APPBARDATA.uEdge, which ABM_GETTASKBARPOS does not document as
// an output: a taskbar wider than it is tall is at the top or bottom, whichever half of the display its
// centre is in; otherwise at the left or right. This also holds for an auto-hidden taskbar whose rectangle
// lies mostly off the display. On a display without that taskbar the edge is the side where the work area
// is inset (a taskbar on a secondary display), and failing that the main taskbar's edge.
// https://learn.microsoft.com/en-us/windows/win32/shell/abm-gettaskbarpos
//
// Taskbar band. A strip along the edge as thick as the taskbar. It is taken out of the work area, which
// matters when the taskbar auto-hides: the work area then covers the whole display and the taskbar slides
// over it when it appears.
// https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate
//
// NearCursor. The popup is aligned to the cursor and moved off the taskbar band the way
// CalculatePopupWindowPosition does for these flags: centred on the cursor and above a bottom taskbar
// (below a top one), vertically centred and beside a left or right taskbar, kept inside the work area.
// The notification area guidance asks for a popup raised by a click to sit near the click.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-calculatepopupwindowposition
// https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
//
// NearTray. The corner of the area nearest the notification area: bottom right for a bottom or right
// taskbar, top right for a top taskbar, bottom left for a left taskbar. This assumes the left-to-right
// shell layout.
//
// Every result is clamped into the area, less a margin scaled for the display's DPI.
internal static class CardPlacement
{
    public const int BaseDpi = 96;

    // Gap between the card and the taskbar or the edge of the work area, in pixels at 96 DPI. A spacing
    // choice for the layout, scaled for the display.
    public const int MarginAt96 = 12;

    // A length at 96 DPI scaled to dpi, rounded half away from zero. A dpi of 0 or less counts as 96.
    public static int Scale(int valueAt96, int dpi)
    {
        int effective = dpi > 0 ? dpi : BaseDpi;
        return (int)Math.Round(valueAt96 * (double)effective / BaseDpi, MidpointRounding.AwayFromZero);
    }

    // The display, edge, taskbar band and usable area for a card.
    public static CardTarget TargetFor(CardAnchor anchor, PlacementScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        IReadOnlyList<DisplayArea> displays = scene.Displays;
        if (displays.Count == 0)
        {
            throw new ArgumentException("A card needs at least one display.", nameof(scene));
        }

        int taskbarDisplay = scene.Taskbar is { } bar ? DisplayFor(bar, displays) : -1;
        int index = anchor == CardAnchor.NearCursor
            ? DisplayFor(scene.Cursor, displays)
            : taskbarDisplay >= 0 ? taskbarDisplay : PrimaryIndex(displays);
        DisplayArea display = displays[index];

        TaskbarEdge edge;
        Rectangle band;
        if (scene.Taskbar is { } taskbar && taskbarDisplay == index)
        {
            edge = EdgeOf(taskbar, display.Bounds);
            band = Strip(display.Bounds, edge, Thickness(taskbar, edge));
        }
        else if (LargestInset(display) is { } inset)
        {
            edge = inset.Edge;
            band = Strip(display.Bounds, edge, inset.Size);
        }
        else
        {
            // No taskbar reserves space on this display. Taskbars on every display share the main
            // taskbar's edge and auto-hide setting.
            edge = scene.Taskbar is { } main && taskbarDisplay >= 0
                ? EdgeOf(main, displays[taskbarDisplay].Bounds)
                : TaskbarEdge.Bottom;
            band = scene.TaskbarAutoHide && scene.Taskbar is { } hidden
                ? Strip(display.Bounds, edge, Thickness(hidden, edge))
                : Rectangle.Empty;
        }

        return new CardTarget(display, edge, band, UsableArea(display.WorkArea, edge, band));
    }

    // The card rectangle for a card of the given size on target. dpi is the target display's DPI.
    public static Rectangle Place(CardAnchor anchor, PlacementScene scene, CardTarget target, Size card, int dpi)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Rectangle area = Deflate(target.Area, Scale(MarginAt96, dpi));
        Rectangle placed = anchor == CardAnchor.NearCursor
            ? CalculatePopup(scene.Cursor, card, CursorFlags(target.Edge), target.TaskbarBand, area)
            : NearTray(card, target.Edge, area);
        return Clamp(placed, area);
    }

    // The widest a card may be on target: the usable area less the margin on both sides.
    public static int AvailableWidth(CardTarget target, int dpi) =>
        Math.Max(1, Deflate(target.Area, Scale(MarginAt96, dpi)).Width);

    // Index of the display that overlaps rect the most, or the nearest display when none overlaps.
    public static int DisplayFor(Rectangle rect, IReadOnlyList<DisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < displays.Count; i++)
        {
            Rectangle overlap = Rectangle.Intersect(rect, displays[i].Bounds);
            long area = (long)overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }

        return best >= 0 ? best : Nearest(new Point(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2)), displays);
    }

    // Index of the display that contains point, or the nearest display.
    public static int DisplayFor(Point point, IReadOnlyList<DisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].Bounds.Contains(point))
            {
                return i;
            }
        }

        return Nearest(point, displays);
    }

    // The taskbar edge from the taskbar rectangle and the bounds of its display.
    public static TaskbarEdge EdgeOf(Rectangle taskbar, Rectangle displayBounds)
    {
        // Twice the centres, so no halves are lost.
        if (taskbar.Width >= taskbar.Height)
        {
            long barY = (long)taskbar.Top + taskbar.Bottom;
            long displayY = (long)displayBounds.Top + displayBounds.Bottom;
            return barY < displayY ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        long barX = (long)taskbar.Left + taskbar.Right;
        long displayX = (long)displayBounds.Left + displayBounds.Right;
        return barX < displayX ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    // The work area less the part the taskbar band covers.
    public static Rectangle UsableArea(Rectangle workArea, TaskbarEdge edge, Rectangle band)
    {
        if (band.IsEmpty)
        {
            return workArea;
        }

        int left = workArea.Left;
        int top = workArea.Top;
        int right = workArea.Right;
        int bottom = workArea.Bottom;
        switch (edge)
        {
            case TaskbarEdge.Bottom:
                bottom = Math.Min(bottom, band.Top);
                break;
            case TaskbarEdge.Top:
                top = Math.Max(top, band.Bottom);
                break;
            case TaskbarEdge.Left:
                left = Math.Max(left, band.Right);
                break;
            case TaskbarEdge.Right:
                right = Math.Min(right, band.Left);
                break;
        }

        return right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : workArea;
    }

    // CalculatePopupWindowPosition alignment flags for a card raised by a click, by taskbar edge.
    public static uint CursorFlags(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => Shell.TPM_CENTERALIGN | Shell.TPM_TOPALIGN | Shell.TPM_VERTICAL,
        TaskbarEdge.Left => Shell.TPM_LEFTALIGN | Shell.TPM_VCENTERALIGN | Shell.TPM_HORIZONTAL,
        TaskbarEdge.Right => Shell.TPM_RIGHTALIGN | Shell.TPM_VCENTERALIGN | Shell.TPM_HORIZONTAL,
        _ => Shell.TPM_CENTERALIGN | Shell.TPM_BOTTOMALIGN | Shell.TPM_VERTICAL,
    };

    // CalculatePopupWindowPosition for the flags above, restricted to area as TPM_WORKAREA restricts it:
    // align the popup to anchor; if it overlaps exclude, move it off along the axis TPM_VERTICAL or
    // TPM_HORIZONTAL names, to the side the alignment points at when that side has room; then keep it
    // inside area.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-calculatepopupwindowposition
    public static Rectangle CalculatePopup(Point anchor, Size size, uint flags, Rectangle exclude, Rectangle area)
    {
        int x = (flags & Shell.TPM_RIGHTALIGN) != 0 ? anchor.X - size.Width
            : (flags & Shell.TPM_CENTERALIGN) != 0 ? anchor.X - (size.Width / 2)
            : anchor.X;
        int y = (flags & Shell.TPM_BOTTOMALIGN) != 0 ? anchor.Y - size.Height
            : (flags & Shell.TPM_VCENTERALIGN) != 0 ? anchor.Y - (size.Height / 2)
            : anchor.Y;
        var popup = new Rectangle(new Point(x, y), size);

        if (!exclude.IsEmpty && popup.IntersectsWith(exclude))
        {
            if ((flags & Shell.TPM_VERTICAL) != 0)
            {
                int above = exclude.Top - size.Height;
                int below = exclude.Bottom;
                bool preferAbove = (flags & Shell.TPM_BOTTOMALIGN) != 0 ||
                    ((flags & Shell.TPM_VCENTERALIGN) != 0 && exclude.Top - area.Top >= area.Bottom - exclude.Bottom);
                bool aboveFits = above >= area.Top;
                bool belowFits = below + size.Height <= area.Bottom;
                popup.Y = preferAbove
                    ? (aboveFits || !belowFits ? above : below)
                    : (belowFits || !aboveFits ? below : above);
            }
            else
            {
                int before = exclude.Left - size.Width;
                int after = exclude.Right;
                bool preferBefore = (flags & Shell.TPM_RIGHTALIGN) != 0 ||
                    ((flags & Shell.TPM_CENTERALIGN) != 0 && exclude.Left - area.Left >= area.Right - exclude.Right);
                bool beforeFits = before >= area.Left;
                bool afterFits = after + size.Width <= area.Right;
                popup.X = preferBefore
                    ? (beforeFits || !afterFits ? before : after)
                    : (afterFits || !beforeFits ? after : before);
            }
        }

        return Clamp(popup, area);
    }

    // The corner of area nearest the notification area for a taskbar on edge.
    public static Rectangle NearTray(Size card, TaskbarEdge edge, Rectangle area)
    {
        int right = area.Right - card.Width;
        int bottom = area.Bottom - card.Height;
        Point corner = edge switch
        {
            TaskbarEdge.Top => new Point(right, area.Top),
            TaskbarEdge.Left => new Point(area.Left, bottom),
            _ => new Point(right, bottom),
        };
        return Clamp(new Rectangle(corner, card), area);
    }

    // Moves rect inside area without resizing it. A rect larger than area is aligned to area's left or top.
    public static Rectangle Clamp(Rectangle rect, Rectangle area)
    {
        int x = rect.Width >= area.Width ? area.Left : Math.Clamp(rect.X, area.Left, area.Right - rect.Width);
        int y = rect.Height >= area.Height ? area.Top : Math.Clamp(rect.Y, area.Top, area.Bottom - rect.Height);
        return new Rectangle(x, y, rect.Width, rect.Height);
    }

    // area shrunk by margin on every side, except along an axis too short to lose it.
    public static Rectangle Deflate(Rectangle area, int margin)
    {
        int m = Math.Max(0, margin);
        int dx = area.Width > 2 * m ? m : 0;
        int dy = area.Height > 2 * m ? m : 0;
        return Rectangle.FromLTRB(area.Left + dx, area.Top + dy, area.Right - dx, area.Bottom - dy);
    }

    private static int Thickness(Rectangle taskbar, TaskbarEdge edge) =>
        Math.Max(0, edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? taskbar.Height : taskbar.Width);

    // A strip along edge of bounds, thickness pixels deep, or empty for no thickness.
    private static Rectangle Strip(Rectangle bounds, TaskbarEdge edge, int thickness)
    {
        if (thickness <= 0)
        {
            return Rectangle.Empty;
        }

        return edge switch
        {
            TaskbarEdge.Top => new Rectangle(bounds.Left, bounds.Top, bounds.Width, Math.Min(thickness, bounds.Height)),
            TaskbarEdge.Left => new Rectangle(bounds.Left, bounds.Top, Math.Min(thickness, bounds.Width), bounds.Height),
            TaskbarEdge.Right => Rectangle.FromLTRB(Math.Max(bounds.Left, bounds.Right - thickness), bounds.Top, bounds.Right, bounds.Bottom),
            _ => Rectangle.FromLTRB(bounds.Left, Math.Max(bounds.Top, bounds.Bottom - thickness), bounds.Right, bounds.Bottom),
        };
    }

    // The side where the work area is inset the most from the display bounds, or null when it is not inset.
    private static (TaskbarEdge Edge, int Size)? LargestInset(DisplayArea display)
    {
        Rectangle b = display.Bounds;
        Rectangle w = display.WorkArea;
        (TaskbarEdge Edge, int Size)[] insets =
        [
            (TaskbarEdge.Bottom, b.Bottom - w.Bottom),
            (TaskbarEdge.Top, w.Top - b.Top),
            (TaskbarEdge.Left, w.Left - b.Left),
            (TaskbarEdge.Right, b.Right - w.Right),
        ];

        (TaskbarEdge Edge, int Size)? best = null;
        foreach ((TaskbarEdge Edge, int Size) inset in insets)
        {
            if (inset.Size > 0 && (best is null || inset.Size > best.Value.Size))
            {
                best = inset;
            }
        }

        return best;
    }

    private static int PrimaryIndex(IReadOnlyList<DisplayArea> displays)
    {
        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].IsPrimary)
            {
                return i;
            }
        }

        return 0;
    }

    private static int Nearest(Point point, IReadOnlyList<DisplayArea> displays)
    {
        int best = 0;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < displays.Count; i++)
        {
            Rectangle b = displays[i].Bounds;
            long dx = Math.Max(0, Math.Max(b.Left - point.X, point.X - (b.Right - 1)));
            long dy = Math.Max(0, Math.Max(b.Top - point.Y, point.Y - (b.Bottom - 1)));
            long distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
