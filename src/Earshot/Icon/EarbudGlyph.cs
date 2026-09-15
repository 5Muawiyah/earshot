using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Earshot.Icons;

// The four tray states. Errors never change the glyph; they go to the tooltip and the card.
internal enum GlyphState
{
    Disconnected,   // outlined earbuds
    Connected,      // solid earbuds
    Busy,           // solid earbuds at reduced opacity (connecting, disconnecting, allowing)
    Blocked         // outlined earbuds with one thin diagonal slash
}

// Draws the earbud glyph at any pixel size.
//
// The shape is a pair of earbuds side by side: each a round head with a straight stem below it, the
// pair mirrored about the vertical centre line. It is laid out on a 16 unit grid and scaled by px / 16.
// The stem edges are snapped to whole pixels so the long straight lines stay crisp at 16 px, and each
// stem is at least two outline widths plus one pixel wide so the outlined stem keeps a gap inside.
//
// Each pixel's coverage is measured by testing an 8 x 8 grid of sample points against signed
// distance functions (negative inside), so antialiasing is exact and deterministic. The bitmap is
// written as straight (not premultiplied) alpha with the ink colour in every pixel, which is what a
// PNG frame in an ICO needs: Bitmap.GetHicon premultiplies and darkens the antialiased edges of a
// white glyph on a dark taskbar, so it is never used.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
internal static class EarbudGlyph
{
    public const int DesignGrid = 16;
    public const int MinSize = 8;
    public const int MaxSize = 256;

    // Opacity of the busy variant.
    public const double BusyOpacity = 0.4;

    private const int SamplesPerAxis = 8;

    internal readonly record struct Circle(double CentreX, double CentreY, double Radius)
    {
        public double Distance(double x, double y)
        {
            double dx = x - CentreX;
            double dy = y - CentreY;
            return Math.Sqrt((dx * dx) + (dy * dy)) - Radius;
        }
    }

    // An axis-aligned box with rounded corners, given by its edges.
    internal readonly record struct RoundBox(double Left, double Top, double Right, double Bottom, double CornerRadius)
    {
        public double Distance(double x, double y)
        {
            double halfWidth = (Right - Left) / 2;
            double halfHeight = (Bottom - Top) / 2;
            double r = Math.Min(CornerRadius, Math.Min(halfWidth, halfHeight));
            double qx = Math.Abs(x - ((Left + Right) / 2)) - halfWidth + r;
            double qy = Math.Abs(y - ((Top + Bottom) / 2)) - halfHeight + r;
            double outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
            double inside = Math.Min(Math.Max(qx, qy), 0);
            return outside + inside - r;
        }
    }

    // A line segment with round caps. Distance is to the centre line.
    internal readonly record struct Segment(double X1, double Y1, double X2, double Y2)
    {
        public double Distance(double x, double y)
        {
            double vx = X2 - X1;
            double vy = Y2 - Y1;
            double t = Math.Clamp((((x - X1) * vx) + ((y - Y1) * vy)) / ((vx * vx) + (vy * vy)), 0, 1);
            double dx = x - (X1 + (t * vx));
            double dy = y - (Y1 + (t * vy));
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }

    // The glyph geometry for one pixel size, in pixels. Pixel (x, y) covers [x, x+1) by [y, y+1).
    internal sealed record Layout(
        int Size,
        int Stroke,
        Circle LeftHead,
        RoundBox LeftStem,
        Circle RightHead,
        RoundBox RightStem,
        Segment Slash,
        double SlashHalfWidth,
        double SlashGap)
    {
        // Signed distance to the outline of the pair: negative inside, positive outside.
        public double Distance(double x, double y) =>
            Math.Min(
                Math.Min(LeftHead.Distance(x, y), LeftStem.Distance(x, y)),
                Math.Min(RightHead.Distance(x, y), RightStem.Distance(x, y)));
    }

    // Outline width: 1 px at 16 and 20, 2 px at 24 to 32, 3 px at 40 and 48.
    public static int StrokeFor(int px)
    {
        ValidateSize(px);
        return Math.Max(1, (int)Math.Round(px / (double)DesignGrid, MidpointRounding.AwayFromZero));
    }

    public static Layout LayoutFor(int px)
    {
        ValidateSize(px);
        double unit = px / (double)DesignGrid;
        int stroke = StrokeFor(px);

        // Left bud; the right bud mirrors it about x = px / 2.
        // The heads stay a clear pixel apart even at 16 px, so the pair never reads as one blob.
        var leftHead = new Circle(4 * unit, 4.5 * unit, 3 * unit);
        int stemWidth = Math.Max((int)Snap(3, unit), (2 * stroke) + 1);
        double stemLeft = Math.Round((4 * unit) - (stemWidth / 2.0), MidpointRounding.AwayFromZero);
        double stemRight = stemLeft + stemWidth;
        double stemTop = Snap(6, unit);
        double stemBottom = Snap(14.5, unit);
        var leftStem = new RoundBox(stemLeft, stemTop, stemRight, stemBottom, stemWidth / 2.0);
        var rightHead = leftHead with { CentreX = px - leftHead.CentreX };
        var rightStem = new RoundBox(px - stemRight, stemTop, px - stemLeft, stemBottom, stemWidth / 2.0);

        // Top left to bottom right, the usual direction for "off". Both ends share x and y, so the
        // line runs through pixel centres at 45 degrees.
        double slashStart = Snap(2, unit);
        double slashEnd = Snap(14, unit);
        var slash = new Segment(slashStart, slashStart, slashEnd, slashEnd);

        return new Layout(
            px, stroke, leftHead, leftStem, rightHead, rightStem, slash,
            SlashHalfWidth: stroke / 2.0,
            SlashGap: Math.Max(1, stroke - 1));
    }

    // Alpha per pixel, row by row, px * px bytes.
    public static byte[] Coverage(int px, GlyphState state)
    {
        Layout layout = LayoutFor(px);
        var alpha = new byte[px * px];
        int samples = SamplesPerAxis * SamplesPerAxis;
        double opacity = state == GlyphState.Busy ? BusyOpacity : 1.0;

        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < SamplesPerAxis; sy++)
                {
                    double sampleY = y + ((sy + 0.5) / SamplesPerAxis);
                    for (int sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        double sampleX = x + ((sx + 0.5) / SamplesPerAxis);
                        if (IsInk(layout, state, sampleX, sampleY))
                        {
                            inside++;
                        }
                    }
                }

                alpha[(y * px) + x] = (byte)Math.Round(255.0 * opacity * inside / samples, MidpointRounding.AwayFromZero);
            }
        }

        return alpha;
    }

    // A 32 bpp ARGB bitmap with the ink colour in every pixel and the coverage as straight alpha.
    // The caller disposes it.
    public static Bitmap ToBitmap(ReadOnlySpan<byte> alpha, int px, Color ink)
    {
        ValidateSize(px);
        if (alpha.Length != px * px)
        {
            throw new ArgumentException("The alpha buffer must hold px * px bytes.", nameof(alpha));
        }

        var pixels = new byte[px * px * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            int o = i * 4;
            pixels[o] = ink.B;
            pixels[o + 1] = ink.G;
            pixels[o + 2] = ink.R;
            pixels[o + 3] = alpha[i];
        }

        var bitmap = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // 32 bpp rows need no padding, so each row is exactly px * 4 bytes.
            for (int y = 0; y < px; y++)
            {
                Marshal.Copy(pixels, y * px * 4, data.Scan0 + (y * data.Stride), px * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    // The glyph as PNG bytes.
    public static byte[] RenderPng(int px, GlyphState state, Color ink)
    {
        byte[] alpha = Coverage(px, state);
        using Bitmap bitmap = ToBitmap(alpha, px, ink);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static bool IsInk(Layout layout, GlyphState state, double x, double y)
    {
        double d = layout.Distance(x, y);
        switch (state)
        {
            case GlyphState.Connected:
            case GlyphState.Busy:
                return d <= 0;

            case GlyphState.Disconnected:
                return d <= 0 && d > -layout.Stroke;

            case GlyphState.Blocked:
                double slash = layout.Slash.Distance(x, y);
                if (slash <= layout.SlashHalfWidth)
                {
                    return true;
                }

                // A clear gap either side of the slash keeps it readable where it crosses the outline.
                return d <= 0 && d > -layout.Stroke && slash > layout.SlashHalfWidth + layout.SlashGap;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown glyph state.");
        }
    }

    private static double Snap(double gridUnits, double unit) =>
        Math.Round(gridUnits * unit, MidpointRounding.AwayFromZero);

    private static void ValidateSize(int px)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(px, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(px, MaxSize);
    }
}
