using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Icons;

// The coverage of one icon in one box, as bytes: 255 where the icon covers a pixel wholly, 0 where it does not touch it, between
// where it covers a part. Every pixel is measured by 8 x 8 sample points: the outline is filled without smoothing into a bitmap
// eight times as wide and as high, and each block of 64 samples is averaged. That is the area the icon covers to within a few
// per cent at the sizes the card uses, which GDI+'s own smoothing is not (it errs by up to a quarter of the ink at 10 to 16 px),
// and it is the same on every PC and every run. Nothing is resampled afterwards: a mask is composited whole pixel to whole pixel.
//
// The box is boxPx square; the mask is a pixel wider on every side (Apron) because a few outlines run a little past the set's art
// area. Rule A puts the art area (units 2 to 22) on the box, rule B the whole 24 unit grid. A bud of the AirPods pair is moved
// sideways so the middle of its own bounds is the middle of the box. A turn is applied to the outline about the middle of the box,
// in steps of a tenth of a turn clockwise, before the sampling.
// https://learn.microsoft.com/en-us/dotnet/api/system.drawing.drawing2d.graphicspath
internal static class IconRaster
{
    // Samples across one pixel, in each direction.
    public const int Samples = 8;

    // Pixels of mask outside the box on each side.
    public const int Apron = 1;

    // The turns of a spinning icon: the card's spinner moves one step a frame (BatteryRefreshView.SpinFrames).
    public const int RotationSteps = 10;

    public const int MinBox = 4;
    public const int MaxBox = 256;

    // Masks kept; when the table is full it is emptied. The card needs a few dozen: one per icon, size and rotation in use.
    internal const int CacheLimit = 512;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<Key, byte[]> Cache = [];

    private readonly record struct Key(Icon Icon, IconStyle Style, int Box, IconRule Rule, int Step);

    public static int MaskSide(int boxPx) => boxPx + (2 * Apron);

    // Masks in the table now, for a test of the limit.
    internal static int CachedMasks
    {
        get
        {
            lock (Gate)
            {
                return Cache.Count;
            }
        }
    }

    // A box's mask, MaskSide x MaskSide bytes, row by row. Shared: do not change it.
    public static ReadOnlyMemory<byte> Coverage(Icon icon, IconStyle style, int boxPx, IconRule rule = IconRule.A, int rotationStep = 0)
    {
        if (boxPx is < MinBox or > MaxBox)
        {
            throw new ArgumentOutOfRangeException(nameof(boxPx), boxPx, "An icon box is " + MinBox + " to " + MaxBox + " pixels.");
        }

        int step = ((rotationStep % RotationSteps) + RotationSteps) % RotationSteps;
        IconStyle effective = style == IconStyle.Filled && IconCatalogue.HasFilled(icon) ? IconStyle.Filled : IconStyle.Line;
        var key = new Key(icon, effective, boxPx, rule, step);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out byte[]? known))
            {
                return known;
            }
        }

        byte[] mask = Render(key);
        lock (Gate)
        {
            if (Cache.Count >= CacheLimit)
            {
                Cache.Clear();
            }

            Cache[key] = mask;
        }

        return mask;
    }

    private static byte[] Render(Key key)
    {
        int side = MaskSide(key.Box);
        int big = side * Samples;
        float scale = key.Rule == IconRule.A ? key.Box / IconOutline.ContentSize : key.Box / (float)IconOutline.Grid;
        float origin = key.Rule == IconRule.A ? IconOutline.ContentStart : 0f;
        IconOutline outline = IconCatalogue.Outline(key.Icon, key.Style);

        // The art's grid unit u lands on sample (u + shift - origin) * scale * 8 + Apron * 8.
        float shift = 0f;
        if (IconCatalogue.IsBud(key.Icon))
        {
            RectangleF bounds = outline.Bounds();
            shift = (IconOutline.Grid / 2f) - (bounds.X + (bounds.Width / 2f));
        }

        float sampleScale = scale * Samples;
        float offset = ((shift - origin) * sampleScale) + (Apron * Samples);
        float centre = (Apron + (key.Box / 2f)) * Samples;
        float degrees = key.Step * (360f / RotationSteps);

        var alpha = new byte[big * big];
        using (var bitmap = new Bitmap(big, big, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            using (GraphicsPath path = outline.ToPath(sampleScale, offset, offset, degrees, centre, centre))
            using (var brush = new SolidBrush(Color.White))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.FillPath(brush, path);
            }

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, big, big), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[big * 4];
                for (int y = 0; y < big; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < big; x++)
                    {
                        alpha[(y * big) + x] = row[(x * 4) + 3];
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        var mask = new byte[side * side];
        int samples = Samples * Samples;
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < Samples; sy++)
                {
                    int rowStart = (((y * Samples) + sy) * big) + (x * Samples);
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        covered += alpha[rowStart + sx] >= 128 ? 1 : 0;
                    }
                }

                mask[(y * side) + x] = (byte)(((covered * 255) + (samples / 2)) / samples);
            }
        }

        return mask;
    }
}
