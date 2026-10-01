using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// How a page taller than the card can be is scrolled: the maths of the offset, the wheel, the keyboard and the thin
// indicator, and the painter of that indicator. No window and no state; the card keeps the offset.
//
// Content positions are the page's own (the header at the top of them); the card's body is the area under the fixed
// header and scrolls over them. An offset is how many pixels of the body have gone up past its top.
//
// The indicator is the one Windows 11 flyouts show: a thin bar along the right edge laid over the content, widening
// when the pointer is near it or it is being dragged. It takes no room from the page.
// https://learn.microsoft.com/en-us/windows/apps/design/controls/scroll-controls
internal static class CardScroll
{
    // One line of a wheel notch, which Windows scrolls by the number of lines in its mouse setting (three by default).
    public const int WheelLineAt96 = 16;

    public const int IndicatorWidthAt96 = 2;
    public const int IndicatorHotWidthAt96 = 6;
    public const int IndicatorInsetAt96 = 2;

    // How wide a strip along the right edge the pointer is counted as being on the indicator in.
    public const int IndicatorZoneAt96 = 14;
    public const int IndicatorMinLengthAt96 = 24;
    public const int IndicatorEndGapAt96 = 4;

    // Room kept round a row that is scrolled into view, so its focus visual is not cut by the edge.
    public const int FocusMarginAt96 = 4;

    // How much of a page a Page Up or Page Down leaves in view from the page before: one line.
    public const int PageOverlapAt96 = 16;

    public static int MaxOffset(int bodyContentHeight, int viewportHeight) => Math.Max(0, bodyContentHeight - viewportHeight);

    public static int Clamp(int offset, int bodyContentHeight, int viewportHeight) =>
        Math.Clamp(offset, 0, MaxOffset(bodyContentHeight, viewportHeight));

    // How far a wheel turn of delta (120 for one notch, up positive) moves the content, in pixels, down positive.
    // lines is Windows' lines per notch: more than 0 scrolls that many lines, -1 a page, 0 not at all.
    public static int WheelPixels(int delta, int lines, int dpi, int viewportHeight)
    {
        if (lines == 0 || delta == 0)
        {
            return 0;
        }

        if (lines < 0)
        {
            int page = Math.Max(1, viewportHeight - CardPlacement.Scale(PageOverlapAt96, dpi));
            return -(int)Math.Round(delta / 120.0 * page, MidpointRounding.AwayFromZero);
        }

        return -(int)Math.Round(delta / 120.0 * lines * CardPlacement.Scale(WheelLineAt96, dpi), MidpointRounding.AwayFromZero);
    }

    // The offset that brings target (a rectangle in content positions) wholly into the viewport, with margin round it,
    // moving as little as it can. A target taller than the viewport is shown from its top.
    public static int EnsureVisible(int offset, Rectangle target, int bodyTop, int viewportHeight, int bodyContentHeight, int margin)
    {
        int top = target.Top - margin - bodyTop;
        int bottom = target.Bottom + margin - bodyTop;
        int next = offset;
        if (top < next)
        {
            next = top;
        }
        else if (bottom > next + viewportHeight)
        {
            next = bottom - viewportHeight;
            if (top < next)
            {
                next = top;
            }
        }

        return Clamp(next, bodyContentHeight, viewportHeight);
    }

    // The indicator's bar, in client pixels, for a viewport (the body's visible area) holding bodyContentHeight of
    // content at offset; empty when everything fits.
    public static Rectangle Thumb(Rectangle viewport, int bodyContentHeight, int offset, int dpi, bool wide)
    {
        int max = MaxOffset(bodyContentHeight, viewport.Height);
        if (max <= 0 || viewport.Height <= 0)
        {
            return Rectangle.Empty;
        }

        int endGap = CardPlacement.Scale(IndicatorEndGapAt96, dpi);
        int track = Math.Max(1, viewport.Height - (2 * endGap));
        int length = Math.Clamp((int)Math.Round((double)track * viewport.Height / bodyContentHeight), Math.Min(CardPlacement.Scale(IndicatorMinLengthAt96, dpi), track), track);
        int travel = track - length;
        int y = viewport.Top + endGap + (int)Math.Round((double)travel * Math.Clamp(offset, 0, max) / max);
        int width = CardPlacement.Scale(wide ? IndicatorHotWidthAt96 : IndicatorWidthAt96, dpi);
        int x = viewport.Right - CardPlacement.Scale(IndicatorInsetAt96, dpi) - width;
        return new Rectangle(x, y, width, length);
    }

    // The strip along the right edge in which the pointer counts as on the indicator.
    public static Rectangle Zone(Rectangle viewport, int dpi)
    {
        int width = CardPlacement.Scale(IndicatorZoneAt96, dpi);
        return new Rectangle(viewport.Right - width, viewport.Top, width, viewport.Height);
    }

    // The offset that puts the bar's top at barTop (a client y), for dragging it.
    public static int OffsetForThumbTop(Rectangle viewport, int bodyContentHeight, int barTop, int barLength, int dpi)
    {
        int max = MaxOffset(bodyContentHeight, viewport.Height);
        int endGap = CardPlacement.Scale(IndicatorEndGapAt96, dpi);
        int travel = Math.Max(1, viewport.Height - (2 * endGap) - barLength);
        double fraction = (double)(barTop - (viewport.Top + endGap)) / travel;
        return Math.Clamp((int)Math.Round(fraction * max), 0, max);
    }

    // How far a Page Up or Page Down moves: a page less one line.
    public static int PagePixels(int viewportHeight, int dpi) => Math.Max(1, viewportHeight - CardPlacement.Scale(PageOverlapAt96, dpi));

    public static void DrawIndicator(Graphics g, Rectangle bar, Color colour, bool wide)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (bar.IsEmpty)
        {
            return;
        }

        using GraphicsPath path = CardPaint.RoundedRectangle(new RectangleF(bar.X, bar.Y, bar.Width, bar.Height), bar.Width / 2f);
        using var brush = new SolidBrush(Color.FromArgb(wide ? 0xDC : 0x9C, colour));
        g.FillPath(brush, path);
    }
}
