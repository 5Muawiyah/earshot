using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Earshot.Widget;

// Draws the gauge bitmap: the earbud pair (or, with the AirPods away, the case mark) in a ring that fills to the lower bud's
// battery (or the case's), that number beside it, and a charging bolt in a slot that is always kept. Pure drawing, used by
// GaugeWindow, the tests and the screenshot probe. Every coordinate comes from GaugeLayout, every colour from GaugePalette.
//
// What each state draws (the design's table):
//   reading          ring track and arc (accent; caution when low), the pair in ink, the value, the bolt when charging
//   no recent reading  ring track only, the pair in tertiary
//   not on this PC   no ring, the pair in disabled
//   on another device  no ring, the pair in tertiary and the phone glyph in the number slot, tertiary
//   away, case value  ring track and arc in tertiary, the case mark in tertiary, the value (≈ when estimated) in tertiary,
//                     the bolt when the case was charging; a live case value is drawn in full ink
//
// GDI+ only (Graphics.FillPath, DrawString with AntiAliasGridFit): GDI text (TextRenderer) writes alpha
// 0 and would vanish on a layered window (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
//
// The number is drawn one digit at a time in cells as wide as a "0", so every digit has the same width and
// the number never shifts as it changes (GDI+ has no tabular figures switch). The cells are centred in the number slot,
// which fits three digits.
internal static class GaugeRenderer
{
    // The phone mark where no icon font is installed (a rounded outline, drawn on a 16 unit grid): the rectangle and its
    // corner radius and stroke, in grid units.
    private const float PhoneGrid = 16f;
    private const float PhoneX = 4.6f;
    private const float PhoneY = 1.6f;
    private const float PhoneWidth = 6.8f;
    private const float PhoneHeight = 12.8f;
    private const float PhoneCorner = 1.6f;
    private const float PhoneStroke = 1.2f;

    // The bolt where no icon font is installed, on a 9 by 12 grid.
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

        // A last reading or an estimate is drawn in tertiary ink throughout; the earbud pair stays at full ink on this PC,
        // since the AirPods themselves are here.
        Color ink = content.Tertiary ? palette.Tertiary : palette.Ink;
        switch (content.Mode)
        {
            case GaugeMode.Reading:
                DrawEarbudPair(g, layout, palette.Ink);
                DrawRing(g, layout, palette, content);
                DrawNumber(g, layout, content, palette, fontFamily);
                if (content.Charging)
                {
                    DrawBolt(g, layout, ink);
                }

                break;

            case GaugeMode.CaseAway:
                DrawCaseMark(g, CaseMarkRect(layout), ink);
                DrawRing(g, layout, palette, content);
                DrawNumber(g, layout, content, palette, fontFamily);
                if (content.Charging)
                {
                    DrawBolt(g, layout, ink);
                }

                break;

            case GaugeMode.OnOtherDevice:
                DrawEarbudPair(g, layout, palette.Tertiary);
                DrawPhone(g, layout, palette.Tertiary);
                break;

            case GaugeMode.MarkOnly:
                DrawEarbudPair(g, layout, palette.Tertiary);
                DrawRingTrack(g, layout, palette);
                break;

            case GaugeMode.NotOnThisPc:
            default:
                DrawEarbudPair(g, layout, palette.Disabled);
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

    // The earbud pair: two heads and two stems, filled in the ink, on the design's 24 unit grid scaled to the ring box.
    private static void DrawEarbudPair(Graphics g, GaugeLayout layout, Color ink)
    {
        (RectangleF[] shapes, float radius) = layout.EarbudShapes();
        using var path = new GraphicsPath { FillMode = FillMode.Winding };
        foreach (RectangleF shape in shapes)
        {
            AddRounded(path, shape, radius);
        }

        using var brush = new SolidBrush(ink);
        g.FillPath(brush, path);
    }

    private static void AddRounded(GraphicsPath path, RectangleF rect, float radius)
    {
        using GraphicsPath one = RoundedRectangle(rect, radius);
        path.AddPath(one, connect: false);
    }

    // The track is the whole circle; the arc runs from 12 o'clock clockwise for the battery's share of it, with round caps.
    // GDI+ measures angles clockwise from 3 o'clock, so 12 o'clock is -90.
    private static void DrawRingTrack(Graphics g, GaugeLayout layout, GaugePalette palette)
    {
        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        using var track = new Pen(palette.Track, layout.RingStroke);
        g.DrawEllipse(track, new RectangleF(c.X - r, c.Y - r, r * 2, r * 2));
    }

    private static void DrawRing(Graphics g, GaugeLayout layout, GaugePalette palette, GaugeContent content)
    {
        DrawRingTrack(g, layout, palette);
        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        var square = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
        int percent = content.Percent ?? 0;
        if (percent <= 0)
        {
            return;
        }

        // Tertiary before caution: a value that is not live is never drawn as if it were a current warning.
        using var fill = new Pen(content.Tertiary ? palette.Tertiary : content.Low ? palette.Caution : palette.Accent, layout.RingStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
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
        float x = layout.NumberSlot.X + ((layout.NumberSlot.Width - (cell * text.Length)) / 2f);
        foreach (char digit in text)
        {
            g.DrawString(digit.ToString(), font, brush, new RectangleF(x, layout.NumberSlot.Y, cell, layout.NumberSlot.Height), format);
            x += cell;
        }
    }

    // Where the case mark goes: the middle 14 units of the ring box's 24 unit grid (a design choice, so it sits inside the ring
    // as the pair does).
    internal static RectangleF CaseMarkRect(GaugeLayout layout)
    {
        float k = layout.Mark.Width / (float)GaugeLayout.MarkGrid;
        return new RectangleF(layout.Mark.X + (5 * k), layout.Mark.Y + (5 * k), 14 * k, 14 * k);
    }

    // The case, our own shape, in place of the earbud mark and the size of it: a filled rounded box wider than it is tall,
    // with a thin unpainted seam a little below the top for the lid. Drawn in the ink it is given, never the accent.
    private static void DrawCaseMark(Graphics g, RectangleF mark, Color ink)
    {
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

    // The bolt: the Fluent glyph E945 at 12, centred in its slot; the five-point shape where no icon font is installed.
    private static void DrawBolt(Graphics g, GaugeLayout layout, Color ink)
    {
        if (!DrawGlyph(g, FluentGlyphs.Bolt, layout.ChargingSlot, layout.Bolt.Height, ink))
        {
            using var brush = new SolidBrush(ink);
            g.FillPolygon(brush, BoltPoints(layout));
        }
    }

    // One Segoe Fluent Icons glyph of the given pixel size centred in a rectangle; false when no icon font is installed.
    private static bool DrawGlyph(Graphics g, char codePoint, Rectangle slot, int pixels, Color colour)
    {
        string? family = FluentGlyphs.Family;
        if (family is null)
        {
            return false;
        }

        using var font = new Font(family, Math.Max(1, pixels), FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        g.DrawString(codePoint.ToString(), font, brush, slot, format);
        return true;
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

    // The phone, in the number slot: the Fluent glyph E8EA, centred; an outlined rounded rectangle on a 16 unit grid without the font.
    private static void DrawPhone(Graphics g, GaugeLayout layout, Color ink)
    {
        if (DrawGlyph(g, FluentGlyphs.CellPhone, layout.NumberSlot, layout.PhoneSize, ink))
        {
            return;
        }

        float k = layout.PhoneSize / PhoneGrid;
        float left = layout.NumberSlot.X + ((layout.NumberSlot.Width - layout.PhoneSize) / 2f);
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
