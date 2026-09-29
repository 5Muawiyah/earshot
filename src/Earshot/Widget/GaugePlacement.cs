using Earshot.Popup;

namespace Earshot.Widget;

// Why a placement found no rectangle. None when it found one.
internal enum PlacementFailure
{
    None,
    UnsupportedEdge,     // a left or right taskbar: the gauge is a fixed landscape shape
    AutoHiddenAway,      // the taskbar has slid off screen
    NoAnchor,            // nothing was read that the position is measured from
    NoRoom,              // something already sits where the gauge would go
    OutsideTaskbar,      // the rectangle would leave the taskbar
}

// A placement: the gauge's rectangle, or the reason there is none.
internal readonly record struct PlacementResult(Rectangle? Bounds, PlacementFailure Failure)
{
    public static PlacementResult Placed(Rectangle bounds) => new(bounds, PlacementFailure.None);

    public static PlacementResult Failed(PlacementFailure failure) => new(null, failure);
}

// Pure placement maths for the taskbar gauge. No window, no native call.
//
// The gauge is 40 px tall at 100% and the taskbar 48, so it is centred on the taskbar's short axis and
// only its x is chosen. Two positions, both measured from what UI Automation read:
//
//   RightEnd (default)  its right edge 8 px (scaled) left of the notification area's left edge, which is
//                       the tray chevron when it shows, or the first tray icon when it does not
//   NextToApps          its left edge 4 px (scaled) after the last button that is not part of the
//                       notification area
//
// Either way the result must not touch any occupant and must stay inside the taskbar; otherwise there is
// no result and the tray icon stays. The maths never guesses: with no occupants read at all, or no
// notification area identified for RightEnd, there is nothing to measure from and the result is none.
//
// A left or right taskbar is not supported: Windows 11 has none, and a gauge that is wider than tall does
// not fit a vertical bar.
//
// Auto-hide: when the taskbar has slid away (the part of its rectangle that overlaps its monitor is thinner
// than its own thickness) the result is none; when it has slid in, the maths runs on the on-screen part.
internal static class GaugePlacement
{
    // Design values at 96 DPI, scaled by CardPlacement.Scale for the target DPI.
    public const int GapToNotificationAreaAt96 = 8;
    public const int GapAfterAppsAt96 = 4;

    // A margin from the taskbar's own ends within which the gauge is never placed.
    public const int EdgeMarginAt96 = 8;

    public static PlacementResult Place(TaskbarLayout layout, GaugeLayout gauge, GaugePosition position)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Edge is not (TaskbarEdge.Bottom or TaskbarEdge.Top))
        {
            return PlacementResult.Failed(PlacementFailure.UnsupportedEdge);
        }

        Rectangle t = layout.Taskbar;
        if (layout.AutoHide)
        {
            Rectangle visible = Rectangle.Intersect(t, layout.MonitorBounds);
            if (visible.Height < t.Height)
            {
                return PlacementResult.Failed(PlacementFailure.AutoHiddenAway);
            }

            t = visible;
        }

        if (t.Height < gauge.Height || t.Width < gauge.Width)
        {
            return PlacementResult.Failed(PlacementFailure.OutsideTaskbar);
        }

        var occupants = new List<Rectangle>(layout.Occupied.Count);
        foreach (Rectangle r in layout.Occupied)
        {
            Rectangle clipped = Rectangle.Intersect(r, t);
            if (clipped.Width > 0 && clipped.Height > 0)
            {
                occupants.Add(clipped);
            }
        }

        if (occupants.Count == 0)
        {
            return PlacementResult.Failed(PlacementFailure.NoAnchor);
        }

        int dpi = layout.Dpi;
        int edgeMargin = CardPlacement.Scale(EdgeMarginAt96, dpi);
        int? trayLeft = layout.NotificationArea is { } area
            ? Rectangle.Intersect(area, t) is { Width: > 0, Height: > 0 } clippedArea ? clippedArea.Left : null
            : null;

        int x;
        if (position == GaugePosition.NextToApps)
        {
            int appsEnd = int.MinValue;
            foreach (Rectangle o in occupants)
            {
                if (trayLeft is { } tl && o.Left >= tl)
                {
                    continue;
                }

                appsEnd = Math.Max(appsEnd, o.Right);
            }

            if (appsEnd == int.MinValue)
            {
                return PlacementResult.Failed(PlacementFailure.NoAnchor);
            }

            x = appsEnd + CardPlacement.Scale(GapAfterAppsAt96, dpi);
        }
        else
        {
            if (trayLeft is not { } tl)
            {
                return PlacementResult.Failed(PlacementFailure.NoAnchor);
            }

            x = tl - CardPlacement.Scale(GapToNotificationAreaAt96, dpi) - gauge.Width;
        }

        Rectangle rect = gauge.BoundsAt(x, t);
        bool tooFarRight = trayLeft is null && rect.Right > t.Right - edgeMargin;
        if (rect.Left < t.Left + edgeMargin || tooFarRight || !t.Contains(rect))
        {
            return PlacementResult.Failed(PlacementFailure.OutsideTaskbar);
        }

        if (trayLeft is { } notificationLeft && rect.Right > notificationLeft)
        {
            return PlacementResult.Failed(PlacementFailure.NoRoom);
        }

        foreach (Rectangle o in occupants)
        {
            if (o.IntersectsWith(rect))
            {
                return PlacementResult.Failed(PlacementFailure.NoRoom);
            }
        }

        return PlacementResult.Placed(rect);
    }
}
