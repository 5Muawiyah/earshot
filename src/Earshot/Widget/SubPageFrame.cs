using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using Earshot.Icons;
using IconId = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Widget;

// The frame every sub-page of the widget card sits in: a 48 px header (a back button, an icon button 16t + 16 square, 8 px from the
// left, the title 8 px after it, a step counter 12 px from the right), a body the page fills, and a 64 px footer holding one
// button across the whole width or two split 50/50 with an 8 px gap. Pure layout, then a painter for the two
// parts the frame owns (header and footer); the body is the page's own. The set-up steps use it now; the
// settings and updates pages sit in it the same way.
internal static class SubPageFrame
{
    public const int WidthAt96 = 360;
    public const int HeaderHeightAt96 = 48;
    public const int BackLeftAt96 = 8;
    public const int TitleGapAt96 = 8;
    public const int StepRightAt96 = 12;
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
        int backSize = WidgetCardLayout.IconButtonSize(dpi, textScale);
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

        CardPaint.DrawIcon(g, IconId.Back, filled: false, layout.Back, colours.Text, dpi);

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
    // The edge of the box an icon is drawn in, in epx at 96 dpi: 16 for most icons, 12 for a chevron, the clock, the bolt and the
    // external link, 10 for the close of a card nobody asked for, the minus and the plus. The box grows with the display scale
    // and, where the caller gives a text size, with that.
    public const int IconBoxAt96 = 16;
    public const int ChevronBoxAt96 = 12;
    public const int MarkBoxAt96 = 10;

    // An icon in a square box of sizeAt96 epx centred in bounds, in ink (its alpha is the icon's opacity), line or filled. The
    // site recorded in the icon log is the line that calls this.
    public static void DrawIcon(
        Graphics g,
        IconId icon,
        bool filled,
        Rectangle bounds,
        Color ink,
        int dpi,
        int sizeAt96 = IconBoxAt96,
        double textScale = 1.0,
        IconRule rule = IconRule.A,
        int rotationStep = 0,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        IconPainter.Draw(g, icon, filled ? IconStyle.Filled : IconStyle.Line, bounds, TextFit.Grow(sizeAt96, dpi, textScale), ink, rule, rotationStep, callerFile, callerLine);

    // An icon in the largest square that fits bounds, centred in it: the sub-page status icons, the card's columns and the updates tile.
    public static void DrawIconFitted(
        Graphics g,
        IconId icon,
        bool filled,
        Rectangle bounds,
        Color ink,
        IconRule rule = IconRule.A,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        IconPainter.Draw(g, icon, filled ? IconStyle.Filled : IconStyle.Line, bounds, Math.Min(bounds.Width, bounds.Height), ink, rule, 0, callerFile, callerLine);

    // What the card draws its words with, and what it measures them with: one format and one text hint, so a rectangle sized from a
    // measurement holds what is drawn in it. (Measuring with StringFormat.GenericTypographic, which does not pad, and drawing with the
    // default format, which does, let the layout size a label to a width the drawing then wrapped inside and cut short.)
    internal const TextRenderingHint CardTextHint = TextRenderingHint.AntiAliasGridFit;

    // Every piece of text a card draws, with the rectangle it was given and how it was drawn, for a test that checks each one fits.
    // Null outside such a test: nothing is kept. Per thread, so a test on its own thread sees only its own card.
    [ThreadStatic]
    internal static List<DrawnText>? DrawnTextLog;

    // Every figure the history chart draws one character at a time (DrawTabular), with the rectangle it was given and the room its
    // characters take together. Null outside a test, and per thread, like DrawnTextLog.
    [ThreadStatic]
    internal static List<DrawnTabular>? DrawnTabularLog;

    internal static void NoteTabular(string text, Rectangle bounds, float left, float width, float height) =>
        DrawnTabularLog?.Add(new DrawnTabular(text, bounds, left, width, height));

    // requestedPixels is the size the text was asked for: what it is drawn at is smaller when it was fitted to a narrow rectangle.
    internal static void NoteDrawn(string text, Rectangle bounds, Font font, StringFormat format, int requestedPixels) =>
        DrawnTextLog?.Add(new DrawnText(
            text, bounds, font.FontFamily.Name, font.Size, font.Style, font.Unit, format.FormatFlags, format.Alignment, format.LineAlignment, format.Trimming, requestedPixels));

    // A line of text placed in bounds, cut with an ellipsis at a character when it is too long.
    internal static StringFormat SingleLineFormat(StringAlignment horizontal, StringAlignment vertical) =>
        new() { Alignment = horizontal, LineAlignment = vertical, Trimming = StringTrimming.EllipsisCharacter };

    // Text that wraps inside bounds, top-aligned, cut with an ellipsis at a word when it runs out of lines.
    internal static StringFormat WrappedFormat() =>
        new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };

    // A single line of text in bounds. A line too long for the width is drawn smaller until it fits (a value, a time, a chord, a button's
    // word, where there is nothing to wrap), never cut short with an ellipsis: the type size follows the person's text size, and where
    // that does not leave room it gives way a little rather than lose the end of a number. Words that can take more lines are
    // drawn with Wrapped in a rectangle the layout sized for them.
    public static void Text(
        Graphics g, string text, Rectangle bounds, CardType type, int pixelSize, bool bold, Color colour, StringAlignment horizontal, StringAlignment vertical)
    {
        using StringFormat format = SingleLineFormat(horizontal, vertical);
        using Font font = FittingFont(g, text, bounds, type, pixelSize, bold, format);
        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, bounds, format);
        NoteDrawn(text, bounds, font, format, type.DrawnPixels(pixelSize));
    }

    // The font at the asked size, or the largest smaller one (down to 55% of it) with which the whole text fits the rectangle.
    internal static Font FittingFont(Graphics g, string text, Rectangle bounds, CardType type, int pixelSize, bool bold, StringFormat format)
    {
        int size = pixelSize;
        int floor = Math.Max(6, pixelSize * 55 / 100);
        while (true)
        {
            Font font = type.Font(size, bold);
            if (size <= floor || FitsWhole(g, text, bounds, font, format))
            {
                return font;
            }

            font.Dispose();
            size--;
        }
    }

    // Whether GDI+ puts every character of text inside the rectangle in this font and format, at the card's text hint.
    internal static bool FitsWhole(Graphics g, string text, Rectangle bounds, Font font, StringFormat format)
    {
        if (text.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return true;
        }

        TextRenderingHint before = g.TextRenderingHint;
        g.TextRenderingHint = CardTextHint;
        try
        {
            _ = g.MeasureString(text, font, new SizeF(bounds.Width, bounds.Height), format, out int fitted, out _);
            return fitted >= text.Length;
        }
        finally
        {
            g.TextRenderingHint = before;
        }
    }

    // Text that wraps inside bounds, top-aligned.
    public static void Wrapped(Graphics g, string text, Rectangle bounds, CardType type, int pixelSize, bool bold, Color colour)
    {
        using Font font = type.Font(pixelSize, bold);
        using var brush = new SolidBrush(colour);
        using StringFormat format = WrappedFormat();
        g.DrawString(text, font, brush, bounds, format);
        NoteDrawn(text, bounds, font, format, type.DrawnPixels(pixelSize));
    }

    // How many lines text takes when wrapped to width, at the given size, measured as Wrapped draws it; at least 1.
    public static int Lines(Graphics g, string text, int width, CardType type, int pixelSize, bool bold)
    {
        TextRenderingHint before = g.TextRenderingHint;
        g.TextRenderingHint = CardTextHint;
        try
        {
            using Font font = type.Font(pixelSize, bold);
            using StringFormat format = WrappedFormat();

            // The lines the format wraps it to, counted by GDI+ itself with room for as many as it needs. (Dividing the measured height
            // by a line height does not do: the drawing format's height has leading the line height of the layout does not, which
            // counted a one-line label as two.)
            _ = g.MeasureString(text, font, new SizeF(Math.Max(1, width), 100_000f), format, out _, out int lines);
            return Math.Max(1, lines);
        }
        finally
        {
            g.TextRenderingHint = before;
        }
    }

    // How wide a line of text is as Text draws it, with the padding the drawing format adds.
    public static int LineWidth(Graphics g, string text, CardType type, int pixelSize, bool bold)
    {
        TextRenderingHint before = g.TextRenderingHint;
        g.TextRenderingHint = CardTextHint;
        try
        {
            using Font font = type.Font(pixelSize, bold);
            using StringFormat format = SingleLineFormat(StringAlignment.Near, StringAlignment.Near);
            return (int)Math.Ceiling(g.MeasureString(text, font, int.MaxValue, format).Width);
        }
        finally
        {
            g.TextRenderingHint = before;
        }
    }

    // A settings-style surface: the fill over the whole box and the 1 px stroke inside its edge, the fill showing under the stroke as a
    // CSS background does under its border (the design's rows are `background` and `border: 1px solid`). The stroke is drawn on the
    // half pixel inside the edge (the card draws with PixelOffsetMode.Half), at the radius less half the stroke.
    public static void Surface(Graphics g, Rectangle rect, int radius, Color fill, Color stroke)
    {
        using (GraphicsPath body = RoundedRectangle(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, body);
        }

        using GraphicsPath edge = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), Math.Max(0f, radius - 0.5f));
        using var pen = new Pen(stroke, 1f);
        g.DrawPath(pen, edge);
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

        if (!primary)
        {
            // The standard control's bottom edge is a little stronger than its sides.
            using var bottom = new Pen(colours.ControlStrokeBottom, 1f);
            g.DrawLine(bottom, rect.Left + radius, rect.Bottom - 1, rect.Right - radius - 1, rect.Bottom - 1);
        }

        Text(g, label, rect, type, Scale(14, dpi), bold: false, primary ? colours.OnAccent : colours.Text, StringAlignment.Center, StringAlignment.Center);
        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // A standard button: the control fill and stroke, the Body size (14) for the words, 4 px corners. trailingGlyph, when given, is a
    // 12 px glyph after the words (the Open button's external link).
    public static void SmallButton(Graphics g, Rectangle rect, string label, CardColours colours, CardType type, int dpi, bool focused, IconId? trailingGlyph = null)
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

        // The standard control's bottom edge is a little stronger than its sides.
        using (var bottom = new Pen(colours.ControlStrokeBottom, 1f))
        {
            g.DrawLine(bottom, rect.Left + radius, rect.Bottom - 1, rect.Right - radius - 1, rect.Bottom - 1);
        }

        if (trailingGlyph is not { } trailing)
        {
            Text(g, label, rect, type, Scale(14, dpi), bold: false, colours.Text, StringAlignment.Center, StringAlignment.Center);
        }
        else
        {
            int glyph = Scale(ChevronBoxAt96, dpi);
            int pad = Scale(12, dpi);
            int gap = Scale(8, dpi);
            Text(g, label, new Rectangle(rect.X + pad, rect.Y, Math.Max(1, rect.Width - (2 * pad) - glyph - gap), rect.Height), type, Scale(14, dpi), bold: false, colours.Text, StringAlignment.Near, StringAlignment.Center);
            DrawIcon(g, trailing, filled: false, new Rectangle(rect.Right - pad - glyph, rect.Y + ((rect.Height - glyph) / 2), glyph, glyph), colours.Text, dpi, ChevronBoxAt96);
        }

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

    // The 40 by 20 toggle: an accent track with a knob at the right when on, an outline with a knob at the left
    // when off.
    public static void Toggle(Graphics g, Rectangle bounds, bool on, CardColours colours, int dpi) => Toggle(g, bounds, on, colours, dpi, on ? 1 : 0);

    // knob: where the knob is, 0 at rest off (x 3) to 1 at rest on (x 23), for a knob that slides (FluentMotion's toggle). The
    // track and the knob's colour are the new value's from the first frame (design choice): only the knob moves.
    public static void Toggle(Graphics g, Rectangle bounds, bool on, CardColours colours, int dpi, double knobAt)
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
        float knobX = bounds.X + ((3 + (20 * (float)Math.Clamp(knobAt, 0, 1))) * s);
        float knobY = bounds.Y + ((bounds.Height - knob) / 2f);
        using var knobBrush = new SolidBrush(on ? colours.OnAccent : colours.TextSecondary);
        g.FillEllipse(knobBrush, knobX, knobY, knob, knob);
    }

    // The 20 px status icons of a sub-page: a check in a circle, a caution triangle, an arrow into a tray for an update and a
    // shield for the step where Windows asks for its administrator approval. The whole 24 unit grid fills the box, so the art keeps
    // its two unit margin.
    public static void CheckIcon(Graphics g, Rectangle bounds, Color colour, int dpi) =>
        DrawIconFitted(g, IconId.CheckCircle, filled: false, bounds, colour, IconRule.B);

    public static void CautionIcon(Graphics g, Rectangle bounds, Color colour, int dpi) =>
        DrawIconFitted(g, IconId.Alert, filled: false, bounds, colour, IconRule.B);

    public static void DownIcon(Graphics g, Rectangle bounds, Color colour, int dpi) =>
        DrawIconFitted(g, IconId.Download, filled: false, bounds, colour, IconRule.B);

    public static void ShieldIcon(Graphics g, Rectangle bounds, Color colour, int dpi) =>
        DrawIconFitted(g, IconId.SafeShield2, filled: false, bounds, colour, IconRule.B);

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

    // A button holding a minus, a plus or a cross. Disabled, it has the disabled control fill and the disabled text colour.
    public static void IconButton(Graphics g, Rectangle rect, GlyphKind kind, bool enabled, CardColours colours, int dpi, bool focused, bool bordered = true)
    {
        ArgumentNullException.ThrowIfNull(colours);
        int radius = Scale(4, dpi);
        if (bordered)
        {
            using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
            using (var fill = new SolidBrush(enabled ? colours.ControlFill : colours.ControlFillDisabled))
            {
                g.FillPath(fill, path);
            }

            using var pen = new Pen(colours.ControlStroke, 1f);
            g.DrawPath(pen, path);
        }

        Color ink = !enabled ? colours.TextDisabled : bordered ? colours.Text : colours.TextSecondary;
        switch (kind)
        {
            case GlyphKind.Minus:
                DrawIcon(g, IconId.Minimize, filled: false, rect, ink, dpi, MarkBoxAt96);
                break;
            case GlyphKind.Plus:
                DrawIcon(g, IconId.Add, filled: false, rect, ink, dpi, MarkBoxAt96);
                break;
            default:
                DrawIcon(g, IconId.Close, filled: false, rect, ink, dpi);
                break;
        }

        if (focused)
        {
            Focus(g, rect, radius, colours, dpi);
        }
    }

    // A box of a list: the accent with a check mark when ticked, the standard control with an outline when not.
    public static void CheckBox(Graphics g, Rectangle rect, bool on, CardColours colours, int dpi, bool focused)
    {
        ArgumentNullException.ThrowIfNull(colours);
        int radius = Scale(4, dpi);
        using GraphicsPath path = RoundedRectangle(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        using (var fill = new SolidBrush(on ? colours.Accent : colours.ControlFill))
        {
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(on ? colours.Accent : colours.TextSecondary, 1f))
        {
            g.DrawPath(pen, path);
        }

        if (on)
        {
            DrawIcon(g, IconId.Check, filled: false, rect, colours.OnAccent, dpi);
        }

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

// One piece of text as it was drawn: what, where, in which font and with which format.
internal readonly record struct DrawnText(
    string Text, Rectangle Bounds, string FontFamily, float FontSize, FontStyle FontStyle, GraphicsUnit FontUnit,
    StringFormatFlags Flags, StringAlignment Horizontal, StringAlignment Vertical, StringTrimming Trimming, int RequestedPixels);

// One figure drawn a character at a time: its rectangle, where its first character starts and the width and height its characters take.
internal readonly record struct DrawnTabular(string Text, Rectangle Bounds, float Left, float Width, float Height);
