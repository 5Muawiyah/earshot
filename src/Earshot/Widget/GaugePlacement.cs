using Earshot.Popup;

namespace Earshot.Widget;

// Pure placement maths for the taskbar gauge. No window, no native call.
//
// Along the long axis (x for a Bottom or Top taskbar, y for Left or Right):
//   1. Project every occupant onto the axis, sort, merge overlaps.
//   2. The task list end L is the end of the merged interval that contains the Start button's
//      rectangle (Start plus the task buttons run together on the machine this was measured on); with
//      no Start button, L is the end of the first merged interval; with no occupants at all, L is the
//      taskbar's own start plus the edge margin.
//   3. The candidate free run is the one that begins exactly at L. No other run is tried, so the gauge
//      never appears anywhere but beside the app buttons.
//   4. It qualifies when its length is at least Clearance + gaugeLong + Clearance; otherwise the result
//      is null (hidden).
//   5. The rect starts at L + Clearance, is gaugeLong along the long axis, and spans the taskbar's own
//      short axis in full.
//   6. The result is clamped inside the taskbar rectangle; one that would leave it is null.
//
// Auto-hide: when the taskbar has slid away (the part of its rectangle that overlaps its monitor is
// thinner than its own thickness) the result is null; when it has slid in, the maths runs on the
// on-screen rectangle.
internal static class GaugePlacement
{
    // Design choices at 96 DPI, scaled by CardPlacement.Scale for the target DPI.
    public const int ClearanceAt96 = 24;
    public const int EdgeMarginAt96 = 8;

    // The gauge's own rectangle for a layout, or null when there is no run wide enough, the taskbar has
    // auto-hidden away, or the placement would leave the taskbar. gaugeLong is the gauge's extent along
    // the long axis, already scaled for layout.Dpi (GaugeRenderer.SizeFor(dpi)).
    public static Rectangle? Place(TaskbarLayout layout, int gaugeLong)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (gaugeLong <= 0)
        {
            return null;
        }

        bool horizontal = layout.Edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
        Rectangle t = layout.Taskbar;
        int thickness = horizontal ? t.Height : t.Width;

        if (layout.AutoHide)
        {
            Rectangle visible = Rectangle.Intersect(t, layout.MonitorBounds);
            int visibleThickness = horizontal ? visible.Height : visible.Width;
            if (visibleThickness < thickness)
            {
                // Slid away.
                return null;
            }

            t = visible;
        }

        int clearance = CardPlacement.Scale(ClearanceAt96, layout.Dpi);
        int edgeMargin = CardPlacement.Scale(EdgeMarginAt96, layout.Dpi);
        int axisStart = horizontal ? t.Left : t.Top;
        int axisEnd = horizontal ? t.Right : t.Bottom;

        List<(int Start, int End)> merged = MergedIntervals(layout.Occupied, t, horizontal);

        int l = FindL(merged, layout.StartButton, t, horizontal, axisStart + edgeMargin);

        (int Start, int End)? candidate = FreeRunAt(merged, l, axisStart + edgeMargin, axisEnd - edgeMargin);
        if (candidate is not { } run)
        {
            return null;
        }

        int required = clearance + gaugeLong + clearance;
        if (run.End - run.Start < required)
        {
            return null;
        }

        int start = l + clearance;
        Rectangle rect = horizontal
            ? new Rectangle(start, t.Top, gaugeLong, t.Height)
            : new Rectangle(t.Left, start, t.Width, gaugeLong);

        return t.Contains(rect) ? rect : null;
    }

    private static List<(int Start, int End)> MergedIntervals(IReadOnlyList<Rectangle> occupied, Rectangle t, bool horizontal)
    {
        var intervals = new List<(int Start, int End)>(occupied.Count);
        foreach (Rectangle r in occupied)
        {
            Rectangle clipped = Rectangle.Intersect(r, t);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                continue;
            }

            intervals.Add(horizontal ? (clipped.Left, clipped.Right) : (clipped.Top, clipped.Bottom));
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int End)>(intervals.Count);
        foreach ((int Start, int End) iv in intervals)
        {
            if (merged.Count > 0 && iv.Start <= merged[^1].End)
            {
                if (iv.End > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, iv.End);
                }
            }
            else
            {
                merged.Add(iv);
            }
        }

        return merged;
    }

    // The task list end L: the end of the merged interval holding the Start button, the end of the
    // first merged interval when Start was not found, or noOccupantsStart when there are no occupants.
    private static int FindL(List<(int Start, int End)> merged, Rectangle? startButton, Rectangle t, bool horizontal, int noOccupantsStart)
    {
        if (merged.Count == 0)
        {
            return noOccupantsStart;
        }

        if (startButton is { } start)
        {
            Rectangle clipped = Rectangle.Intersect(start, t);
            if (clipped.Width > 0 && clipped.Height > 0)
            {
                (int s, int e) = horizontal ? (clipped.Left, clipped.Right) : (clipped.Top, clipped.Bottom);
                foreach ((int Start, int End) m in merged)
                {
                    if (m.Start <= s && e <= m.End)
                    {
                        return m.End;
                    }
                }
            }
        }

        return merged[0].End;
    }

    // The free run whose start is exactly at l, or null when no such run exists (the candidate free run
    // is the only one ever tried).
    private static (int Start, int End)? FreeRunAt(List<(int Start, int End)> merged, int l, int lowerBound, int upperBound)
    {
        int cursor = lowerBound;
        foreach ((int Start, int End) m in merged)
        {
            if (m.Start > cursor)
            {
                if (cursor == l)
                {
                    return (cursor, m.Start);
                }
            }

            cursor = Math.Max(cursor, m.End);
        }

        if (cursor == l && upperBound > cursor)
        {
            return (cursor, upperBound);
        }

        return null;
    }
}
