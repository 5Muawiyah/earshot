using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Icons;

// The four tray states. Errors never change the glyph; they go to the tooltip and the card.
internal enum GlyphState
{
    Disconnected,   // the AirPods icon, line drawing
    Connected,      // the AirPods icon, filled drawing
    Busy,           // the filled drawing at reduced opacity (connecting, disconnecting, allowing)
    Blocked         // the line drawing with one thin diagonal slash
}

// The tray icon at any pixel size: MingCute's AirPods icon (see IconCatalogue), the line drawing for Disconnected and Blocked and
// the filled one for Connected and Busy, in the coverage the icon painter uses (IconRaster, rule A, so the icon's art area fills the
// box). Busy is the filled coverage at 0.4 of its strength; Blocked is the line drawing with Earshot's own slash: a thin diagonal
// from the upper left to the lower right of the 16 unit grid, snapped to whole pixels, with a clear gap either side of it where it
// crosses the outline so it stays readable.
//
// The coverage is written as a straight (not premultiplied) alpha bitmap with the ink colour in every pixel, which is what a PNG
// frame in an ICO needs: Bitmap.GetHicon premultiplies and darkens the antialiased edges of a white glyph on a dark taskbar, so it
// is never used.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
internal static class TrayGlyph
{
    public const int DesignGrid = 16;
    public const int MinSize = 8;
    public const int MaxSize = 256;

    // Opacity of the busy variant.
    public const double BusyOpacity = 0.4;

    private const int SamplesPerAxis = 8;

    // Outline width of the line drawing at a size, which the slash takes: 1 px at 16 and 20, 2 px at 24 to 32, 3 px at 40 and 48.
    public static int StrokeFor(int px)
    {
        ValidateSize(px);
        return Math.Max(1, (int)Math.Round(px / (double)DesignGrid, MidpointRounding.AwayFromZero));
    }

    // Alpha per pixel, row by row, px * px bytes.
    public static byte[] Coverage(int px, GlyphState state)
    {
        ValidateSize(px);
        switch (state)
        {
            case GlyphState.Disconnected:
                return Box(Icon.AirPods, IconStyle.Line, px);
            case GlyphState.Connected:
                return Box(Icon.AirPods, IconStyle.Filled, px);
            case GlyphState.Busy:
                byte[] filled = Box(Icon.AirPods, IconStyle.Filled, px);
                for (int i = 0; i < filled.Length; i++)
                {
                    filled[i] = (byte)Math.Round(filled[i] * BusyOpacity, MidpointRounding.AwayFromZero);
                }

                return filled;
            case GlyphState.Blocked:
                return Slashed(Box(Icon.AirPods, IconStyle.Line, px), px);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown glyph state.");
        }
    }

    // The icon's box out of its mask: the mask is a pixel wider on every side, and the tray icon is the box.
    private static byte[] Box(Icon icon, IconStyle style, int px)
    {
        ReadOnlySpan<byte> mask = IconRaster.Coverage(icon, style, px).Span;
        int side = IconRaster.MaskSide(px);
        var alpha = new byte[px * px];
        for (int y = 0; y < px; y++)
        {
            mask.Slice(((y + IconRaster.Apron) * side) + IconRaster.Apron, px).CopyTo(alpha.AsSpan(y * px, px));
        }

        return alpha;
    }

    // The slash over the line drawing: where a pixel's samples lie within the slash's half width they are ink, where they lie
    // within the gap beyond that the outline is taken away, and the outline stays elsewhere.
    private static byte[] Slashed(byte[] outline, int px)
    {
        double unit = px / (double)DesignGrid;
        double start = Math.Round(2 * unit, MidpointRounding.AwayFromZero);
        double end = Math.Round(14 * unit, MidpointRounding.AwayFromZero);
        int stroke = StrokeFor(px);
        double half = stroke / 2.0;
        double cleared = half + Math.Max(1, stroke - 1);
        double span = end - start;
        int samples = SamplesPerAxis * SamplesPerAxis;
        var alpha = new byte[px * px];
        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                int ink = 0;
                int gap = 0;
                for (int sy = 0; sy < SamplesPerAxis; sy++)
                {
                    double sampleY = y + ((sy + 0.5) / SamplesPerAxis);
                    for (int sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        double sampleX = x + ((sx + 0.5) / SamplesPerAxis);

                        // The slash runs along y = x, so the foot of the perpendicular is the mean of the two offsets.
                        double t = Math.Clamp(((sampleX - start) + (sampleY - start)) / (2 * span), 0, 1);
                        double dx = sampleX - (start + (t * span));
                        double dy = sampleY - (start + (t * span));
                        double distance = Math.Sqrt((dx * dx) + (dy * dy));
                        ink += distance <= half ? 1 : 0;
                        gap += distance <= cleared ? 1 : 0;
                    }
                }

                double kept = outline[(y * px) + x] * (1.0 - ((double)gap / samples));
                alpha[(y * px) + x] = (byte)Math.Min(255, Math.Round((255.0 * ink / samples) + kept, MidpointRounding.AwayFromZero));
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

    private static void ValidateSize(int px)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(px, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(px, MaxSize);
    }
}
