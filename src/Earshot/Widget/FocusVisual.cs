using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// The colours of the Windows 11 focus visual, from the design's tokens: an outer 2 px stroke in the primary text colour
// and an inner 1 px stroke, #FFFFFF on a light theme and black at 70% on a dark one. Under a high-contrast theme they
// are the theme's own window text and window colours.
// https://github.com/microsoft/microsoft-ui-xaml (controls/dev/CommonStyles/Common_themeresources_any.xaml:
// FocusStrokeColorOuter and FocusStrokeColorInner)
internal sealed record FocusPalette(Color OuterStroke, Color InnerStroke)
{
    public static FocusPalette For(bool dark, bool highContrast) => For(DesignTokens.For(dark, highContrast));

    public static FocusPalette For(DesignTokens tokens) => new(tokens.TextPrimary, tokens.FocusInner);
}

// How far the visual reaches beyond a control, in physical pixels at dpi: a one pixel gap, a one pixel inner
// stroke and a two pixel outer stroke, each scaled and never thinner than one pixel.
// https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback
internal readonly record struct FocusMetrics(int Gap, int InnerWidth, int OuterWidth)
{
    public int Reach => Gap + InnerWidth + OuterWidth;

    public static FocusMetrics For(int dpi) => new(
        Math.Max(1, CardPlacement.Scale(1, dpi)),
        Math.Max(1, CardPlacement.Scale(1, dpi)),
        Math.Max(1, CardPlacement.Scale(2, dpi)));
}

// Draws the focus visual round a control. One implementation for the card, its pages and the dialogs.
internal static class FocusVisual
{
    // The corner radii of the controls the card draws, at 96 dpi.
    public const int ControlRadiusAt96 = 4;

    // Strokes the inner and then the outer ring outward from the control's bounds, each concentric with the
    // control's own corners: the inner stroke's centre line sits at gap + inner/2 from the edge, the outer's at
    // gap + inner + outer/2. At 96 dpi that is a radius of control + 1.5 and control + 3.
    public static void Draw(Graphics g, Rectangle control, int controlRadius, int dpi, FocusPalette palette)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(palette);
        if (control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        FocusMetrics m = FocusMetrics.For(dpi);
        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Ring(g, control, controlRadius, m.Gap + (m.InnerWidth / 2f), m.InnerWidth, palette.InnerStroke);
            Ring(g, control, controlRadius, m.Gap + m.InnerWidth + (m.OuterWidth / 2f), m.OuterWidth, palette.OuterStroke);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void Ring(Graphics g, Rectangle control, int controlRadius, float centreOffset, int width, Color colour)
    {
        var rect = new RectangleF(control.X - centreOffset, control.Y - centreOffset, control.Width + (2 * centreOffset), control.Height + (2 * centreOffset));
        using GraphicsPath path = CardPaint.RoundedRectangle(rect, controlRadius + centreOffset);
        using var pen = new Pen(colour, width);
        g.DrawPath(pen, path);
    }
}

// Whether the focus visual is showing. It follows the Windows 11 rule: the visual is for keyboard use. It is
// showing when the card was opened from the keyboard, and from the first Tab, Shift+Tab, arrow, Home or End key;
// a mouse press on the card hides it. Enter and Space use the focused control and leave it as it is.
internal sealed class KeyboardFocusCue(bool openedByKeyboard = false)
{
    public bool Visible { get; private set; } = openedByKeyboard;

    // Starts again for a card that has just been opened.
    public void Reset(bool openedByKeyboard) => Visible = openedByKeyboard;

    public void KeyDown(Keys key)
    {
        if ((key & Keys.KeyCode) is Keys.Tab or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End)
        {
            Visible = true;
        }
    }

    public void MouseDown() => Visible = false;
}
