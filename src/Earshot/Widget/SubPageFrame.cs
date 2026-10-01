using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// The frame every sub-page of the widget card sits in: a 48 px header (a 32 by 32 back button 8 px from the
// left, the title, a step counter 16 px from the right), a body the page fills, and a 64 px footer holding one
// button across the whole width or two split 50/50 with an 8 px gap. Pure layout, then a painter for the two
// parts the frame owns (header and footer); the body is the page's own. The set-up steps use it now; the
// settings and updates pages sit in it the same way.
internal static class SubPageFrame
{
    public const int WidthAt96 = 360;
    public const int HeaderHeightAt96 = 48;
    public const int BackSizeAt96 = 32;
    public const int BackLeftAt96 = 8;
    public const int TitleGapAt96 = 4;
    public const int StepRightAt96 = 16;
    public const int FooterHeightAt96 = 64;
    public const int FooterPaddingAt96 = 16;
    public const int ButtonHeightAt96 = 32;
    public const int ButtonGapAt96 = 8;
    public const int StepHeightAt96 = 16;
    public const int StepWidthAt96 = 40;

    // Everything in client pixels. Buttons is empty when the page has no footer buttons (the footer is then
    // not drawn and Height stops at the body).
    internal sealed record FrameLayout(
        int Width,
        int Height,
        Rectangle Header,
        Rectangle Back,
        Rectangle Title,
        Rectangle Step,
        Rectangle Body,
        Rectangle Footer,
        IReadOnlyList<Rectangle> Buttons);

    // bodyHeight is the page's own content height at dpi, in pixels; buttonCount is 0, 1 or 2.
    public static FrameLayout Compute(int dpi, int bodyHeight, int buttonCount, double textScale = 1.0)
    {
        if (buttonCount is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(buttonCount), "A footer holds no button, one or two.");
        }

        int width = CardPlacement.Scale(WidthAt96, dpi);
        int headerHeight = TextFit.Fit(HeaderHeightAt96, TypeRole.BodyStrong, 28, dpi, textScale);
        int backSize = CardPlacement.Scale(BackSizeAt96, dpi);
        int backLeft = CardPlacement.Scale(BackLeftAt96, dpi);
        int titleGap = CardPlacement.Scale(TitleGapAt96, dpi);
        int stepRight = CardPlacement.Scale(StepRightAt96, dpi);
        int stepHeight = TextFit.Grow(StepHeightAt96, dpi, textScale);
        int stepWidth = CardPlacement.Scale(StepWidthAt96, dpi);
        int footerPad = CardPlacement.Scale(FooterPaddingAt96, dpi);
        int buttonHeight = TextFit.Fit(ButtonHeightAt96, TypeRole.Body, 12, dpi, textScale);
        int footerHeight = buttonCount == 0 ? 0 : buttonHeight + (2 * footerPad);
        int buttonGap = CardPlacement.Scale(ButtonGapAt96, dpi);

        var header = new Rectangle(0, 0, width, headerHeight);
        var back = new Rectangle(backLeft, (headerHeight - backSize) / 2, backSize, backSize);
        var step = new Rectangle(width - stepRight - stepWidth, (headerHeight - stepHeight) / 2, stepWidth, stepHeight);
        int titleX = back.Right + titleGap;
        var title = new Rectangle(titleX, 0, Math.Max(1, step.X - titleX), headerHeight);
        var body = new Rectangle(0, headerHeight, width, bodyHeight);
        var footer = new Rectangle(0, body.Bottom, width, footerHeight);

        var buttons = new List<Rectangle>(buttonCount);
        int inner = width - (2 * footerPad);
        int buttonY = footer.Y + footerPad;
        if (buttonCount == 1)
        {
            buttons.Add(new Rectangle(footerPad, buttonY, inner, buttonHeight));
        }
        else if (buttonCount == 2)
        {
            int half = (inner - buttonGap) / 2;
            buttons.Add(new Rectangle(footerPad, buttonY, half, buttonHeight));
            buttons.Add(new Rectangle(footerPad + half + buttonGap, buttonY, inner - half - buttonGap, buttonHeight));
        }

        return new FrameLayout(width, footer.Bottom, header, back, title, step, body, footer, buttons);
    }

    // The header: the back button's arrow (with a hover-style fill when it has the keyboard focus), the title
    // in semibold and the step counter in the caption size.
    public static void DrawHeader(
        Graphics g, FrameLayout layout, string title, string? step, CardColours colours, CardType type, int dpi, bool backFocused)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(colours);

        if (backFocused)
        {
            CardPaint.Focus(g, layout.Back, CardPlacement.Scale(FocusVisual.ControlRadiusAt96, dpi), colours, dpi);
        }

        CardPaint.BackArrow(g, layout.Back, colours.Text, dpi);
        CardPaint.Text(g, title, layout.Title, type, CardPlacement.Scale(14, dpi), bold: true, colours.Text, StringAlignment.Near, StringAlignment.Center);
        if (step is not null)
        {
            CardPaint.Text(g, step, layout.Step, type, CardPlacement.Scale(12, dpi), bold: false, colours.TextSecondary, StringAlignment.Far, StringAlignment.Center);
        }
    }

    // The footer: a divider on top, the footer fill, and the buttons. focusedButton is the index of the button
    // that has the keyboard focus, or -1.
    public static void DrawFooter(
        Graphics g, FrameLayout layout, IReadOnlyList<(string Label, bool Primary)> buttons, CardColours colours, CardType type, int dpi, int focusedButton)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentNullException.ThrowIfNull(colours);

        if (layout.Buttons.Count == 0)
        {
            return;
        }

        using (var fill = new SolidBrush(colours.Footer))
        {
            g.FillRectangle(fill, layout.Footer);
        }

        using (var pen = new Pen(colours.Divider))
        {
            g.DrawLine(pen, layout.Footer.Left, layout.Footer.Top, layout.Footer.Right, layout.Footer.Top);
        }

        for (int i = 0; i < layout.Buttons.Count && i < buttons.Count; i++)
        {
            CardPaint.Button(g, layout.Buttons[i], buttons[i].Label, buttons[i].Primary, colours, type, dpi, focused: i == focusedButton);
        }
    }
}

// The small shapes and text the card's own views are drawn from: GDI+ only (never TextRenderer, whose alpha 0
// text vanishes on the translucent backdrop), pixel sizes scaled from the design's 96 DPI values.
internal enum GlyphKind { Minus, Plus, Cross }

internal static partial class CardPaint
{
    public static void Text(
        Graphics g, string text, Rectangle bounds, CardType type, int pixelSize, bool bold, Color colour, StringAlignment horizontal, StringAlignment vertical)
    {
        using Font font = type.Font(pixelSize, bold);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat { Alignment = horizontal, LineAlignment = vertical, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    // Text that wraps inside bounds, top-aligned.
    public static void Wrapped(Graphics g, string text, Rectangle bounds, CardType type, int pixelSize, bool bold, Color colour)
    {
        using Font font = type.Font(pixelSize, bold);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
        g.DrawString(text, font, brush, bounds, format);
    }

    // How many lines text takes when wrapped to width, at the given size; at least 1.
    public static int Lines(Graphics g, string text, int width, CardType type, int pixelSize, bool bold, int lineHeight)
    {
        using Font font = type.Font(pixelSize, bold);
        SizeF size = g.MeasureString(text, font, Math.Max(1, width), StringFormat.GenericTypographic);
        return Math.Max(1, (int)Math.Ceiling(size.Height / Math.Max(1, lineHeight) - 0.01));
    }

    public static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float r = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        if (r <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Button(Graphics g, Rectangle rect, string label, bool primary, CardColours colours, CardType type, int dpi, bool focused)
    {
        int radius = Scale(4, dpi);
        using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        using (var fill = new SolidBrush(primary ? colours.Accent : colours.ControlFill))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(primary ? colours.Accent : colours.ControlStroke, 1f))
        {
            g.DrawPath(pen, path);
        }

        Text(g, label, rect, type, Scale(14, dpi), bold: false, primary ? colours.OnAccent : colours.Text, StringAlignment.Center, StringAlignment.Center);
        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // The 24 px button of the update line: the standard control fill and stroke, the caption size.
    public static void SmallButton(Graphics g, Rectangle rect, string label, CardColours colours, CardType type, int dpi, bool focused)
    {
        ArgumentNullException.ThrowIfNull(colours);
        int radius = Scale(4, dpi);
        using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        using (var fill = new SolidBrush(colours.ControlFill))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(colours.ControlStroke, 1f))
        {
            g.DrawPath(pen, path);
        }

        Text(g, label, rect, type, Scale(12, dpi), bold: false, colours.Text, StringAlignment.Center, StringAlignment.Center);
        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // The Windows 11 focus visual round a control, in the theme's own colours. Callers draw it only for keyboard
    // focus (KeyboardFocusCue); radius is the control's own corner radius.
    public static void Focus(Graphics g, Rectangle control, int radius, CardColours colours, int dpi)
    {
        ArgumentNullException.ThrowIfNull(colours);
        FocusVisual.Draw(g, control, radius, dpi, colours.Focus);
    }

    // The arrow of the back button, a 16 px line icon with a 1.2 px stroke and round caps.
    public static void BackArrow(Graphics g, Rectangle button, Color colour, int dpi)
    {
        float s = dpi / 96f;
        float left = button.X + ((button.Width - (16 * s)) / 2f);
        float top = button.Y + ((button.Height - (16 * s)) / 2f);
        PointF P(float x, float y) => new(left + (x * s), top + (y * s));
        using var pen = RoundPen(colour, 1.2f * s);
        g.DrawLine(pen, P(13.5f, 8f), P(2.5f, 8f));
        g.DrawLines(pen, new[] { P(7f, 3.5f), P(2.5f, 8f), P(7f, 12.5f) });
    }

    // A 12 px chevron, pointing up or down, centred in bounds.
    public static void Chevron(Graphics g, Rectangle bounds, bool up, Color colour, int dpi)
    {
        float s = dpi / 96f;
        float left = bounds.X + ((bounds.Width - (12 * s)) / 2f);
        float top = bounds.Y + ((bounds.Height - (12 * s)) / 2f);
        PointF P(float x, float y) => new(left + (x * s), top + (y * s));
        using var pen = RoundPen(colour, 1.2f * s);
        g.DrawLines(pen, up ? new[] { P(2, 8), P(6, 4), P(10, 8) } : new[] { P(2, 4), P(6, 8), P(10, 4) });
    }

    // The 40 by 20 toggle: an accent track with a knob at the right when on, an outline with a knob at the left
    // when off.
    public static void Toggle(Graphics g, Rectangle bounds, bool on, CardColours colours, int dpi)
    {
        float s = dpi / 96f;
        using GraphicsPath track = RoundedRectangle(new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1), bounds.Height / 2f);
        if (on)
        {
            using var fill = new SolidBrush(colours.Accent);
            g.FillPath(fill, track);
        }

        using (var pen = new Pen(on ? colours.Accent : colours.TextSecondary, 1f))
        {
            g.DrawPath(pen, track);
        }

        float knob = 12 * s;
        float knobX = bounds.X + ((on ? 23 : 3) * s);
        float knobY = bounds.Y + ((bounds.Height - knob) / 2f);
        using var knobBrush = new SolidBrush(on ? colours.OnAccent : colours.TextSecondary);
        g.FillEllipse(knobBrush, knobX, knobY, knob, knob);
    }

    // A 20 px status icon: a check in a circle, or a caution triangle, in the given colour, 1.4 px line.
    public static void CheckIcon(Graphics g, Rectangle bounds, Color colour, int dpi)
    {
        float s = dpi / 96f;
        PointF P(float x, float y) => new(bounds.X + (x * s), bounds.Y + (y * s));
        using var pen = RoundPen(colour, 1.4f * s);
        g.DrawEllipse(pen, bounds.X + (1.7f * s), bounds.Y + (1.7f * s), 16.6f * s, 16.6f * s);
        g.DrawLines(pen, new[] { P(6.2f, 10.2f), P(8.8f, 12.8f), P(13.8f, 7.6f) });
    }

    public static void CautionIcon(Graphics g, Rectangle bounds, Color colour, int dpi)
    {
        float s = dpi / 96f;
        PointF P(float x, float y) => new(bounds.X + (x * s), bounds.Y + (y * s));
        using var pen = RoundPen(colour, 1.4f * s);
        g.DrawLines(pen, new[] { P(10f, 2.2f), P(18.4f, 17f), P(1.6f, 17f), P(10f, 2.2f) });
        g.DrawLine(pen, P(10f, 7.6f), P(10f, 11.6f));
        g.DrawLine(pen, P(10f, 14.2f), P(10f, 14.3f));
    }

    // The 20 px spinner: an accent arc, 2 px, that turns once every ten frames.
    public static void Spinner(Graphics g, Rectangle bounds, Color accent, int frame, int dpi)
    {
        float s = dpi / 96f;
        using var pen = RoundPen(accent, 2f * s);
        float inset = 1.5f * s;
        var arc = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - (2 * inset), bounds.Height - (2 * inset));
        float start = (((frame % 10) + 10) % 10) * 36f - 90f;
        g.DrawArc(pen, arc, start, 110f);
    }

    // The 16 px gear of the title row: a small circle inside a larger one, with eight short spokes.
    public static void Gear(Graphics g, Rectangle button, Color colour, int dpi)
    {
        float s = dpi / 96f;
        float left = button.X + ((button.Width - (16 * s)) / 2f);
        float top = button.Y + ((button.Height - (16 * s)) / 2f);
        PointF P(float x, float y) => new(left + (x * s), top + (y * s));
        using var pen = RoundPen(colour, 1.2f * s);
        g.DrawEllipse(pen, left + (5.8f * s), top + (5.8f * s), 4.4f * s, 4.4f * s);
        g.DrawEllipse(pen, left + (3.2f * s), top + (3.2f * s), 9.6f * s, 9.6f * s);
        (float X1, float Y1, float X2, float Y2)[] spokes =
        [
            (8f, 1.2f, 8f, 3.2f), (8f, 12.8f, 8f, 14.8f), (1.2f, 8f, 3.2f, 8f), (12.8f, 8f, 14.8f, 8f),
            (3.2f, 3.2f, 4.6f, 4.6f), (11.4f, 11.4f, 12.8f, 12.8f), (12.8f, 3.2f, 11.4f, 4.6f), (4.6f, 11.4f, 3.2f, 12.8f),
        ];
        foreach ((float x1, float y1, float x2, float y2) in spokes)
        {
            g.DrawLine(pen, P(x1, y1), P(x2, y2));
        }
    }

    // The 20 px arrow into a tray that stands for an update: the download and the available icons.
    public static void DownIcon(Graphics g, Rectangle bounds, Color colour, int dpi)
    {
        float s = dpi / 96f;
        PointF P(float x, float y) => new(bounds.X + (x * s), bounds.Y + (y * s));
        using var pen = RoundPen(colour, 1.4f * s);
        g.DrawLine(pen, P(10f, 2.5f), P(10f, 13f));
        g.DrawLines(pen, new[] { P(5.5f, 8.5f), P(10f, 13f), P(14.5f, 8.5f) });
        g.DrawLine(pen, P(3f, 17f), P(17f, 17f));
    }

    // The 20 px shield of the step where Windows asks for its administrator approval.
    public static void ShieldIcon(Graphics g, Rectangle bounds, Color colour, int dpi)
    {
        float s = dpi / 96f;
        PointF P(float x, float y) => new(bounds.X + (x * s), bounds.Y + (y * s));
        using var pen = RoundPen(colour, 1.4f * s);
        using var path = new GraphicsPath();
        path.AddLine(P(10f, 1.8f), P(16.8f, 4.4f));
        path.AddLine(P(16.8f, 4.4f), P(16.8f, 9.4f));
        path.AddBezier(P(16.8f, 9.4f), P(16.8f, 13.6f), P(13.9f, 16.8f), P(10f, 18.2f));
        path.AddBezier(P(10f, 18.2f), P(6.1f, 16.8f), P(3.2f, 13.6f), P(3.2f, 9.4f));
        path.AddLine(P(3.2f, 9.4f), P(3.2f, 4.4f));
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    // A 4 px bar with full radius: the track, and the accent fill over the first percent of it.
    public static void ProgressBar(Graphics g, Rectangle bounds, int percent, CardColours colours)
    {
        ArgumentNullException.ThrowIfNull(colours);
        using (GraphicsPath track = RoundedRectangle(bounds, bounds.Height / 2f))
        using (var brush = new SolidBrush(colours.Track))
        {
            g.FillPath(brush, track);
        }

        int filled = (int)Math.Round(bounds.Width * Math.Clamp(percent, 0, 100) / 100.0);
        if (filled > 0)
        {
            var fillRect = new Rectangle(bounds.X, bounds.Y, filled, bounds.Height);
            using GraphicsPath fill = RoundedRectangle(fillRect, bounds.Height / 2f);
            using var brush = new SolidBrush(colours.Accent);
            g.FillPath(brush, fill);
        }
    }

    // A 10 px minus, plus or cross centred in bounds, 1.2 px round line.
    public static void Glyph10(Graphics g, Rectangle bounds, GlyphKind kind, Color colour, int dpi)
    {
        float s = dpi / 96f;
        float left = bounds.X + ((bounds.Width - (10 * s)) / 2f);
        float top = bounds.Y + ((bounds.Height - (10 * s)) / 2f);
        PointF P(float x, float y) => new(left + (x * s), top + (y * s));
        using var pen = RoundPen(colour, 1.2f * s);
        switch (kind)
        {
            case GlyphKind.Minus:
                g.DrawLine(pen, P(1, 5), P(9, 5));
                break;
            case GlyphKind.Plus:
                g.DrawLine(pen, P(1, 5), P(9, 5));
                g.DrawLine(pen, P(5, 1), P(5, 9));
                break;
            default:
                g.DrawLine(pen, P(1.5f, 1.5f), P(8.5f, 8.5f));
                g.DrawLine(pen, P(8.5f, 1.5f), P(1.5f, 8.5f));
                break;
        }
    }

    // One of the settings page's two-choice buttons: the accent when it is the choice, the standard control when not.
    public static void Segment(Graphics g, Rectangle rect, string label, bool selected, CardColours colours, CardType type, int dpi, bool focused)
    {
        ArgumentNullException.ThrowIfNull(colours);
        int radius = Scale(4, dpi);
        using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        using (var fill = new SolidBrush(selected ? colours.Accent : colours.ControlFill))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(selected ? colours.Accent : colours.ControlStroke, 1f))
        {
            g.DrawPath(pen, path);
        }

        Text(g, label, rect, type, Scale(12, dpi), bold: false, selected ? colours.OnAccent : colours.Text, StringAlignment.Center, StringAlignment.Center);
        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // A 28 by 28 button holding a minus, a plus or a cross. Drawn at 35% when it cannot be used.
    public static void IconButton(Graphics g, Rectangle rect, GlyphKind kind, bool enabled, CardColours colours, int dpi, bool focused, bool bordered = true)
    {
        ArgumentNullException.ThrowIfNull(colours);
        int radius = Scale(4, dpi);
        int alpha = enabled ? 255 : 89;
        if (bordered)
        {
            using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
            using (var fill = new SolidBrush(Color.FromArgb((colours.ControlFill.A * alpha) / 255, colours.ControlFill)))
            {
                g.FillPath(fill, path);
            }

            using var pen = new Pen(Color.FromArgb((colours.ControlStroke.A * alpha) / 255, colours.ControlStroke), 1f);
            g.DrawPath(pen, path);
        }

        Glyph10(g, rect, kind, Color.FromArgb(alpha, bordered ? colours.Text : colours.TextSecondary), dpi);
        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // A one pixel line across, in the divider colour.
    public static void Divider(Graphics g, int x1, int x2, int y, CardColours colours)
    {
        ArgumentNullException.ThrowIfNull(colours);
        using var pen = new Pen(colours.Divider);
        g.DrawLine(pen, x1, y, x2, y);
    }

    private static Pen RoundPen(Color colour, float width) =>
        new(colour, Math.Max(1f, width)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static int Scale(int valueAt96, int dpi) => CardPlacement.Scale(valueAt96, dpi);
}
