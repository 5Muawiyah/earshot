using Earshot.Popup;

namespace Earshot.Widget;

// Pure row layout for the widget card. No window, no drawing. widget-ui.md section 7.1, 12.1.
//
// Width 320 at 96 DPI, padding 16: three columns (Left, Right, Case), the where line, the read line,
// then the Connect/Disconnect button and, only when the snapshot says auto-pause is available, the
// switch row beneath it.
internal static class WidgetCardLayout
{
    public const int WidthAt96 = 320;
    public const int PaddingAt96 = 16;
    public const int ColumnGapAt96 = 8;
    public const int RowGapAt96 = 10;
    public const int ColumnGlyphSizeAt96 = 28;
    public const int ColumnBarWidthAt96 = 56;
    public const int ColumnBarHeightAt96 = 6;
    public const int ColumnGapToBarAt96 = 4;
    public const int TextLineHeightAt96 = 18;
    public const int ButtonHeightAt96 = 32;
    public const int SwitchRowHeightAt96 = 24;

    public static int WidthFor(int dpi) => CardPlacement.Scale(WidthAt96, dpi);

    // One column's parts, in client pixels.
    internal readonly record struct ColumnLayout(Rectangle Glyph, Rectangle Bar, Rectangle Percent);

    // The whole card, in client pixels. Switch is Rectangle.Empty when ShowSwitch is false; nothing
    // reads it then.
    internal sealed record Layout(
        int Width,
        int Height,
        ColumnLayout Left,
        ColumnLayout Right,
        ColumnLayout Case,
        Rectangle WhereLine,
        Rectangle ReadLine,
        Rectangle Button,
        bool ShowSwitch,
        Rectangle Switch);

    public static Layout Compute(int dpi, bool showSwitch)
    {
        int width = WidthFor(dpi);
        int pad = CardPlacement.Scale(PaddingAt96, dpi);
        int colGap = CardPlacement.Scale(ColumnGapAt96, dpi);
        int rowGap = CardPlacement.Scale(RowGapAt96, dpi);
        int glyphSize = CardPlacement.Scale(ColumnGlyphSizeAt96, dpi);
        int barWidth = CardPlacement.Scale(ColumnBarWidthAt96, dpi);
        int barHeight = CardPlacement.Scale(ColumnBarHeightAt96, dpi);
        int gapToBar = CardPlacement.Scale(ColumnGapToBarAt96, dpi);
        int lineHeight = CardPlacement.Scale(TextLineHeightAt96, dpi);
        int buttonHeight = CardPlacement.Scale(ButtonHeightAt96, dpi);
        int switchHeight = CardPlacement.Scale(SwitchRowHeightAt96, dpi);

        int contentWidth = Math.Max(1, width - (2 * pad));
        int colWidth = Math.Max(1, (contentWidth - (2 * colGap)) / 3);

        int y = pad;
        int leftX = pad;
        int midX = leftX + colWidth + colGap;
        int rightX = midX + colWidth + colGap;

        ColumnLayout left = Column(leftX, y, colWidth, glyphSize, barWidth, barHeight, gapToBar, lineHeight);
        ColumnLayout right = Column(midX, y, colWidth, glyphSize, barWidth, barHeight, gapToBar, lineHeight);
        ColumnLayout box = Column(rightX, y, colWidth, glyphSize, barWidth, barHeight, gapToBar, lineHeight);

        int rowsBottom = left.Percent.Bottom;
        int whereY = rowsBottom + rowGap;
        var whereLine = new Rectangle(pad, whereY, contentWidth, lineHeight);
        int readY = whereLine.Bottom;
        var readLine = new Rectangle(pad, readY, contentWidth, lineHeight);
        int buttonY = readLine.Bottom + rowGap;
        var button = new Rectangle(pad, buttonY, contentWidth, buttonHeight);

        Rectangle switchRect = Rectangle.Empty;
        int bottom = button.Bottom + pad;
        if (showSwitch)
        {
            switchRect = new Rectangle(pad, button.Bottom + rowGap, contentWidth, switchHeight);
            bottom = switchRect.Bottom + pad;
        }

        return new Layout(width, bottom, left, right, box, whereLine, readLine, button, showSwitch, switchRect);
    }

    private static ColumnLayout Column(int x, int y, int colWidth, int glyphSize, int barWidth, int barHeight, int gapToBar, int lineHeight)
    {
        var glyph = new Rectangle(x + ((colWidth - glyphSize) / 2), y, glyphSize, glyphSize);
        var bar = new Rectangle(x + ((colWidth - barWidth) / 2), glyph.Bottom + gapToBar, barWidth, barHeight);
        var percent = new Rectangle(x, bar.Bottom + gapToBar, colWidth, lineHeight);
        return new ColumnLayout(glyph, bar, percent);
    }
}
