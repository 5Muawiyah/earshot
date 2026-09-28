using Earshot.Popup;

namespace Earshot.Widget;

// Pure placement maths for the widget card: where it goes above a given rectangle (the gauge's bounds, or
// a zero-size rectangle at a fallback point when the gauge is hidden), clamped into a work area. No window,
// no native call, so it is unit-testable without a window.
//
// The card is meant to land above the gauge the way a click-anchored popup lands above its anchor.
// CardPlacement.TargetFor(NearCursor) does the equivalent job for the tray's other cards, with the cursor as
// the anchor, but doing the same here would need CardTarget's taskbar-band exclusion rectangle to be the
// gauge's own rectangle, which CardPlacement does not expose a seam for today; wiring one in would be a
// larger change to CardPlacement's own contract than this card needs. This gives the same visible result
// (above the anchor, clamped to the work area) with its own small, directly testable function, reusing
// CardPlacement.Scale and CardPlacement.Clamp rather than duplicating them.
internal static class WidgetCardPlacement
{
    // Gap between the card and the rectangle it is placed above, in pixels at 96 DPI. A layout choice, not
    // a measurement, matching CardPlacement.MarginAt96.
    public const int GapAt96 = 12;

    // The card's rectangle: centred over anchor's horizontal middle, its bottom edge Gap above anchor's
    // top, clamped inside workArea without resizing. anchor may be a zero-size rectangle at a point (the
    // fallback when the gauge is hidden): centring and gap above still apply.
    public static Rectangle Above(Rectangle anchor, Size cardSize, Rectangle workArea, int dpi)
    {
        int gap = CardPlacement.Scale(GapAt96, dpi);
        int x = anchor.X + (anchor.Width / 2) - (cardSize.Width / 2);
        int y = anchor.Y - gap - cardSize.Height;
        var placed = new Rectangle(x, y, cardSize.Width, cardSize.Height);
        return CardPlacement.Clamp(placed, workArea);
    }
}
