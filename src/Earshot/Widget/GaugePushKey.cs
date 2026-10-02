using System.Drawing;

namespace Earshot.Widget;

// What the last push of the gauge's bitmap drew: a repaint that would draw exactly the same is not pushed again, so the poll
// (which asks once a second) never redraws for nothing. The tooltip is not part of it, since it is not drawn into the
// bitmap: its text carries the age of the reading ("Read 45 s ago"), which moves every second, and a key that included it
// pushed a new bitmap through UpdateLayeredWindow for each tick of it. The tooltip is set apart, only when its text changes
// (GaugeWindow).
//
// Hover and Ring are where the hover fill and the ring's arc are drawn: at rest, 0 or 1 and the content's own percentage; part
// way through a motion, the frame's values, so each frame of a motion is a push and nothing at rest is.
internal sealed record GaugePushKey(GaugeContent Content, GaugePalette Palette, GaugeLayout Layout, double Hover, string FontFamily, Point Location, double Ring)
{
    public static GaugePushKey Of(GaugeContent content, GaugePalette palette, GaugeLayout layout, bool hover, string fontFamily, Point location) =>
        Of(content, palette, layout, hover ? 1 : 0, fontFamily, location, content?.Percent ?? 0);

    public static GaugePushKey Of(GaugeContent content, GaugePalette palette, GaugeLayout layout, double hover, string fontFamily, Point location, double ring)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new GaugePushKey(content with { Tooltip = "" }, palette, layout, hover, fontFamily, location, ring);
    }
}
