using Earshot.Popup;

namespace Earshot.Widget;

// Pure placement maths for the widget card: where it goes above a given rectangle (the gauge's bounds, or
// a zero-size rectangle at a fallback point when the gauge is hidden), clamped into a work area. No window,
// no native call, so it is unit-testable without a window.
//
// The card follows the gauge:
//   gauge at the right-hand end   the card's right edge is 12 px from the screen edge
//   gauge next to the apps        the card is centred on the gauge
// and either way it is kept 12 px inside the screen. Its bottom edge is 12 px above the taskbar, which on a
// bottom taskbar is the bottom of the work area; a gauge on an auto-hidden taskbar has no work area edge to
// use, so the card sits 12 px above the gauge instead.
//
// CardPlacement.TargetFor(NearCursor) does the equivalent job for the tray's other cards, with the cursor as
// the anchor, but doing the same here would need CardTarget's taskbar-band exclusion rectangle to be the
// gauge's own rectangle, which CardPlacement does not expose a seam for today; wiring one in would be a
// larger change to CardPlacement's own contract than this card needs. This gives the same visible result
// (above the anchor, clamped to the work area) with its own small, directly testable function, reusing
// CardPlacement.Scale and CardPlacement.Clamp rather than duplicating them.
internal static class WidgetCardPlacement
{
    // Gap between the card and the rectangle it is placed above, and between the card and the screen's
    // edge, in pixels at 96 DPI. A layout choice, not a measurement, matching CardPlacement.MarginAt96.
    public const int GapAt96 = 12;

    // The work area of the display the anchor is on: the gauge's own display, so a card for a gauge on a secondary
    // display opens there and is kept inside that display's work area. The display holding most of a gauge-sized
    // anchor, or the one holding or nearest a point when the anchor is a point. fallback when there are no displays.
    public static Rectangle WorkAreaFor(Rectangle anchor, IReadOnlyList<DisplayArea> displays, Rectangle fallback)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return fallback;
        }

        int index = anchor.Width > 0 && anchor.Height > 0
            ? CardPlacement.DisplayFor(anchor, displays)
            : CardPlacement.DisplayFor(anchor.Location, displays);
        return displays[index].WorkArea;
    }

    // The tallest the card can be and still sit above anchor with the gap under it and the gap under the top of workArea
    // above it: the space above the taskbar (the work area's bottom, or the anchor's top when that is higher), less the
    // margins. A page taller than this is capped to it and scrolls. At least 1.
    public static int MaxHeight(Rectangle anchor, Rectangle workArea, int dpi)
    {
        int gap = CardPlacement.Scale(GapAt96, dpi);
        int bottom = Math.Min(anchor.Top, workArea.Bottom) - gap;
        return Math.Max(1, bottom - (workArea.Top + gap));
    }

    // The card's rectangle. anchor may be a zero-size rectangle at a point (the fallback when the gauge is
    // hidden): the card is then centred on that point with its bottom edge the gap above it, clamped inside
    // workArea without resizing.
    public static Rectangle Above(Rectangle anchor, Size cardSize, Rectangle workArea, int dpi, GaugePosition position = GaugePosition.RightEnd)
    {
        int gap = CardPlacement.Scale(GapAt96, dpi);
        bool isGauge = anchor.Width > 0 && anchor.Height > 0;

        if (!isGauge)
        {
            int fallbackX = anchor.X - (cardSize.Width / 2);
            int fallbackY = anchor.Y - gap - cardSize.Height;
            return CardPlacement.Clamp(new Rectangle(fallbackX, fallbackY, cardSize.Width, cardSize.Height), workArea);
        }

        int bottom = Math.Min(anchor.Top, workArea.Bottom);
        int y = bottom - gap - cardSize.Height;
        int x = position == GaugePosition.RightEnd
            ? workArea.Right - gap - cardSize.Width
            : anchor.X + (anchor.Width / 2) - (cardSize.Width / 2);

        Rectangle inner = Rectangle.FromLTRB(workArea.Left + gap, workArea.Top, workArea.Right - gap, workArea.Bottom);
        return CardPlacement.Clamp(new Rectangle(x, y, cardSize.Width, cardSize.Height), inner);
    }
}
