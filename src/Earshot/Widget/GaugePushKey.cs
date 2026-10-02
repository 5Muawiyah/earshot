using System.Drawing;

namespace Earshot.Widget;

// What the last push of the gauge's bitmap drew: a repaint that would draw exactly the same is not pushed again, so the poll
// (which asks once a second) never redraws for nothing. The tooltip is not part of it, since it is not drawn into the
// bitmap: its text carries the age of the reading ("Read 45 s ago"), which moves every second, and a key that included it
// pushed a new bitmap through UpdateLayeredWindow for each tick of it. The tooltip is set apart, only when its text changes
// (GaugeWindow).
internal sealed record GaugePushKey(GaugeContent Content, GaugePalette Palette, GaugeLayout Layout, bool Hover, string FontFamily, Point Location)
{
    public static GaugePushKey Of(GaugeContent content, GaugePalette palette, GaugeLayout layout, bool hover, string fontFamily, Point location)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new GaugePushKey(content with { Tooltip = "" }, palette, layout, hover, fontFamily, location);
    }
}
