using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.Icons;

namespace Earshot.Widget;

// Draws the gauge bitmap: the earbud mark (or, with the AirPods away, the case mark), a ring round it that fills to the
// lower bud's battery (or the case's), that number beside it, and a charging bolt in a slot that is always kept. Pure drawing, used by GaugeWindow, the
// tests and the screenshot probe. Every coordinate comes from GaugeLayout.
//
// GDI+ only (Graphics.FillPath, DrawString with AntiAliasGridFit): GDI text (TextRenderer) writes alpha
// 0 and would vanish on a layered window (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
//
// The number is drawn one digit at a time in cells as wide as a "0", so every digit has the same width and
// the number never shifts as it changes (GDI+ has no tabular figures switch). The cells are left aligned in
// the number slot, which fits three digits.
internal static class GaugeRenderer
{
    // The earbud mark's opacity when the AirPods are not on this PC.
    public const double AwayOpacity = EarbudGlyph.BusyOpacity;

    // The phone mark (a rounded outline, drawn on a 16 unit grid): the rectangle and its corner radius and
    // stroke, in grid units.
    private const float PhoneGrid = 16f;
    private const float PhoneX = 4.6f;
    private const float PhoneY = 1.6f;
    private const float PhoneWidth = 6.8f;
    private const float PhoneHeight = 12.8f;
    private const float PhoneCorner = 1.6f;
    private const float PhoneStroke = 1.2f;

    // The bolt, on a 9 by 12 grid.
    private static readonly PointF[] BoltGrid =
    [
        new(6f, 0f), new(1f, 7f), new(4.5f, 7f), new(3.5f, 12f), new(9f, 4.5f), new(5.5f, 4.5f),
    ];

    private const float BoltGridWidth = 9f;
    private const float BoltGridHeight = 12f;

    // The gauge for a snapshot at a moment: the content the snapshot gives, drawn in the palette for the
    // theme the ink says (a dark ink is the light theme). height is the taskbar's own thickness, which the
    // gauge no longer depends on (the window is a fixed size, centred by the placement); it stays in the
    // signature for callers that still pass it.
    public static Bitmap Render(
        WidgetSnapshot snapshot, DateTimeOffset now, int dpi, int height, Color ink, bool hover, string fontFamily,
        GaugeDisplaySettings? settings = null, Color? accent = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = height;
        bool light = ink.GetBrightness() < 0.5f;
        Color accentColour = accent ?? UiSettingsColourSource.DefaultShade(light ? AccentShade.Dark1 : AccentShade.Light2);
        GaugePalette palette = GaugePalette.Create(light, accentColour, highContrast: false, ink);
        return Render(GaugeContent.From(snapshot, now, settings ?? GaugeDisplaySettings.Default), palette, GaugeLayout.For(dpi), hover, fontFamily);
    }

    // The gauge bitmap, layout.Width x layout.Height, Format32bppPArgb so it can go straight to
    // UpdateLayeredWindow. The caller disposes it.
    public static Bitmap Render(GaugeContent content, GaugePalette palette, GaugeLayout layout, bool hover, string fontFamily)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fontFamily);
        var bitmap = new Bitmap(layout.Width, layout.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        FillBackground(g, palette, layout, hover);

        // A last reading or an estimate is drawn in tertiary ink throughout; the earbud mark stays at full ink on this PC,
        // since the AirPods themselves are here.
        Color ink = content.Tertiary ? palette.Tertiary : palette.Ink;
        if (content.CaseMark)
        {
            DrawCaseMark(g, layout.Mark, ink);
        }
        else
        {
            double markOpacity = content.Mode == GaugeMode.NotOnThisPc ? AwayOpacity : 1.0;
            DrawMark(g, layout.Mark, palette.Ink, markOpacity);
        }

        switch (content.Mode)
        {
            case GaugeMode.Reading:
            case GaugeMode.CaseAway:
                DrawRing(g, layout, palette, content);
                DrawNumber(g, layout, content, palette, fontFamily);
                if (content.Charging)
                {
                    DrawBolt(g, layout, ink);
                }

                break;

            case GaugeMode.OnOtherDevice:
                DrawPhone(g, layout, palette.Ink);
                break;

            case GaugeMode.MarkOnly:
            case GaugeMode.NotOnThisPc:
            default:
                break;
        }

        return bitmap;
    }

    // The gauge for a picture of an order on the settings page: the gauge as it is, with the number's place marked by a
    // neutral bar when there is no number to show and the bolt drawn as an outline when it is not charging, so all three
    // pieces show in every picture. Nothing here is a figure: a bar is not a reading.
    public static Bitmap RenderPreview(GaugeContent content, GaugePalette palette, GaugeLayout layout, string fontFamily, Color placeholder)
    {
        Bitmap bitmap = Render(content, palette, layout, hover: false, fontFamily);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // The phone mark fills the number's place on its own.
        if (content.Mode is not (GaugeMode.Reading or GaugeMode.OnOtherDevice or GaugeMode.CaseAway))
        {
            float height = Math.Max(1f, layout.Dpi / 48f);
            var bar = new RectangleF(layout.NumberSlot.X, (layout.Height - height) / 2f, layout.NumberSlot.Width, height);
            using GraphicsPath path = RoundedRectangle(bar, height / 2f);
            using var brush = new SolidBrush(placeholder);
            g.FillPath(brush, path);
        }

        if (!(content.Mode is (GaugeMode.Reading or GaugeMode.CaseAway) && content.Charging))
        {
            using var pen = new Pen(placeholder, Math.Max(1f, layout.Dpi / 96f)) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, BoltPoints(layout));
        }

        return bitmap;
    }

    // The centre of the ring, and the ring's own square.
    internal static PointF RingCentre(GaugeLayout layout) =>
        new(layout.RingBox.X + (layout.RingBox.Width / 2f), layout.RingBox.Y + (layout.RingBox.Height / 2f));

    // The digit cell: the advance of a "0" at the gauge's type size, measured, not assumed.
    internal static float DigitCell(string fontFamily, int typePixels)
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(fontFamily, typePixels, FontStyle.Regular, GraphicsUnit.Pixel);
        return g.MeasureString("0", font, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    // The whole window: the hover fill while pointed at, otherwise an alpha of 1 so a click lands on the
    // gauge. Layered-window hit testing follows the painted pixels, so nothing outside this rounded rectangle
    // ever answers a click.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows
    private static void FillBackground(Graphics g, GaugePalette palette, GaugeLayout layout, bool hover)
    {
        using GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, layout.Width, layout.Height), layout.CornerRadius);
        using var brush = new SolidBrush(hover ? palette.HoverFill : palette.IdleFill);
        g.FillPath(brush, path);
    }

    private static void DrawMark(Graphics g, Rectangle mark, Color ink, double opacity)
    {
        if (mark.Width < EarbudGlyph.MinSize)
        {
            return;
        }

        byte[] alpha = EarbudGlyph.Coverage(mark.Width, GlyphState.Connected);
        if (opacity < 1.0)
        {
            for (int i = 0; i < alpha.Length; i++)
            {
                alpha[i] = (byte)Math.Round(alpha[i] * opacity);
            }
        }

        using Bitmap glyph = EarbudGlyph.ToBitmap(alpha, mark.Width, ink);
        g.DrawImageUnscaled(glyph, mark.X, mark.Y);
    }

    // The track is the whole circle; the fill runs from 12 o'clock clockwise for the battery's share of it.
    // Flat ends. GDI+ measures angles clockwise from 3 o'clock, so 12 o'clock is -90.
    private static void DrawRing(Graphics g, GaugeLayout layout, GaugePalette palette, GaugeContent content)
    {
        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        var square = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);

        using (var track = new Pen(palette.Track, layout.RingStroke))
        {
            g.DrawEllipse(track, square);
        }

        int percent = content.Percent ?? 0;
        if (percent <= 0)
        {
            return;
        }

        // Tertiary before caution: a value that is not live is never drawn as if it were a current warning.
        using var fill = new Pen(content.Tertiary ? palette.Tertiary : content.Low ? palette.Caution : palette.Accent, layout.RingStroke);
        if (percent >= 100)
        {
            g.DrawEllipse(fill, square);
        }
        else
        {
            g.DrawArc(fill, square, -90f, 360f * percent / 100f);
        }
    }

    // An estimate carries "≈" before its digits in a cell of its own. The slot fits three cells, so "≈100" is drawn at
    // three quarters of the type size, which fits four.
    private static void DrawNumber(Graphics g, GaugeLayout layout, GaugeContent content, GaugePalette palette, string fontFamily)
    {
        if (content.Percent is not { } percent)
        {
            return;
        }

        string text = (content.Estimated ? WidgetCopy.EstimateSign : "") + percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int typePixels = text.Length > 3 ? Math.Max(1, layout.TypePixels * 3 / text.Length) : layout.TypePixels;
        float cell = DigitCell(fontFamily, typePixels);
        using var font = new Font(fontFamily, typePixels, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(content.Tertiary ? palette.Tertiary : content.Low ? palette.Caution : palette.Ink);
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
        float x = layout.NumberAlignRight ? layout.NumberSlot.Right - (cell * text.Length) : layout.NumberSlot.X;
        foreach (char digit in text)
        {
            g.DrawString(digit.ToString(), font, brush, new RectangleF(x, layout.NumberSlot.Y, cell, layout.NumberSlot.Height), format);
            x += cell;
        }
    }

    // The case, our own shape, in place of the earbud mark and the size of it: a filled rounded box wider than it is tall,
    // with a thin unpainted seam a little below the top for the lid. Drawn in the ink it is given, never the accent.
    private static void DrawCaseMark(Graphics g, Rectangle mark, Color ink)
    {
        if (mark.Width < EarbudGlyph.MinSize)
        {
            return;
        }

        float w = mark.Width;
        float h = mark.Height * 0.8f;
        float top = mark.Y + ((mark.Height - h) / 2f);
        float seam = Math.Max(1f, h * 0.1f);
        float lidHeight = h * 0.3f;
        var lid = new RectangleF(mark.X, top, w, lidHeight);
        var box = new RectangleF(mark.X, top + lidHeight + seam, w, Math.Max(0f, h - lidHeight - seam));
        float radius = h * 0.3f;
        using var brush = new SolidBrush(ink);
        using (GraphicsPath lidPath = TopRounded(lid, radius))
        {
            g.FillPath(brush, lidPath);
        }

        using GraphicsPath boxPath = BottomRounded(box, radius);
        g.FillPath(brush, boxPath);
    }

    private static GraphicsPath TopRounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height * 2));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath BottomRounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height * 2));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddLine(r.X, r.Y, r.Right, r.Y);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawBolt(Graphics g, GaugeLayout layout, Color ink)
    {
        using var brush = new SolidBrush(ink);
        g.FillPolygon(brush, BoltPoints(layout));
    }

    // The bolt's corners, centred in its slot.
    private static PointF[] BoltPoints(GaugeLayout layout)
    {
        float k = Math.Min(layout.Bolt.Width / BoltGridWidth, layout.Bolt.Height / BoltGridHeight);
        float left = layout.ChargingSlot.X + ((layout.ChargingSlot.Width - (BoltGridWidth * k)) / 2f);
        float top = (layout.Height - (BoltGridHeight * k)) / 2f;
        var points = new PointF[BoltGrid.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new PointF(left + (BoltGrid[i].X * k), top + (BoltGrid[i].Y * k));
        }

        return points;
    }

    // The phone, in the number slot: an outlined rounded rectangle on a 16 unit grid, drawn at full ink.
    private static void DrawPhone(Graphics g, GaugeLayout layout, Color ink)
    {
        float k = layout.PhoneSize / PhoneGrid;
        float left = layout.NumberAlignRight ? layout.NumberSlot.Right - layout.PhoneSize : layout.NumberSlot.X;
        float top = (layout.Height - layout.PhoneSize) / 2f;
        var rect = new RectangleF(left + (PhoneX * k), top + (PhoneY * k), PhoneWidth * k, PhoneHeight * k);
        using GraphicsPath path = RoundedRectangle(rect, PhoneCorner * k);
        using var pen = new Pen(ink, PhoneStroke * k);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
