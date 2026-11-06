using System.Drawing;
using Earshot.Icons;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Widget;

// Where the gauge's pair and case icons are drawn, worked out from the layout alone (not from the renderer), and a way to read the
// drawn pixel an icon's mask covers most.
internal static class GaugeMarks
{
    // 14 px at 100%, in a box centred in the ring box (IconRaster's rule A: the icon's art area fills the box).
    public static int BoxPx(GaugeLayout layout) => GaugeLayout.Round(14, layout.Dpi);

    public static Rectangle Box(GaugeLayout layout)
    {
        int px = BoxPx(layout);
        return new Rectangle(IconPainter.BoxOrigin(layout.Mark, px), new Size(px, px));
    }

    // The bitmap position of the first pixel of the icon's mask that has the highest coverage, with that coverage.
    public static (Point At, int Coverage) Strongest(Icon icon, IconStyle style, Rectangle box)
    {
        ReadOnlySpan<byte> mask = IconRaster.Coverage(icon, style, box.Width).Span;
        int side = IconRaster.MaskSide(box.Width);
        int best = -1;
        int at = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] > best)
            {
                best = mask[i];
                at = i;
            }
        }

        return (new Point(box.X - IconRaster.Apron + (at % side), box.Y - IconRaster.Apron + (at / side)), best);
    }

    // The icon's strongest pixel is drawn in the ink: its alpha is the ink's times the coverage, and where it is solid enough to read
    // a colour from, so is its colour (a half-covered pixel of a bitmap with a premultiplied alpha loses the low bits of its colour).
    public static void AssertInk(Bitmap bitmap, Icon icon, IconStyle style, Rectangle box, Color ink, string message)
    {
        (Point at, int coverage) = Strongest(icon, style, box);
        Assert.IsGreaterThanOrEqualTo(128, coverage, message + ": the " + icon + " icon has a pixel it covers at least half of");
        Color drawn = bitmap.GetPixel(at.X, at.Y);
        int alpha = ((ink.A * coverage) + 127) / 255;
        Assert.IsLessThanOrEqualTo(3, Math.Abs(drawn.A - alpha), message + ": alpha " + drawn.A + ", expected " + alpha + " of " + ink);
        if (drawn.A >= 100)
        {
            int worst = Math.Max(Math.Abs(drawn.R - ink.R), Math.Max(Math.Abs(drawn.G - ink.G), Math.Abs(drawn.B - ink.B)));
            Assert.IsLessThanOrEqualTo(8, worst, message + ": colour " + drawn + ", expected " + ink);
        }
    }
}
