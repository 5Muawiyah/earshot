using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Icons;

// One icon drawn, as a test records it: what, in which style and box, in which ink, where the box stood and which line of code drew it.
internal readonly record struct DrawnIcon(Icon Icon, IconStyle Style, int BoxPx, Color Ink, Rectangle Box, IconRule Rule, int RotationStep, string Site);

// Draws an icon from its coverage mask. The mask is turned into a bitmap of straight (not premultiplied) alpha, the ink's colour in
// every pixel and its alpha times the coverage, and drawn once, a pixel to a pixel, at whole-pixel positions: nothing is scaled,
// shifted or resampled, so the edges are as sharp as the mask is. The Graphics' own state (smoothing, interpolation, pixel offset,
// compositing quality) is set for that and put back; the bitmap carries no resolution of its own that a Graphics could scale by,
// because the source and the destination rectangles are given in pixels.
internal static class IconPainter
{
    // Every icon drawn on this thread, for a test that checks each site drew the icon it should. Null outside such a test, so
    // nothing is kept. Per thread, like CardPaint.DrawnTextLog.
    [ThreadStatic]
    internal static List<DrawnIcon>? DrawnIconLog;

    // True while a test renders a page for its background alone: every icon is still logged, none is painted. Per thread.
    [ThreadStatic]
    internal static bool PaintNothingForTest;

    // The box's top-left corner for an icon of boxPx pixels centred in bounds: whole pixels, by integer division.
    public static Point BoxOrigin(Rectangle bounds, int boxPx) =>
        new(bounds.X + ((bounds.Width - boxPx) / 2), bounds.Y + ((bounds.Height - boxPx) / 2));

    // The icon in a boxPx square centred in bounds, in ink (its alpha is the icon's opacity). rotationStep turns it about the middle
    // of the box, a tenth of a turn clockwise a step.
    public static void Draw(
        Graphics g,
        Icon icon,
        IconStyle style,
        Rectangle bounds,
        int boxPx,
        Color ink,
        IconRule rule = IconRule.A,
        int rotationStep = 0,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentNullException.ThrowIfNull(g);
        Point origin = BoxOrigin(bounds, boxPx);
        var box = new Rectangle(origin, new Size(boxPx, boxPx));
        DrawnIconLog?.Add(new DrawnIcon(icon, style, boxPx, ink, box, rule, rotationStep, Path.GetFileName(callerFile) + ":" + callerLine));

        ReadOnlyMemory<byte> mask = IconRaster.Coverage(icon, style, boxPx, rule, rotationStep);
        if (ink.A == 0 || PaintNothingForTest)
        {
            return;
        }

        int side = IconRaster.MaskSide(boxPx);
        int[] pixels = Argb(mask.Span, ink);
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(side, side, side * 4, PixelFormat.Format32bppArgb, pin.AddrOfPinnedObject());
            GraphicsState state = g.Save();
            try
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(bitmap, new Rectangle(origin.X - IconRaster.Apron, origin.Y - IconRaster.Apron, side, side), 0, 0, side, side, GraphicsUnit.Pixel);
            }
            finally
            {
                g.Restore(state);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    // The mask in ink: the ink's colour, its alpha times the coverage (rounded).
    internal static int[] Argb(ReadOnlySpan<byte> mask, Color ink)
    {
        var pixels = new int[mask.Length];
        int rgb = ink.ToArgb() & 0x00FFFFFF;
        for (int i = 0; i < pixels.Length; i++)
        {
            int alpha = ((mask[i] * ink.A) + 127) / 255;
            pixels[i] = (alpha << 24) | rgb;
        }

        return pixels;
    }
}
