using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.Icons;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Widget;

// Draws the gauge bitmap: the earbud pair (or, with the AirPods away, the case mark) in a ring that fills to the lower bud's
// battery (or the case's), that number beside it, and a charging bolt in a slot that is always kept. Pure drawing, used by
// GaugeWindow, the tests and the screenshot probe. Every coordinate comes from GaugeLayout, every colour from GaugePalette.
//
// What each state draws (the design's table):
//   reading          ring track and arc (accent; caution when low), the pair in ink, the value, the bolt when charging
//   no recent reading  ring track only, the pair in tertiary
//   not on this PC   no ring, the pair in disabled
//   on another device  no ring, the pair in tertiary and the phone icon in the number slot, tertiary
//   away, case value  ring track and arc in tertiary, the case mark in tertiary, the value in tertiary,
//                     the bolt when the case was charging; a live case value is drawn in full ink
//
// While a connect or disconnect is told (GaugeContent.Story, GaugeStoryFrame) the number's place and the arc are the story's:
//   connecting       the mark as the state has it, the track, a 90 degree accent sweep, the number slot and the bolt empty
//   connected        the arc at the live level (the track only without one), a tick in the accent in the number's place
//   failed           the track only, a cross in caution in the number's place
//   leaving          the glyph fades out as the number (or the phone) comes back, the arc at what the state draws at rest
//
// GDI+ only (Graphics.FillPath, DrawString with AntiAliasGridFit, DrawImage for the icons): GDI text (TextRenderer) writes alpha
// 0 and would vanish on a layered window (https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
//
// Every mark is a MingCute icon drawn by IconPainter from its coverage mask (IconCatalogue): the pair (AirPods; filled while the
// AirPods are on this PC, line otherwise), the case (AirPods case, line), the bolt (filled), the phone, the tick and the cross (line).
//
// The number is drawn one digit at a time in cells as wide as a "0", so every digit has the same width and
// the number never shifts as it changes (GDI+ has no tabular figures switch). The cells are centred in the number slot,
// which fits three digits.
internal static class GaugeRenderer
{
    // The pair's and the case's box, in pixels at 100%, centred in the ring box: the mark sits inside the ring as the design draws it.
    private const int MarkAt96 = 14;

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
    public static Bitmap Render(GaugeContent content, GaugePalette palette, GaugeLayout layout, bool hover, string fontFamily) =>
        Render(content, palette, layout, hover ? 1 : 0, fontFamily, ring: null);

    // The same, part way through a motion: hover is the hover fill's strength (0 none, 1 full), and ring, when given, is where
    // the ring's arc is drawn to (a percentage, which may be fractional while it moves point to point to a new value). The
    // number is always the content's own: it changes at once.
    //
    // frame is the story's frame (the sweep's start and extent and what is in the number's place); with none, a content that has a
    // story is drawn as the story's still state (the sweep at 12 o'clock, the tick or the cross whole), and one that has none is
    // drawn as it is at rest.
    public static Bitmap Render(
        GaugeContent content, GaugePalette palette, GaugeLayout layout, double hover, string fontFamily, double? ring, GaugeStoryFrame? frame = null)
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
        if (frame is not null || content.Story.Kind != GaugeStoryKind.None)
        {
            DrawStory(g, content, palette, layout, fontFamily, frame ?? GaugeStoryFrame.Still(content));
            return bitmap;
        }

        double sweep = ring ?? content.Percent ?? 0;

        // A last reading is drawn in tertiary ink throughout; the earbud pair stays at full ink on this PC,
        // since the AirPods themselves are here.
        Color ink = content.Tertiary ? palette.Tertiary : palette.Ink;
        switch (content.Mode)
        {
            case GaugeMode.Reading:
                DrawPair(g, layout, palette.Ink, filled: true);
                DrawRing(g, layout, palette, content, sweep);
                DrawNumber(g, layout, content, palette, fontFamily);
                if (content.Charging)
                {
                    DrawBolt(g, layout, ink);
                }

                break;

            case GaugeMode.CaseAway:
                DrawCaseMark(g, layout, ink);
                DrawRing(g, layout, palette, content, sweep);
                DrawNumber(g, layout, content, palette, fontFamily);
                if (content.Charging)
                {
                    DrawBolt(g, layout, ink);
                }

                break;

            case GaugeMode.OnOtherDevice:
                DrawPair(g, layout, palette.Tertiary, filled: false);
                DrawPhone(g, layout, palette.Tertiary);
                break;

            case GaugeMode.MarkOnly:
                DrawPair(g, layout, palette.Tertiary, filled: true);
                DrawRingTrack(g, layout, palette);
                break;

            case GaugeMode.NotOnThisPc:
            default:
                DrawPair(g, layout, palette.Disabled, filled: false);
                break;
        }

        return bitmap;
    }

    // The gauge while its story is told, or while it leaves: the mark as the state draws it, the track, the arc the frame gives, and
    // in the number's place the number (or, with the AirPods on another device, the phone) at the frame's strength and the glyph at
    // its own. The bolt goes with the number.
    private static void DrawStory(Graphics g, GaugeContent content, GaugePalette palette, GaugeLayout layout, string fontFamily, GaugeStoryFrame frame)
    {
        Color ink = content.Tertiary ? palette.Tertiary : palette.Ink;
        switch (content.Mode)
        {
            case GaugeMode.Reading:
                DrawPair(g, layout, palette.Ink, filled: true);
                break;
            case GaugeMode.CaseAway:
                DrawCaseMark(g, layout, ink);
                break;
            case GaugeMode.OnOtherDevice:
                DrawPair(g, layout, palette.Tertiary, filled: false);
                break;
            case GaugeMode.MarkOnly:
                DrawPair(g, layout, palette.Tertiary, filled: true);
                break;
            case GaugeMode.NotOnThisPc:
            default:
                DrawPair(g, layout, palette.Disabled, filled: false);
                break;
        }

        // Leaving, the ring is drawn only for the states that have one at rest, so a ring that is not theirs goes as the glyph does.
        bool leaving = frame.Phase == GaugeStoryKind.None;
        if (!leaving || content.Mode is GaugeMode.Reading or GaugeMode.CaseAway or GaugeMode.MarkOnly)
        {
            DrawRingTrack(g, layout, palette);
        }

        Color arc = frame.Phase == GaugeStoryKind.Spinning ? palette.Accent : content.Tertiary ? palette.Tertiary : content.Low ? palette.Caution : palette.Accent;
        DrawSweep(g, layout, arc, frame.Start, frame.Extent);

        if (frame.NumberOpacity > 0)
        {
            if (content.Mode is GaugeMode.Reading or GaugeMode.CaseAway)
            {
                DrawNumber(g, layout, content, palette, fontFamily, frame.NumberOpacity);
                if (content.Charging)
                {
                    DrawBolt(g, layout, Fade(ink, frame.NumberOpacity));
                }
            }
            else if (content.Mode == GaugeMode.OnOtherDevice)
            {
                DrawPhone(g, layout, Fade(palette.Tertiary, frame.NumberOpacity));
            }
        }

        if (frame.Glyph != GaugeSlotGlyph.None && frame.GlyphOpacity > 0)
        {
            bool tick = frame.Glyph == GaugeSlotGlyph.Tick;
            DrawStoryGlyph(g, layout, tick, Fade(tick ? palette.Tick : palette.Caution, frame.GlyphOpacity));
        }
    }

    // An arc from start for extent degrees (GDI+ angles) with round caps; the whole circle from 360.
    private static void DrawSweep(Graphics g, GaugeLayout layout, Color colour, double start, double extent)
    {
        if (extent <= 0)
        {
            return;
        }

        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        var square = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
        using var pen = new Pen(colour, layout.RingStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (extent >= 360)
        {
            g.DrawEllipse(pen, square);
        }
        else
        {
            g.DrawArc(pen, square, (float)start, (float)extent);
        }
    }

    // The tick (the check icon) or the cross (the close icon), 12 px at 100%, centred in the number's place.
    private static void DrawStoryGlyph(Graphics g, GaugeLayout layout, bool tick, Color colour) =>
        IconPainter.Draw(g, tick ? Icon.Check : Icon.Close, IconStyle.Line, layout.NumberSlot, layout.Bolt.Height, colour);

    // The colour at a fraction of its own alpha.
    private static Color Fade(Color colour, double opacity) =>
        Color.FromArgb((int)Math.Round(colour.A * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero), colour);

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
            IconPainter.Draw(g, Icon.Lightning, IconStyle.Line, layout.ChargingSlot, layout.Bolt.Height, placeholder);
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
    // A fill part way between the two (fading in or out over FluentMotion's hover time) is the two colours mixed, and never
    // falls under the idle fill's alpha of 1.
    private static void FillBackground(Graphics g, GaugePalette palette, GaugeLayout layout, double hover)
    {
        using GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, layout.Width, layout.Height), layout.CornerRadius);
        using var brush = new SolidBrush(Mix(palette.IdleFill, palette.HoverFill, hover));
        g.FillPath(brush, path);
    }

    // The box the pair and the case are drawn in: 14 px at 100%, centred in the ring box.
    internal static int MarkBoxPx(GaugeLayout layout) => GaugeLayout.Round(MarkAt96, layout.Dpi);

    internal static Rectangle MarkBox(GaugeLayout layout)
    {
        int px = MarkBoxPx(layout);
        return new Rectangle(IconPainter.BoxOrigin(layout.Mark, px), new Size(px, px));
    }

    // The earbud pair (the AirPods icon), filled while the AirPods are on this PC and line otherwise, in the ring box.
    private static void DrawPair(Graphics g, GaugeLayout layout, Color ink, bool filled) =>
        IconPainter.Draw(g, Icon.AirPods, filled ? IconStyle.Filled : IconStyle.Line, layout.Mark, MarkBoxPx(layout), ink);

    // The track is the whole circle; the arc runs from 12 o'clock clockwise for the battery's share of it, with round caps.
    // GDI+ measures angles clockwise from 3 o'clock, so 12 o'clock is -90.
    private static void DrawRingTrack(Graphics g, GaugeLayout layout, GaugePalette palette)
    {
        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        using var track = new Pen(palette.Track, layout.RingStroke);
        g.DrawEllipse(track, new RectangleF(c.X - r, c.Y - r, r * 2, r * 2));
    }

    internal static Color Mix(Color from, Color to, double share)
    {
        double p = Math.Clamp(share, 0, 1);
        if (p <= 0)
        {
            return from;
        }

        if (p >= 1)
        {
            return to;
        }

        static int Channel(int a, int b, double p) => (int)Math.Round(a + ((b - a) * p), MidpointRounding.AwayFromZero);
        return Color.FromArgb(Channel(from.A, to.A, p), Channel(from.R, to.R, p), Channel(from.G, to.G, p), Channel(from.B, to.B, p));
    }

    private static void DrawRing(Graphics g, GaugeLayout layout, GaugePalette palette, GaugeContent content, double percent)
    {
        DrawRingTrack(g, layout, palette);
        PointF c = RingCentre(layout);
        float r = layout.RingRadius;
        var square = new RectangleF(c.X - r, c.Y - r, r * 2, r * 2);
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
            g.DrawArc(fill, square, -90f, (float)(360 * percent / 100));
        }
    }

    // The number is its digits and nothing before them: a plain figure in the broadcast's steps, never an approximate one.
    // opacity is the number's own strength while it fades in or out beside the story's glyph; whole at rest.
    private static void DrawNumber(Graphics g, GaugeLayout layout, GaugeContent content, GaugePalette palette, string fontFamily, double opacity = 1)
    {
        if (content.Percent is not { } percent)
        {
            return;
        }

        string text = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int typePixels = layout.TypePixels;
        float cell = DigitCell(fontFamily, typePixels);
        using var font = new Font(fontFamily, typePixels, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Fade(content.Tertiary ? palette.Tertiary : content.Low ? palette.Caution : palette.Ink, opacity));
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
        float x = layout.NumberSlot.X + ((layout.NumberSlot.Width - (cell * text.Length)) / 2f);
        foreach (char digit in text)
        {
            g.DrawString(digit.ToString(), font, brush, new RectangleF(x, layout.NumberSlot.Y, cell, layout.NumberSlot.Height), format);
            x += cell;
        }
    }

    // The case, in place of the earbud pair and the size of it: the AirPods case icon, line, in the ink it is given, never the accent.
    private static void DrawCaseMark(Graphics g, GaugeLayout layout, Color ink) =>
        IconPainter.Draw(g, Icon.AirPodsCase, IconStyle.Line, layout.Mark, MarkBoxPx(layout), ink);

    // The bolt (the lightning icon, filled) at 12 px at 100%, centred in its slot.
    private static void DrawBolt(Graphics g, GaugeLayout layout, Color ink) =>
        IconPainter.Draw(g, Icon.Lightning, IconStyle.Filled, layout.ChargingSlot, layout.Bolt.Height, ink);

    // The phone (the cellphone icon, line), centred in the number slot.
    private static void DrawPhone(Graphics g, GaugeLayout layout, Color ink) =>
        IconPainter.Draw(g, Icon.Cellphone, IconStyle.Line, layout.NumberSlot, layout.PhoneSize, ink);

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
