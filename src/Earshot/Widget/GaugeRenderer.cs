using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using Earshot.Icons;
using Earshot.Popup;

namespace Earshot.Widget;

// Draws the gauge bitmap: the earbud mark, the lower bud's battery as a bar and a number, a charging
// bolt, and a hover pill behind the whole gauge (the taskbar's own hover look). Pure drawing, used by
// GaugeWindow, the tests and the screenshot probe. widget-ui.md section 6.
//
// GDI+ only (Graphics.FillPath, DrawString with AntiAliasGridFit): GDI text (TextRenderer) writes alpha
// 0 and would vanish on a layered window (research.json F10).
internal static class GaugeRenderer
{
    // Layout at 96 DPI: | 8 | mark 20 | 6 | bar 24 x 6 | 4 | number (room for three digits, 9 pt) | 8 |
    public const int PaddingAt96 = 8;
    public const int MarkSizeAt96 = 20;
    public const int GapAfterMarkAt96 = 6;
    public const int BarWidthAt96 = 24;
    public const int BarHeightAt96 = 6;
    public const int GapAfterBarAt96 = 4;
    public const int NumberWidthAt96 = 18; // 88 total: 8 + 20 + 6 + 24 + 4 + 18 + 8 (widget-ui.md section 6)
    public const int BoltWidthAt96 = 6;
    public const int BoltHeightAt96 = 10;
    public const float NumberPoints = 9f;

    // Track ink and hover pill ink, as a fraction of full alpha. Design choices.
    public const double TrackInk = 0.30;
    public const double HoverPillInk = 0.12;
    public const byte IdlePillAlpha = 1;

    public static int WidthFor(int dpi) =>
        CardPlacement.Scale(PaddingAt96, dpi) + CardPlacement.Scale(MarkSizeAt96, dpi) + CardPlacement.Scale(GapAfterMarkAt96, dpi) +
        CardPlacement.Scale(BarWidthAt96, dpi) + CardPlacement.Scale(GapAfterBarAt96, dpi) + CardPlacement.Scale(NumberWidthAt96, dpi) +
        CardPlacement.Scale(PaddingAt96, dpi);

    // The gauge bitmap, width x height (height is the taskbar's own thickness), Format32bppPArgb so it
    // can go straight to UpdateLayeredWindow. The caller disposes it.
    public static Bitmap Render(WidgetSnapshot snapshot, int dpi, int height, Color ink, bool hover, string fontFamily)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(fontFamily);
        int width = Math.Max(1, WidthFor(dpi));
        int h = Math.Max(1, height);
        var bitmap = new Bitmap(width, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            byte pillAlpha = hover ? (byte)Math.Round(255 * HoverPillInk) : IdlePillAlpha;
            using (GraphicsPath pillPath = Pill(new Rectangle(0, 0, width, h)))
            using (var pillBrush = new SolidBrush(Color.FromArgb(pillAlpha, ink)))
            {
                g.FillPath(pillBrush, pillPath);
            }

            bool dimmed = snapshot.Where != AirPodsWhere.ThisPc;
            double opacity = dimmed ? EarbudGlyph.BusyOpacity : 1.0;
            int markSize = CardPlacement.Scale(MarkSizeAt96, dpi);
            int pad = CardPlacement.Scale(PaddingAt96, dpi);
            int markTop = Math.Max(0, (h - markSize) / 2);
            GlyphState state = snapshot.Where == AirPodsWhere.ThisPc ? GlyphState.Connected : GlyphState.Disconnected;
            DrawGlyph(g, pad, markTop, markSize, state, ink, opacity);

            (int? percent, bool? charging) = LowerReading(snapshot);
            int barLeft = pad + markSize + CardPlacement.Scale(GapAfterMarkAt96, dpi);
            int barWidth = CardPlacement.Scale(BarWidthAt96, dpi);
            int barHeight = CardPlacement.Scale(BarHeightAt96, dpi);
            int barTop = Math.Max(0, (h - barHeight) / 2);

            if (percent is { } shown)
            {
                using (var trackBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * TrackInk * opacity), ink)))
                {
                    g.FillRectangle(trackBrush, barLeft, barTop, barWidth, barHeight);
                }

                int filled = (int)Math.Round(barWidth * Math.Clamp(shown, 0, 100) / 100.0);
                if (filled > 0)
                {
                    using var fillBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * opacity), ink));
                    g.FillRectangle(fillBrush, barLeft, barTop, filled, barHeight);
                }

                if (charging == true)
                {
                    int boltHeight = CardPlacement.Scale(BoltHeightAt96, dpi);
                    DrawBolt(g, barLeft + barWidth, Math.Max(0, (h - boltHeight) / 2), dpi, ink, opacity);
                }

                int numberLeft = barLeft + barWidth + CardPlacement.Scale(GapAfterBarAt96, dpi);
                using var font = new Font(fontFamily, NumberPoints * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * opacity), ink));
                var numberBounds = new RectangleF(numberLeft, 0, CardPlacement.Scale(NumberWidthAt96, dpi), h);
                var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
                g.DrawString(shown.ToString(CultureInfo.InvariantCulture), font, textBrush, numberBounds, format);
            }
        }

        return bitmap;
    }

    // The bar and number bounds for a rendered gauge, in client pixels, for tests that sample pixels.
    internal static (Rectangle Bar, Rectangle Number) ContentBounds(int dpi, int height)
    {
        int pad = CardPlacement.Scale(PaddingAt96, dpi);
        int markSize = CardPlacement.Scale(MarkSizeAt96, dpi);
        int barLeft = pad + markSize + CardPlacement.Scale(GapAfterMarkAt96, dpi);
        int barWidth = CardPlacement.Scale(BarWidthAt96, dpi);
        int barHeight = CardPlacement.Scale(BarHeightAt96, dpi);
        int barTop = Math.Max(0, (height - barHeight) / 2);
        int numberLeft = barLeft + barWidth + CardPlacement.Scale(GapAfterBarAt96, dpi);
        return (new Rectangle(barLeft, barTop, barWidth, barHeight), new Rectangle(numberLeft, 0, CardPlacement.Scale(NumberWidthAt96, dpi), height));
    }

    // The lower of the two buds' percent (the minimum of left and right among the known values; one
    // null uses the other; both null means no reading at all), and the charging flag of whichever bud
    // that percent came from. Left wins a tie: the design does not care which bud reads first.
    private static (int? Percent, bool? Charging) LowerReading(WidgetSnapshot snapshot)
    {
        int? left = snapshot.Left.Percent;
        int? right = snapshot.Right.Percent;
        if (left is null && right is null)
        {
            return (null, null);
        }

        if (left is null)
        {
            return (right, snapshot.Right.Charging);
        }

        if (right is null)
        {
            return (left, snapshot.Left.Charging);
        }

        return left <= right ? (left, snapshot.Left.Charging) : (right, snapshot.Right.Charging);
    }

    private static void DrawGlyph(Graphics g, int x, int y, int px, GlyphState state, Color ink, double opacity)
    {
        if (px < EarbudGlyph.MinSize)
        {
            return;
        }

        byte[] alpha = EarbudGlyph.Coverage(px, state);
        if (opacity < 1.0)
        {
            for (int i = 0; i < alpha.Length; i++)
            {
                alpha[i] = (byte)Math.Round(alpha[i] * opacity);
            }
        }

        using Bitmap glyph = EarbudGlyph.ToBitmap(alpha, px, ink);
        g.DrawImageUnscaled(glyph, x, y);
    }

    // A five-point lightning bolt, scaled to a BoltWidthAt96 x BoltHeightAt96 box. A layout choice, not
    // a measurement.
    private static void DrawBolt(Graphics g, int x, int y, int dpi, Color ink, double opacity)
    {
        float w = CardPlacement.Scale(BoltWidthAt96, dpi);
        float h = CardPlacement.Scale(BoltHeightAt96, dpi);
        PointF[] points =
        [
            new PointF(x + (w * 0.55f), y),
            new PointF(x, y + (h * 0.6f)),
            new PointF(x + (w * 0.42f), y + (h * 0.6f)),
            new PointF(x + (w * 0.45f), y + h),
            new PointF(x + w, y + (h * 0.38f)),
            new PointF(x + (w * 0.58f), y + (h * 0.38f)),
        ];
        using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * opacity), ink));
        g.FillPolygon(brush, points);
    }

    // A stadium (fully rounded ends) covering bounds, or a plain rectangle when it is too short to
    // round. The whole gauge is one clickable target: layered-window hit testing follows the painted
    // pixels (research.json F10), so nothing outside this shape ever answers a click.
    private static GraphicsPath Pill(Rectangle bounds)
    {
        var path = new GraphicsPath();
        int r = Math.Min(bounds.Width, bounds.Height) / 2;
        if (r <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                path.AddRectangle(bounds);
            }

            return path;
        }

        int d = r * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
